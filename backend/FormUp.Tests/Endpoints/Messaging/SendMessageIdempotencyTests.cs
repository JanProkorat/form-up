using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Messaging;

/// <summary>
/// Retry safety of POST /conversations/{id}/messages: a repeated ClientMessageId from the same sender
/// returns the original message instead of creating, notifying or unarchiving again.
/// </summary>
[Collection(TestCollection.Name)]
public class SendMessageIdempotencyTests(FitnessApiFactory factory)
{
    private sealed record ConversationResponse(Guid Id);

    private sealed record SentMessage(Guid Id, Guid SenderId, string Text, Guid? ClientMessageId);

    private async Task<(Actor Coach, Actor Client, Guid ConversationId)> SeedConversationAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var coach = await TestActors.Trainer(factory).CreateAsync(ct);
        var client = await TestActors.Client(factory).CreateAsync(ct);
        await TestActors.Link(factory, coach, client).CreateAsync(ct);

        var created = await client.Http.PostAsJsonAsync("/conversations", new { ParticipantId = coach.PublicId }, ct);
        created.EnsureSuccessStatusCode();
        var conversation = await created.Content.ReadFromJsonAsync<ConversationResponse>(cancellationToken: ct);

        return (coach, client, conversation!.Id);
    }

    private static Task<HttpResponseMessage> SendAsync(Actor sender, Guid conversationId, string text, Guid? clientMessageId) =>
        sender.Http.PostAsJsonAsync(
            $"/conversations/{conversationId}/messages",
            new { Text = text, ClientMessageId = clientMessageId },
            TestContext.Current.CancellationToken);

    private static async Task<SentMessage> ReadAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<SentMessage>(cancellationToken: TestContext.Current.CancellationToken))!;
    }

    private async Task<List<ChatMessage>> MessagesAsync(Guid conversationId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.ChatMessages.AsNoTracking()
            .Where(m => m.Conversation.PublicId == conversationId)
            .OrderBy(m => m.Id)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    private int PushCountTo(Guid userId) =>
        factory.Services.GetRequiredService<FakePushNotificationService>().Calls.Count(call => call.UserId == userId);

    [Fact]
    public async Task Send_SameClientMessageIdWithinWindow_ReturnsOriginalWithoutASecondRowOrPush()
    {
        var (coach, client, conversationId) = await SeedConversationAsync();
        var clientMessageId = Guid.NewGuid();

        var first = await ReadAsync(await SendAsync(client, conversationId, "Hello coach", clientMessageId));
        var second = await ReadAsync(await SendAsync(client, conversationId, "Hello coach", clientMessageId));

        second.Id.Should().Be(first.Id);
        second.ClientMessageId.Should().Be(clientMessageId);
        (await MessagesAsync(conversationId)).Should().ContainSingle();
        PushCountTo(coach.UserId).Should().Be(1);
    }

    [Fact]
    public async Task Send_SameClientMessageIdAfterWindow_CreatesNewMessageAndReleasesTheOldId()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, client, conversationId) = await SeedConversationAsync();
        var clientMessageId = Guid.NewGuid();

        var first = await ReadAsync(await SendAsync(client, conversationId, "Hello coach", clientMessageId));

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.ChatMessages
                .Where(m => m.PublicId == first.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.DateCreated, DateTime.UtcNow.AddHours(-25)), ct);
        }

        var second = await ReadAsync(await SendAsync(client, conversationId, "Hello coach", clientMessageId));

        second.Id.Should().NotBe(first.Id);
        var rows = await MessagesAsync(conversationId);
        rows.Should().HaveCount(2);
        rows.Single(m => m.PublicId == first.Id).ClientMessageId.Should().BeNull();
        rows.Single(m => m.PublicId == second.Id).ClientMessageId.Should().Be(clientMessageId);
    }

    [Fact]
    public async Task Send_TwoConcurrentSendsWithTheSameClientMessageId_BothSucceedWithOneRow()
    {
        var (coach, client, conversationId) = await SeedConversationAsync();
        var clientMessageId = Guid.NewGuid();

        var responses = await Task.WhenAll(
            SendAsync(client, conversationId, "Race", clientMessageId),
            SendAsync(client, conversationId, "Race", clientMessageId));

        var first = await ReadAsync(responses[0]);
        var second = await ReadAsync(responses[1]);

        second.Id.Should().Be(first.Id);
        (await MessagesAsync(conversationId)).Should().ContainSingle();
        PushCountTo(coach.UserId).Should().Be(1);
    }

    /// <summary>
    /// Mechanism test for the send race: a held, uncommitted row with the same client id is invisible to
    /// the replay lookup (read committed), so the send's insert blocks on the unique index. Once the
    /// holder commits, the insert fails with 23505 and the endpoint must answer with the winner's row.
    /// Deterministic: the lock wait is polled with a deadline, not slept on.
    /// </summary>
    [Fact]
    public async Task Send_InsertBlockedByAnUncommittedRowWithTheSameClientMessageId_ReturnsTheWinnerAfterItCommits()
    {
        var ct = TestContext.Current.CancellationToken;
        var (coach, client, conversationId) = await SeedConversationAsync();
        var clientMessageId = Guid.NewGuid();

        long conversationDbId;
        using (var lookupScope = factory.Services.CreateScope())
        {
            var lookupDb = lookupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            conversationDbId = await lookupDb.Conversations.AsNoTracking()
                .Where(c => c.PublicId == conversationId)
                .Select(c => c.Id)
                .SingleAsync(ct);
        }

        using var holderScope = factory.Services.CreateScope();
        var holderDb = holderScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await using var holderTransaction = await holderDb.Database.BeginTransactionAsync(ct);

        var heldMessage = new ChatMessage
        {
            ConversationId = conversationDbId,
            SenderUserId = client.UserId,
            ClientMessageId = clientMessageId,
            Text = "Held winner",
        };
        holderDb.ChatMessages.Add(heldMessage);
        await holderDb.SaveChangesAsync(ct);

        var send = SendAsync(client, conversationId, "Losing send", clientMessageId);

        await WaitForABackendBlockedOnAChatMessageInsertAsync(send, ct);
        await holderTransaction.CommitAsync(ct);

        var response = await ReadAsync(await send);

        response.Id.Should().Be(heldMessage.PublicId);
        response.Text.Should().Be("Held winner");
        var rows = await MessagesAsync(conversationId);
        rows.Should().ContainSingle(m => m.ClientMessageId == clientMessageId);
        rows.Should().ContainSingle();
        PushCountTo(coach.UserId).Should().Be(0, "the losing send must not notify for a message it did not create");
    }

    /// <summary>
    /// Polls pg_stat_activity until a backend is waiting on a lock while running a chat_messages
    /// statement. Fails with a clear message if the send finishes first or the deadline passes.
    /// </summary>
    private async Task WaitForABackendBlockedOnAChatMessageInsertAsync(Task<HttpResponseMessage> send, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);

        using var probeScope = factory.Services.CreateScope();
        var probeDb = probeScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        while (true)
        {
            send.IsCompleted.Should().BeFalse(
                "the send must still be blocked on the held row's unique index entry, but it already finished");

            var blocked = await probeDb.Database.SqlQuery<int>($"""
                SELECT count(*)::int AS "Value"
                FROM pg_stat_activity
                WHERE datname = current_database()
                  AND pid <> pg_backend_pid()
                  AND wait_event_type = 'Lock'
                  AND query ILIKE '%chat_messages%'
                """).SingleAsync(ct);

            if (blocked > 0)
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(
                    "No backend was waiting on a lock for a chat_messages statement within 30 s; "
                    + "the send never reached the unique-index insert.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), ct);
        }
    }

    [Fact]
    public async Task Send_OtherParticipantUsesTheSameClientMessageId_CreatesASeparateMessage()
    {
        var (coach, client, conversationId) = await SeedConversationAsync();
        var clientMessageId = Guid.NewGuid();

        var fromClient = await ReadAsync(await SendAsync(client, conversationId, "From client", clientMessageId));
        var fromCoach = await ReadAsync(await SendAsync(coach, conversationId, "From coach", clientMessageId));

        fromCoach.Id.Should().NotBe(fromClient.Id);
        fromCoach.SenderId.Should().Be(coach.UserId);
        fromCoach.Text.Should().Be("From coach");
        (await MessagesAsync(conversationId)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Send_WithoutClientMessageId_AlwaysInserts()
    {
        var (_, client, conversationId) = await SeedConversationAsync();

        var first = await ReadAsync(await SendAsync(client, conversationId, "Same text", null));
        var second = await ReadAsync(await SendAsync(client, conversationId, "Same text", null));

        second.Id.Should().NotBe(first.Id);
        first.ClientMessageId.Should().BeNull();
        (await MessagesAsync(conversationId)).Should().HaveCount(2);
    }
}
