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
