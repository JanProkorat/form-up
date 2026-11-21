using System.Net;
using System.Net.Http.Json;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;

namespace FormUp.Tests.Endpoints.Client.Notifications;

/// <summary>
/// <c>GET /client/notifications</c>: the unread count for clients and coaches, and the
/// InvitationReceived notification a coach's invite to an existing client persists.
/// </summary>
[Collection(TestCollection.Shard4)]
public class GetNotificationsUnreadCountTests(FitnessApiFactory factory)
{
    private record NotificationsResult(List<NotificationItem> Items, int UnreadCount);

    private record NotificationItem(string Type, bool Read, string? ActionPayload);

    private record CreatedInvite(Guid PublicId);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<NotificationsResult> GetAsync(Actor caller)
    {
        var response = await caller.Http.GetAsync("/client/notifications", Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<NotificationsResult>(cancellationToken: Ct))!;
    }

    [Fact]
    public async Task InviteToExistingClient_PersistsInvitationReceivedWithInviteId_AndCountsAsUnread()
    {
        var coach = await TestActors.Trainer(factory).WithName("Coach", "Carl").CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);

        var created = await coach.Http.PostAsJsonAsync(
            "/trainer/pending-invites", new { Email = client.Email, Message = "Hello" }, Ct);
        created.StatusCode.Should().Be(HttpStatusCode.OK);
        var inviteId = (await created.Content.ReadFromJsonAsync<CreatedInvite>(cancellationToken: Ct))!.PublicId;

        var result = await GetAsync(client);

        var notification = result.Items.Should().ContainSingle(n => n.Type == "invitationreceived").Subject;
        notification.ActionPayload.Should().Contain(inviteId.ToString());
        result.UnreadCount.Should().Be(1);
    }

    [Fact]
    public async Task UnreadCount_IsAvailableToCoaches_AndNotLimitedByPageSize()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        (await GetAsync(coach)).UnreadCount.Should().Be(0);

        for (var i = 0; i < 3; i++)
        {
            var client = await TestActors.Client(factory).CreateAsync(Ct);
            var response = await client.Http.PostAsJsonAsync(
                "/client/requests", new { ProfessionalPublicId = coach.PublicId }, Ct);
            response.StatusCode.Should().Be(HttpStatusCode.Created);
        }

        var limited = await coach.Http.GetAsync("/client/notifications?limit=1", Ct);
        var body = await limited.Content.ReadFromJsonAsync<NotificationsResult>(cancellationToken: Ct);

        body!.Items.Should().HaveCount(1);
        body.UnreadCount.Should().Be(3);
    }
}
