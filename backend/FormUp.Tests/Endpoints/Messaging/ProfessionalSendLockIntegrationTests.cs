using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.Messaging.Shared;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Services;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Messaging;

/// <summary>
/// A professional may message a client with a live link, an ended link or a pending join
/// request; only a thread with no link ever (an invite-only thread) is locked. Only a live
/// link un-archives the thread for the client.
/// </summary>
[Collection(TestCollection.Shard3)]
public class ProfessionalSendLockIntegrationTests(FitnessApiFactory factory)
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    public enum Relation { InviteOnly, LiveLink, EndedLink, PendingRequest, DeclinedRequest }

    private async Task<(Actor Trainer, Actor Client, Guid ConversationId)> SetupAsync(
        Relation relation, bool clientArchived = false)
    {
        var trainer = await TestActors.Trainer(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        switch (relation)
        {
            case Relation.LiveLink:
                await TestActors.Link(factory, trainer, client).CreateAsync(Ct);
                break;
            case Relation.EndedLink:
                await TestActors.Link(factory, trainer, client).Inactive().CreateAsync(Ct);
                break;
            case Relation.PendingRequest:
            case Relation.DeclinedRequest:
                db.ClientRequests.Add(new ClientRequest
                {
                    ClientProfileId = client.ProfileId,
                    ProfessionalProfileId = trainer.ProfileId,
                    Status = relation == Relation.PendingRequest
                        ? ClientRequestStatus.Pending
                        : ClientRequestStatus.Rejected,
                });
                break;
        }

        var conversation = new Conversation
        {
            ProfessionalUserId = trainer.UserId,
            ClientUserId = client.UserId,
            ArchivedByClientAt = clientArchived ? DateTime.UtcNow.AddDays(-1) : null,
        };
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync(Ct);

        return (trainer, client, conversation.PublicId);
    }

    private async Task<int> MessageCountAsync(Guid conversationId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.ChatMessages.CountAsync(m => m.Conversation.PublicId == conversationId, Ct);
    }

    private async Task<DateTime?> ClientArchivedAtAsync(Guid conversationId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Conversations.AsNoTracking()
            .Where(c => c.PublicId == conversationId)
            .Select(c => c.ArchivedByClientAt)
            .SingleAsync(Ct);
    }

    private static async Task AssertLockedAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDto>(Ct);
        problem!.ErrorCode.Should().Be("CONVERSATION_LOCKED");
    }

    [Fact]
    public async Task SendMessage_ProfessionalInInviteOnlyThread_Returns403Locked_NoMessageStored()
    {
        var (trainer, _, conversationId) = await SetupAsync(Relation.InviteOnly);

        var response = await trainer.Http.PostAsJsonAsync(
            $"/conversations/{conversationId}/messages", new { Text = "hello" }, Ct);

        await AssertLockedAsync(response);
        (await MessageCountAsync(conversationId)).Should().Be(0);
    }

    [Fact]
    public async Task SendMessage_InviteOnlyThreadAfterInviteWithdrawn_StaysLocked()
    {
        // Withdrawing or declining an invite never creates a link, so the relation is unchanged.
        var (trainer, _, conversationId) = await SetupAsync(Relation.InviteOnly);

        var first = await trainer.Http.PostAsJsonAsync(
            $"/conversations/{conversationId}/messages", new { Text = "one" }, Ct);
        var second = await trainer.Http.PostAsJsonAsync(
            $"/conversations/{conversationId}/messages", new { Text = "two" }, Ct);

        await AssertLockedAsync(first);
        await AssertLockedAsync(second);
    }

    [Fact]
    public async Task ImageUploadUrl_ProfessionalInInviteOnlyThread_Returns403Locked()
    {
        var (trainer, _, conversationId) = await SetupAsync(Relation.InviteOnly);

        var response = await trainer.Http.PostAsJsonAsync(
            $"/conversations/{conversationId}/messages/image-upload-url",
            new { ContentType = "image/jpeg", SizeBytes = 1024 }, Ct);

        await AssertLockedAsync(response);
    }

    [Fact]
    public async Task ImageUploadUrl_ProfessionalWithEndedLink_Returns200()
    {
        var (trainer, _, conversationId) = await SetupAsync(Relation.EndedLink);

        var response = await trainer.Http.PostAsJsonAsync(
            $"/conversations/{conversationId}/messages/image-upload-url",
            new { ContentType = "image/jpeg", SizeBytes = 1024 }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SendMessage_ClientInInviteOnlyThread_Returns200()
    {
        var (_, client, conversationId) = await SetupAsync(Relation.InviteOnly);

        var response = await client.Http.PostAsJsonAsync(
            $"/conversations/{conversationId}/messages", new { Text = "hi coach" }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await MessageCountAsync(conversationId)).Should().Be(1);
    }

    [Fact]
    public async Task SendMessage_ProfessionalWithLiveLink_SendsAndUnarchivesForClient()
    {
        var (trainer, _, conversationId) = await SetupAsync(Relation.LiveLink, clientArchived: true);

        var response = await trainer.Http.PostAsJsonAsync(
            $"/conversations/{conversationId}/messages", new { Text = "welcome back" }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ClientArchivedAtAsync(conversationId)).Should().BeNull();
    }

    [Fact]
    public async Task SendMessage_ProfessionalWithEndedLink_SendsButDoesNotUnarchive()
    {
        var (trainer, _, conversationId) = await SetupAsync(Relation.EndedLink, clientArchived: true);

        var response = await trainer.Http.PostAsJsonAsync(
            $"/conversations/{conversationId}/messages", new { Text = "how are you" }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await MessageCountAsync(conversationId)).Should().Be(1);
        (await ClientArchivedAtAsync(conversationId)).Should().NotBeNull();
    }

    [Fact]
    public async Task SendMessage_ProfessionalWithPendingRequest_SendsButDoesNotUnarchive()
    {
        var (trainer, _, conversationId) = await SetupAsync(Relation.PendingRequest, clientArchived: true);

        var response = await trainer.Http.PostAsJsonAsync(
            $"/conversations/{conversationId}/messages", new { Text = "got your request" }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await MessageCountAsync(conversationId)).Should().Be(1);
        (await ClientArchivedAtAsync(conversationId)).Should().NotBeNull();
    }

    [Fact]
    public async Task SendMessage_ProfessionalAfterDecliningClientRequest_SendsButDoesNotUnarchive()
    {
        var (trainer, _, conversationId) = await SetupAsync(Relation.DeclinedRequest, clientArchived: true);

        var response = await trainer.Http.PostAsJsonAsync(
            $"/conversations/{conversationId}/messages", new { Text = "sorry, I am full" }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await MessageCountAsync(conversationId)).Should().Be(1);
        (await ClientArchivedAtAsync(conversationId)).Should().NotBeNull();
    }

    [Theory]
    [InlineData(Relation.DeclinedRequest, false)]
    [InlineData(Relation.InviteOnly, true)]
    [InlineData(Relation.EndedLink, false)]
    [InlineData(Relation.PendingRequest, false)]
    [InlineData(Relation.LiveLink, false)]
    public async Task GetConversations_IsSendLocked_TrueExactlyForInviteOnlyThread(Relation relation, bool expectedLocked)
    {
        var (trainer, client, conversationId) = await SetupAsync(relation);

        var response = await trainer.Http.GetAsync("/conversations", Ct);
        response.EnsureSuccessStatusCode();
        var rows = await response.Content.ReadFromJsonAsync<List<ConversationDto>>(Ct);

        rows!.Single(c => c.Id == conversationId).IsSendLocked.Should().Be(expectedLocked);
        client.UserId.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData(Relation.InviteOnly, false)]
    [InlineData(Relation.EndedLink, true)]
    [InlineData(Relation.DeclinedRequest, true)]
    public async Task GetConversations_OnlineInvitee_IsShownOnlineOnlyOutsideInviteOnlyThreads(Relation relation, bool expectedOnline)
    {
        var (trainer, client, conversationId) = await SetupAsync(relation);
        var presence = factory.Services.GetRequiredService<PresenceTracker>();
        presence.UserConnected(client.UserId.ToString());

        try
        {
            var response = await trainer.Http.GetAsync("/conversations", Ct);
            response.EnsureSuccessStatusCode();
            var rows = await response.Content.ReadFromJsonAsync<List<ConversationDto>>(Ct);

            rows!.Single(c => c.Id == conversationId).Participant.Online.Should().Be(expectedOnline);
        }
        finally
        {
            presence.UserDisconnected(client.UserId.ToString());
        }
    }

    [Fact]
    public async Task GetConversations_ClientCaller_IsNeverSendLocked()
    {
        var (_, client, conversationId) = await SetupAsync(Relation.InviteOnly);

        var response = await client.Http.GetAsync("/conversations", Ct);
        var rows = await response.Content.ReadFromJsonAsync<List<ConversationDto>>(Ct);

        rows!.Single(c => c.Id == conversationId).IsSendLocked.Should().BeFalse();
    }

    [Fact]
    public async Task StartConversation_ProfessionalReopeningInviteOnlyThread_ReportsLocked()
    {
        var (trainer, client, conversationId) = await SetupAsync(Relation.InviteOnly);

        var response = await trainer.Http.PostAsJsonAsync("/conversations", new { ParticipantId = client.PublicId }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ConversationDto>(Ct);
        body!.Id.Should().Be(conversationId);
        body.IsSendLocked.Should().BeTrue();
    }

    private sealed class ProblemDto
    {
        public string? ErrorCode { get; set; }
    }
}
