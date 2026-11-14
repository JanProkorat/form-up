using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Messaging;

/// <summary>
/// A coach whose plan lacks messaging cannot write in chat, however they ask; the client of that coach still can.
/// Reads, mark-read and archive stay open.
/// </summary>
[Collection(TestCollection.Name)]
public class MessagingEntitlementTests(FitnessApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(Actor Coach, Actor Client, Guid ConversationId)> CoachWithoutChatAsync(bool withConversation)
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).CreateAsync(Ct);
        await CoachSubscriptionBuilder.MoveToPlanAsync(factory, coach, plan => plan.CanMessage = false);

        var conversationId = Guid.Empty;

        if (withConversation)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var conversation = new Conversation { ProfessionalUserId = coach.UserId, ClientUserId = client.UserId };
            db.Conversations.Add(conversation);
            await db.SaveChangesAsync(Ct);
            conversationId = conversation.PublicId;
        }

        return (coach, client, conversationId);
    }

    private static async Task AssertSubscriptionRequiredAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.SubscriptionRequired);
    }

    private async Task<int> MessageCountAsync(Guid conversationId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.ChatMessages.CountAsync(message => message.Conversation.PublicId == conversationId, Ct);
    }

    [Fact]
    public async Task SendMessage_CoachWithoutMessaging_Returns403AndStoresNothing()
    {
        var (coach, _, conversationId) = await CoachWithoutChatAsync(withConversation: true);

        var response = await coach.Http.PostAsJsonAsync($"/conversations/{conversationId}/messages", new { Text = "hello" }, Ct);

        await AssertSubscriptionRequiredAsync(response);
        (await MessageCountAsync(conversationId)).Should().Be(0);
    }

    [Fact]
    public async Task SendMessage_ClientOfCoachWithoutMessaging_StillSends()
    {
        var (_, client, conversationId) = await CoachWithoutChatAsync(withConversation: true);

        var response = await client.Http.PostAsJsonAsync($"/conversations/{conversationId}/messages", new { Text = "hello coach" }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await MessageCountAsync(conversationId)).Should().Be(1);
    }

    [Fact]
    public async Task StartConversation_CoachWithoutMessaging_Returns403AndCreatesNothing()
    {
        var (coach, client, _) = await CoachWithoutChatAsync(withConversation: false);

        var response = await coach.Http.PostAsJsonAsync("/conversations", new { ParticipantId = client.PublicId }, Ct);

        await AssertSubscriptionRequiredAsync(response);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.Conversations.AnyAsync(c => c.ProfessionalUserId == coach.UserId, Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task Broadcast_CoachWithoutMessaging_Returns403()
    {
        var (coach, client, _) = await CoachWithoutChatAsync(withConversation: false);

        var response = await coach.Http.PostAsJsonAsync(
            "/trainer/broadcast", new { ClientPublicIds = new[] { client.PublicId }, Text = "hello all" }, Ct);

        await AssertSubscriptionRequiredAsync(response);
    }

    [Fact]
    public async Task ChatImageUploadUrl_CoachWithoutMessaging_Returns403()
    {
        var (coach, _, conversationId) = await CoachWithoutChatAsync(withConversation: true);

        var response = await coach.Http.PostAsJsonAsync(
            $"/conversations/{conversationId}/messages/image-upload-url",
            new { ContentType = "image/png", SizeBytes = 1000 },
            Ct);

        await AssertSubscriptionRequiredAsync(response);
    }

    [Fact]
    public async Task ReadingMessages_CoachWithoutMessaging_StaysOpen()
    {
        var (coach, _, conversationId) = await CoachWithoutChatAsync(withConversation: true);

        var response = await coach.Http.GetAsync($"/conversations/{conversationId}/messages", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
