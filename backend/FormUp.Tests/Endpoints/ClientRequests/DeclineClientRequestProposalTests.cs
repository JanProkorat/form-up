using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static FormUp.Tests.Endpoints.ClientPayments.ClientPaymentFixtures;
using static FormUp.Tests.Endpoints.PaymentSettings.PaymentSettingsFixtures;

namespace FormUp.Tests.Endpoints.ClientRequests;

/// <summary>
/// A client declines a coach's package proposal (POST /client/requests/{id}/decline-proposal). The request ends,
/// the conversation stays open.
/// </summary>
[Collection(TestCollection.Shard2)]
public class DeclineClientRequestProposalTests(FitnessApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private record ConversationItem(Guid Id);

    private record MessagesResult(List<MessageItem> Items);

    private record MessageItem(string Kind, string? EventType, Guid? EventSourceId, string? EventStatus);

    private async Task<(Actor Coach, Actor Client, Guid RequestId)> ProposedAsync()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await SubscribeAsync(factory, coach, canUsePayments: true);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var packageId = await CreatePackageAsync(coach);

        var created = await client.Http.PostAsJsonAsync("/client/requests", new { ProfessionalPublicId = coach.PublicId }, Ct);
        var requestId = (await created.Content.ReadFromJsonAsync<RequestCreated>(Ct))!.PublicId;

        var proposed = await coach.Http.PostAsJsonAsync(
            $"/trainer/client-requests/{requestId}/propose-package", new { PackagePublicId = packageId }, Ct);
        proposed.StatusCode.Should().Be(HttpStatusCode.NoContent);

        return (coach, client, requestId);
    }

    private static Task<HttpResponseMessage> DeclineAsync(Actor client, Guid requestId) =>
        client.Http.PostAsJsonAsync($"/client/requests/{requestId}/decline-proposal", new { }, Ct);

    private async Task<ClientRequest> StoredRequestAsync(Guid requestId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.ClientRequests.AsNoTracking().SingleAsync(r => r.PublicId == requestId, Ct);
    }

    private static async Task<(Guid ConversationId, List<MessageItem> Messages)> ThreadAsync(Actor viewer)
    {
        var conversations = await viewer.Http.GetAsync("/conversations", Ct);
        var id = (await conversations.Content.ReadFromJsonAsync<List<ConversationItem>>(cancellationToken: Ct))!
            .Should().ContainSingle().Subject.Id;

        var response = await viewer.Http.GetAsync($"/conversations/{id}/messages", Ct);
        var body = await response.Content.ReadFromJsonAsync<MessagesResult>(cancellationToken: Ct);

        return (id, body!.Items);
    }

    [Fact]
    public async Task Decline_OpenProposal_EndsTheRequestKeepsTheConversationOpenAndNotifiesTheCoach()
    {
        var (coach, client, requestId) = await ProposedAsync();

        // While the proposal waits, the chat card still reads Pending.
        var (conversationId, whileOpen) = await ThreadAsync(client);
        whileOpen.Should().ContainSingle(m => m.EventType == "Requested").Which.EventStatus.Should().Be("Pending");

        var response = await DeclineAsync(client, requestId);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var stored = await StoredRequestAsync(requestId);
        stored.Status.Should().Be(ClientRequestStatus.ProposalDeclined);
        stored.RespondedAt.Should().NotBeNull();

        var (_, afterwards) = await ThreadAsync(client);
        afterwards.Should().ContainSingle(m => m.EventType == "Requested").Which.EventStatus.Should().Be("Declined");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var conversation = await db.Conversations.SingleAsync(c => c.PublicId == conversationId, Ct);
            conversation.ArchivedByClientAt.Should().BeNull();
            conversation.ArchivedByProfessionalAt.Should().BeNull();

            var declined = await db.ChatMessages.SingleAsync(
                m => m.EventSourceId == requestId && m.EventType == ChatEventType.Declined, Ct);
            declined.SenderUserId.Should().Be(client.UserId);

            var notification = await db.Notifications.SingleAsync(
                n => n.RecipientUserId == coach.UserId && n.Type == NotificationType.ClientRequestProposalDeclined, Ct);
            JsonDocument.Parse(notification.Data!).RootElement.GetProperty("requestId").GetString().Should().Be(requestId.ToString());
        }

        factory.Services.GetRequiredService<FakeRealtimeNotifier>().Calls.Should().Contain(
            call => call.UserId == coach.UserId && call.EventType == "clientrequestproposaldeclined");

        // The coach can still write to the client.
        var message = await coach.Http.PostAsJsonAsync($"/conversations/{conversationId}/messages", new { Text = "No problem" }, Ct);
        message.IsSuccessStatusCode.Should().BeTrue();
    }

    [Fact]
    public async Task Decline_AfterwardsTheClientCanSendANewRequestToTheSameCoach()
    {
        var (coach, client, requestId) = await ProposedAsync();
        (await DeclineAsync(client, requestId)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var again = await client.Http.PostAsJsonAsync("/client/requests", new { ProfessionalPublicId = coach.PublicId }, Ct);

        again.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Decline_WhenNoConversationExists_DoesNotCreateOne()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var requestId = Guid.NewGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.ClientRequests.Add(new ClientRequest
            {
                PublicId = requestId,
                ClientProfileId = client.ProfileId,
                ProfessionalProfileId = coach.ProfileId,
                Status = ClientRequestStatus.PackageProposed,
            });
            await db.SaveChangesAsync(Ct);
        }

        var response = await DeclineAsync(client, requestId);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var checkScope = factory.Services.CreateScope();
        var checkDb = checkScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await checkDb.Conversations.AnyAsync(c => c.ClientUserId == client.UserId && c.ProfessionalUserId == coach.UserId, Ct))
            .Should().BeFalse();
    }

    [Fact]
    public async Task Decline_UnknownRequestOrAnotherClientsRequest_Returns404()
    {
        var (_, _, requestId) = await ProposedAsync();
        var otherClient = await TestActors.Client(factory).CreateAsync(Ct);

        var unknown = await DeclineAsync(otherClient, Guid.NewGuid());
        var foreign = await DeclineAsync(otherClient, requestId);

        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ProblemCodeAsync(unknown)).Should().Be("CLIENT_REQUEST_NOT_FOUND");
        foreign.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ProblemCodeAsync(foreign)).Should().Be("CLIENT_REQUEST_NOT_FOUND");
        (await StoredRequestAsync(requestId)).Status.Should().Be(ClientRequestStatus.PackageProposed);
    }

    [Fact]
    public async Task Decline_RequestWithoutOpenProposal_Returns409InvalidState()
    {
        var (_, client, requestId) = await ProposedAsync();
        (await DeclineAsync(client, requestId)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var second = await DeclineAsync(client, requestId);

        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ProblemCodeAsync(second)).Should().Be("CLIENT_REQUEST_INVALID_STATE");
    }

    [Fact]
    public async Task Cancel_AndReject_StillRefuseARequestWithAnOpenProposal()
    {
        var (coach, client, requestId) = await ProposedAsync();

        var cancel = await client.Http.DeleteAsync($"/client/requests/{requestId}", Ct);
        var reject = await coach.Http.PostAsJsonAsync($"/trainer/client-requests/{requestId}/reject", new { }, Ct);

        cancel.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        reject.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCodesAsync(cancel)).Should().Contain("CLIENT_REQUEST_NOT_FOUND");
        (await ErrorCodesAsync(reject)).Should().Contain("CLIENT_REQUEST_NOT_FOUND");
        (await StoredRequestAsync(requestId)).Status.Should().Be(ClientRequestStatus.PackageProposed);
    }

    private record RequestCreated(Guid PublicId);
}
