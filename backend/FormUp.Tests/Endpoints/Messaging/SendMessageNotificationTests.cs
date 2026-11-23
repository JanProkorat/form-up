using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Messaging;

/// <summary>
/// The coach-side effects of POST /conversations/{id}/messages: the push and the grouped-email burst. Only
/// a client's message to the coach creates either, no in-app notice row is written, and a failing burst upsert never
/// fails the send.
/// </summary>
[Collection(TestCollection.Shard2)]
public class SendMessageNotificationTests(FitnessApiFactory factory)
{
    private async Task<(Actor Coach, Actor Client, Guid ConversationId)> SeedConversationAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var coach = await TestActors.Trainer(factory).CreateAsync(ct);
        var client = await TestActors.Client(factory).WithName("Petra", "Svobodova").CreateAsync(ct);
        await TestActors.Link(factory, coach, client).CreateAsync(ct);

        var created = await client.Http.PostAsJsonAsync("/conversations", new { ParticipantId = coach.PublicId }, ct);
        created.EnsureSuccessStatusCode();
        var conversation = await created.Content.ReadFromJsonAsync<ConversationResponse>(cancellationToken: ct);

        return (coach, client, conversation!.Id);
    }

    private sealed record ConversationResponse(Guid Id);

    private async Task<int> NewMessageNoticeCountAsync(Guid recipientUserId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.Notifications.CountAsync(
            n => n.RecipientUserId == recipientUserId && n.Type == NotificationType.NewMessage,
            TestContext.Current.CancellationToken);
    }

    private List<FakePushNotificationService.PushCall> PushesTo(Guid userId) =>
        factory.Services.GetRequiredService<FakePushNotificationService>().Calls
            .Where(call => call.UserId == userId).ToList();

    [Fact]
    public async Task Send_ClientWritesToCoach_PushesToTheCoachWithoutAnInAppNotice()
    {
        var (coach, client, conversationId) = await SeedConversationAsync();

        var response = await client.Http.PostAsJsonAsync(
            $"/conversations/{conversationId}/messages", new { Text = "Hello coach" }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await NewMessageNoticeCountAsync(coach.UserId)).Should().Be(0);
        var push = PushesTo(coach.UserId).Should().ContainSingle().Subject;
        var data = push.Data.Should().BeAssignableTo<IDictionary<string, string>>().Subject;
        data["type"].Should().Be(nameof(NotificationType.NewMessage));
        data["conversationId"].Should().Be(conversationId.ToString());
        PushesTo(client.UserId).Should().BeEmpty();
    }

    private async Task<List<PendingNotificationEmail>> PendingEmailsAsync(Guid recipientUserId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.PendingNotificationEmails.AsNoTracking()
            .Where(p => p.RecipientUserId == recipientUserId)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Send_ClientWritesToCoach_OpensOneGroupedEmailBurstForTheConversation()
    {
        var (coach, client, conversationId) = await SeedConversationAsync();

        foreach (var text in new[] { "one", "two", "three" })
        {
            (await client.Http.PostAsJsonAsync(
                $"/conversations/{conversationId}/messages", new { Text = text }, TestContext.Current.CancellationToken))
                .StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var burst = (await PendingEmailsAsync(coach.UserId)).Should().ContainSingle().Subject;
        burst.Event.Should().Be(NotificationEvent.NewMessage);
        burst.GroupId.Should().Be(conversationId);
        burst.DueAt.Should().BeAfter(burst.LastEventAt);
        (await PendingEmailsAsync(client.UserId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Send_CoachWritesToClient_OpensNoEmailBurst()
    {
        var (coach, client, conversationId) = await SeedConversationAsync();

        var response = await coach.Http.PostAsJsonAsync(
            $"/conversations/{conversationId}/messages", new { Text = "Hello client" }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await PendingEmailsAsync(client.UserId)).Should().BeEmpty();
        (await PendingEmailsAsync(coach.UserId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Send_BurstUpsertFails_StillReturns200AndSavesTheMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        var (coach, client, conversationId) = await SeedConversationAsync();
        var blocker = "ck_test_block_" + Guid.NewGuid().ToString("N");

        // A constraint that rejects only this coach's rows makes the upsert fail for real, without touching other tests.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                "ALTER TABLE pending_notification_emails ADD CONSTRAINT " + blocker
                + " CHECK (recipient_user_id <> '" + coach.UserId + "')", ct);
        }

        try
        {
            var response = await client.Http.PostAsJsonAsync(
                $"/conversations/{conversationId}/messages", new { Text = "Hello coach" }, ct);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await PendingEmailsAsync(coach.UserId)).Should().BeEmpty();
            PushesTo(coach.UserId).Should().ContainSingle();
        }
        finally
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE pending_notification_emails DROP CONSTRAINT " + blocker, ct);
        }
    }

    [Fact]
    public async Task Send_CoachWritesToClient_PushesNothingToTheClient()
    {
        var (coach, client, conversationId) = await SeedConversationAsync();

        var response = await coach.Http.PostAsJsonAsync(
            $"/conversations/{conversationId}/messages", new { Text = "Hello client" }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        PushesTo(client.UserId).Should().BeEmpty();
        PushesTo(coach.UserId).Should().BeEmpty();
    }
}
