using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Messaging;

/// <summary>
/// Integration coverage for <c>ConversationDto.LastMessageEventType</c> (#1100 C6, "RULING
/// preview field") across all three builders that populate it: <c>GetConversationsEndpoint</c>'s
/// plain-conversation query (client caller), its live-roster row builder (professional caller),
/// and <c>StartConversationEndpoint.BuildResponse</c>.
/// </summary>
[Collection(TestCollection.Shard3)]
public class ConversationLastMessageEventTypeTests(FitnessApiFactory factory)
{
    // The API serializes enums as strings (JsonStringEnumConverter globally), so use matching
    // client-side options wherever a response includes LastMessageEventType.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public async Task GetConversations_ProfessionalRosterRow_ShowsEventType_ThenNullAfterPlainMessage()
    {
        var coach = await TestActors.Trainer(factory).WithName("Coach", "Carl").CreateAsync();
        var client = await TestActors.Client(factory).WithName("Jane", "Doe").CreateAsync();
        await TestActors.Link(factory, coach, client).CreateAsync();

        using (var scope = factory.Services.CreateScope())
        {
            var seedService = scope.ServiceProvider.GetRequiredService<IConversationSeedService>();
            await seedService.AppendCooperationEventAsync(
                coach.UserId, client.UserId, coach.UserId, ChatEventType.Accepted,
                sourceId: Guid.NewGuid(), messageText: null, createConversationIfMissing: true,
                TestContext.Current.CancellationToken);
        }

        var afterEvent = await GetConversationsAsync(coach.Http, "All");
        var rowAfterEvent = afterEvent.Should().ContainSingle(c => c.Participant.Id == client.UserId).Subject;
        rowAfterEvent.LastMessageEventType.Should().Be(ChatEventType.Accepted,
            "the live-roster row builder must surface the conversation's LastMessageEventType");

        var sendResponse = await coach.Http.PostAsJsonAsync(
            $"/conversations/{rowAfterEvent.Id}/messages", new { Text = "let's get started" },
            TestContext.Current.CancellationToken);
        sendResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var afterMessage = await GetConversationsAsync(coach.Http, "All");
        var rowAfterMessage = afterMessage.Should().ContainSingle(c => c.Participant.Id == client.UserId).Subject;
        rowAfterMessage.LastMessageEventType.Should().BeNull(
            "a plain message must reset the preview back to null — the last message is no longer an event");
    }

    [Fact]
    public async Task GetConversations_ClientCallerPlainQuery_ShowsEventType()
    {
        var coach = await TestActors.Trainer(factory).WithName("Coach", "Carl").CreateAsync();
        var client = await TestActors.Client(factory).WithName("Jane", "Doe").CreateAsync();

        using (var scope = factory.Services.CreateScope())
        {
            var seedService = scope.ServiceProvider.GetRequiredService<IConversationSeedService>();
            await seedService.AppendCooperationEventAsync(
                coach.UserId, client.UserId, client.UserId, ChatEventType.Requested,
                sourceId: Guid.NewGuid(), messageText: null, createConversationIfMissing: true,
                TestContext.Current.CancellationToken);
        }

        var conversations = await GetConversationsAsync(client.Http, filter: null);
        var row = conversations.Should().ContainSingle(c => c.Participant.Id == coach.UserId).Subject;
        row.LastMessageEventType.Should().Be(ChatEventType.Requested,
            "the client caller's plain-conversation query must also surface LastMessageEventType");
    }

    [Fact]
    public async Task StartConversation_ExistingConversationWithEventLast_ReturnsLastMessageEventType()
    {
        var coach = await TestActors.Trainer(factory).WithName("Coach", "Carl").CreateAsync();
        var client = await TestActors.Client(factory).WithName("Jane", "Doe").CreateAsync();

        using (var scope = factory.Services.CreateScope())
        {
            var seedService = scope.ServiceProvider.GetRequiredService<IConversationSeedService>();
            await seedService.AppendCooperationEventAsync(
                coach.UserId, client.UserId, coach.UserId, ChatEventType.Declined,
                sourceId: Guid.NewGuid(), messageText: null, createConversationIfMissing: true,
                TestContext.Current.CancellationToken);
        }

        var response = await client.Http.PostAsJsonAsync(
            "/conversations", new { ParticipantId = coach.PublicId }, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<ConversationResult>(
            JsonOptions, TestContext.Current.CancellationToken);

        body!.LastMessageEventType.Should().Be(ChatEventType.Declined,
            "StartConversationEndpoint.BuildResponse must also surface LastMessageEventType");
    }

    private async Task<List<ConversationResult>> GetConversationsAsync(HttpClient http, string? filter)
    {
        var url = filter is null ? "/conversations" : $"/conversations?filter={filter}";
        var response = await http.GetAsync(url, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<List<ConversationResult>>(
            JsonOptions, TestContext.Current.CancellationToken);
        return body!;
    }

    private record ConversationResult(Guid? Id, ParticipantResult Participant, ChatEventType? LastMessageEventType);
    private record ParticipantResult(Guid Id);
}
