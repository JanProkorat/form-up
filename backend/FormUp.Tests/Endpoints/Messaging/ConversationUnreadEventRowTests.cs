using System.Net;
using System.Net.Http.Json;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;

namespace FormUp.Tests.Endpoints.Messaging;

/// <summary>
/// A cooperation event row never counts as unread, so an invite or request that comes with a
/// message counts once. The four unread computations must agree.
/// </summary>
[Collection(TestCollection.Shard4)]
public class ConversationUnreadEventRowTests(FitnessApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private record FilterCounts(int All, int UnreadMessages);

    private record ClientsBody(List<ClientRow> Clients);

    private record ClientRow(Guid UserId, int UnreadMessageCount);

    private record StartResult(int UnreadCount);

    [Fact]
    public async Task ClientCaller_InviteWithMessageCountsOne_InviteWithoutMessageCountsZero()
    {
        var withMessage = await TestActors.Trainer(factory).CreateAsync(Ct);
        var withoutMessage = await TestActors.Trainer(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await ConversationListSeed.SeedInviteAsync(factory, withMessage, client, message: "Welcome aboard");
        await ConversationListSeed.SeedInviteAsync(factory, withoutMessage, client);

        var rows = await ConversationListSeed.GetAsync(client.Http);

        rows.Single(r => r.Participant.Id == withMessage.UserId).UnreadCount.Should().Be(1);
        rows.Single(r => r.Participant.Id == withoutMessage.UserId).UnreadCount.Should().Be(0);
    }

    [Fact]
    public async Task CoachCaller_RequestWithMessage_CountsOneUnread_InListFilterCountsAndClients()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).CreateAsync(Ct);
        await ConversationListSeed.SeedRequestAsync(factory, coach, client, message: "Please take me on");

        var list = await ConversationListSeed.GetAsync(coach.Http, "?filter=UnreadMessages");
        list.Should().ContainSingle().Which.UnreadCount.Should().Be(1);

        var counts = await coach.Http.GetFromJsonAsync<FilterCounts>(
            "/conversations/filter-counts", ConversationListSeed.JsonOptions, Ct);
        counts!.UnreadMessages.Should().Be(1);

        var clients = await coach.Http.GetFromJsonAsync<ClientsBody>(
            "/trainer/clients", ConversationListSeed.JsonOptions, Ct);
        clients!.Clients.Single(c => c.UserId == client.UserId).UnreadMessageCount.Should().Be(1);
    }

    [Fact]
    public async Task CoachCaller_RequestWithoutMessage_IsNotUnreadAnywhere()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).CreateAsync(Ct);
        await ConversationListSeed.SeedRequestAsync(factory, coach, client);

        (await ConversationListSeed.GetAsync(coach.Http, "?filter=UnreadMessages")).Should().BeEmpty();

        var counts = await coach.Http.GetFromJsonAsync<FilterCounts>(
            "/conversations/filter-counts", ConversationListSeed.JsonOptions, Ct);
        counts!.UnreadMessages.Should().Be(0);

        var clients = await coach.Http.GetFromJsonAsync<ClientsBody>(
            "/trainer/clients", ConversationListSeed.JsonOptions, Ct);
        clients!.Clients.Single(c => c.UserId == client.UserId).UnreadMessageCount.Should().Be(0);
    }

    [Fact]
    public async Task StartConversation_ExistingThreadWithInviteAndMessage_ReturnsOneUnread()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await ConversationListSeed.SeedInviteAsync(factory, coach, client, message: "Welcome aboard");

        var response = await client.Http.PostAsJsonAsync("/conversations", new { ParticipantId = coach.PublicId }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<StartResult>(ConversationListSeed.JsonOptions, Ct))!
            .UnreadCount.Should().Be(1);
    }
}
