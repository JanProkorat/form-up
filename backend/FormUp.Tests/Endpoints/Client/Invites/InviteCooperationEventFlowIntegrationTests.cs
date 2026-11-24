using System.Net;
using System.Net.Http.Json;
using System.Text;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Client.Invites;

/// <summary>
/// End-to-end (real Postgres via Testcontainers) coverage for the cooperation-event write
/// seam as exercised through the full HTTP pipeline — not the mocked unit tests in
/// <c>AcceptClientInviteEndpointTests</c> / <c>AcceptInvitationEndpointTests</c> /
/// <c>DeletePendingInviteEndpointTests</c>, and not the seam-in-isolation coverage in
/// <c>ConversationSeedServiceIntegrationTests</c>.
/// </summary>
/// <remarks>
/// What this closes: on the most common accept path (a verified account invited with a
/// message), <c>AcceptClientInviteEndpoint</c>'s first <c>AppendCooperationEventAsync(Invited,
/// …)</c> call is EXPECTED to hit the partial unique index's real Postgres <c>23505</c> — the
/// Invited event (and its message) was already written immediately at invite-creation time
/// (#1100 C3). The endpoint then does more work on the SAME <see cref="IApplicationDbContext"/>
/// afterward (the professional notification, then <c>IAuditService.LogAsync</c>, which itself
/// calls <c>SaveChangesAsync</c> again). A mock-DB unit test can never raise a real <c>23505</c>,
/// so nothing at the unit layer proves this path returns 204 instead of 500 if the internal
/// catch-and-untrack in <c>ConversationSeedService.AppendCooperationEventAsync</c> ever leaves
/// the change tracker in a state a later, unrelated <c>SaveChangesAsync</c> chokes on.
/// </remarks>
[Collection(TestCollection.Shard2)]
public class InviteCooperationEventFlowIntegrationTests(FitnessApiFactory factory)
{
    private FakeEmailService EmailService => factory.Services.GetRequiredService<FakeEmailService>();

    /// <summary>
    /// The immediate invite-time seed (#1100 C3, R4) only fires for a VERIFIED existing account.
    /// TestActors users are verified by default since #1517, so this is a no-op kept to make
    /// that precondition explicit in every scenario.
    /// </summary>
    private async Task MarkEmailConfirmedAsync(Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await db.Users.FirstAsync(u => u.Id == userId, TestContext.Current.CancellationToken);
        user.EmailConfirmed = true;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<Guid> CreateInviteAsync(HttpClient coachHttp, string clientEmail, string? message)
    {
        var response = await coachHttp.PostAsJsonAsync("/trainer/pending-invites", new
        {
            Email = clientEmail,
            Message = message
        }, cancellationToken: TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<CreatePendingInviteResult>(
            cancellationToken: TestContext.Current.CancellationToken);
        return body!.PublicId;
    }

    private async Task<Guid> GetSoleConversationIdAsync(HttpClient http)
    {
        var response = await http.GetAsync("/conversations", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var conversations = await response.Content.ReadFromJsonAsync<List<ConversationResult>>(
            cancellationToken: TestContext.Current.CancellationToken);
        conversations.Should().ContainSingle();
        return conversations![0].Id;
    }

    /// <summary>
    /// Messages ordered chronologically (oldest first) — <c>GetMessagesEndpoint</c> returns them
    /// newest-first for pagination, so the test reverses before asserting write order.
    /// </summary>
    private async Task<List<MessageResult>> GetMessagesChronologicalAsync(HttpClient http, Guid conversationId)
    {
        var response = await http.GetAsync(
            $"/conversations/{conversationId}/messages", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<GetMessagesResult>(
            cancellationToken: TestContext.Current.CancellationToken);
        body!.Items.Reverse();
        return body.Items;
    }

    [Fact]
    public async Task Accept_VerifiedClientWithMessage_WritesInvitedTextAccepted_InChronologicalOrder()
    {
        var coach = await TestActors.Trainer(factory).WithName("Coach", "Carl").CreateAsync();
        var client = await TestActors.Client(factory).WithName("Jane", "Doe").CreateAsync();
        await MarkEmailConfirmedAsync(client.UserId);

        // The Invited event + message are written immediately here (#1100 C3) — the client is
        // already verified.
        var invitePublicId = await CreateInviteAsync(coach.Http, client.Email, "Welcome aboard!");

        var conversationId = await GetSoleConversationIdAsync(client.Http);

        // The accept endpoint's own AppendCooperationEventAsync(Invited, …) call below is
        // EXPECTED to hit the real partial-unique-index 23505 here — the row already exists.
        // What this test proves is that the endpoint still returns 204 and still writes exactly
        // one new Accepted row, despite that internal conflict and the further SaveChangesAsync
        // calls (notification, audit log) that follow on the same DbContext.
        var acceptResponse = await client.Http.PostAsync(
            $"/client/invites/{invitePublicId}/accept",
            new StringContent("{}", Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        acceptResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var messages = await GetMessagesChronologicalAsync(client.Http, conversationId);

        messages.Should().HaveCount(3,
            "exactly one Invited event, one message copy, and one Accepted event — no 23505-triggered duplicate or loss");
        messages[0].SenderId.Should().Be(coach.UserId);
        messages[0].Text.Should().Be("Coach Carl sent an invite to collaborate.");
        messages[1].SenderId.Should().Be(coach.UserId);
        messages[1].Text.Should().Be("Welcome aboard!");
        messages[2].SenderId.Should().Be(client.UserId);
        messages[2].Text.Should().Be("Jane Doe accepted the collaboration.");
    }

    [Fact]
    public async Task Accept_RetriedAfterSuccess_DoesNotDuplicateEvents_AndReturnsGuardStatusNot500()
    {
        var coach = await TestActors.Trainer(factory).WithName("Coach", "Carl").CreateAsync();
        var client = await TestActors.Client(factory).WithName("Jane", "Doe").CreateAsync();
        await MarkEmailConfirmedAsync(client.UserId);

        var invitePublicId = await CreateInviteAsync(coach.Http, client.Email, "Looking forward to it!");
        var conversationId = await GetSoleConversationIdAsync(client.Http);

        var firstAccept = await client.Http.PostAsync(
            $"/client/invites/{invitePublicId}/accept",
            new StringContent("{}", Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        firstAccept.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var retryAccept = await client.Http.PostAsync(
            $"/client/invites/{invitePublicId}/accept",
            new StringContent("{}", Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        // The invite's own Status == Pending lookup filter already 404s a re-processed accept, upstream
        // of any cooperation-event write — pinning the actual observed status rather than a loose
        // "not 500" so a regression that changes this guard is caught explicitly.
        retryAccept.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "the invite's own Status == Pending guard 404s a retried accept before it ever reaches the cooperation-event seam");

        var messages = await GetMessagesChronologicalAsync(client.Http, conversationId);

        messages.Should().HaveCount(3,
            "the retry must not add a second Invited, message, or Accepted row");
        messages.Count(m => m.Text == "Coach Carl sent an invite to collaborate.").Should().Be(1);
        messages.Count(m => m.Text == "Looking forward to it!").Should().Be(1);
        messages.Count(m => m.Text == "Jane Doe accepted the collaboration.").Should().Be(1);
    }

    [Fact]
    public async Task Withdraw_ThenTokenAccept_WritesWithdrawn_AndTokenAcceptFailsWithNoLinkOrAcceptedRow()
    {
        var coach = await TestActors.Trainer(factory).WithName("Coach", "Carl").CreateAsync();
        var client = await TestActors.Client(factory).WithName("Jane", "Doe").CreateAsync();
        await MarkEmailConfirmedAsync(client.UserId);

        var invitePublicId = await CreateInviteAsync(coach.Http, client.Email, "Hope to work with you!");
        var conversationId = await GetSoleConversationIdAsync(client.Http);

        // The invitation email is now sent by the background worker (#1109), not inline in the
        // request — wait for the drain instead of assuming it already landed by the time
        // CreateInviteAsync's HTTP response returned.
        await FakeEmailService.WaitForAsync(() => EmailService.SentInvitations.Any(i => i.Email == client.Email));
        var invitation = EmailService.SentInvitations.Should().ContainSingle(i => i.Email == client.Email).Subject;

        // Coach withdraws the invite in-app — a thread already exists (the invite-time seed),
        // so this must write a neutral Withdrawn event and invalidate the emailed token.
        var deleteResponse = await coach.Http.DeleteAsync(
            $"/trainer/pending-invites/{invitePublicId}", TestContext.Current.CancellationToken);
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var messagesAfterWithdraw = await GetMessagesChronologicalAsync(client.Http, conversationId);
        messagesAfterWithdraw.Should().HaveCount(3);
        messagesAfterWithdraw[2].SenderId.Should().Be(coach.UserId);
        messagesAfterWithdraw[2].Text.Should().Be("Coach Carl withdrew the collaboration.");

        // The invitee's email link must no longer work — DeletePendingInvite marked the
        // matching InvitationToken used.
        var tokenAcceptResponse = await client.Http.PostAsJsonAsync(
            "/auth/invite/accept", new { Token = invitation.Token },
            cancellationToken: TestContext.Current.CancellationToken);
        tokenAcceptResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "the token was invalidated by the withdraw — AcceptInvitationEndpoint's IsUsed guard must reject it");

        var messagesAfterTokenAccept = await GetMessagesChronologicalAsync(client.Http, conversationId);
        messagesAfterTokenAccept.Should().HaveCount(3,
            "the rejected token accept must not add an Accepted row");
        messagesAfterTokenAccept.Should().NotContain(m => m.Text == "Jane Doe accepted the collaboration.");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var linkExists = await db.ClientProfessionalLinks.AnyAsync(
            l => l.ClientProfileId == client.ProfileId && l.ProfessionalProfileId == coach.ProfileId,
            TestContext.Current.CancellationToken);
        linkExists.Should().BeFalse("the rejected token accept must never create a client-professional link");
    }

    /// <summary>
    /// #1109: the invitation email is sent by <c>EmailDispatchWorker</c> off the request path.
    /// An SMTP failure there must never turn a successful invite creation into a 500, or skip
    /// the synchronous work that already happened before the send was enqueued — the saved
    /// invite/token, the notification, and (for a verified client) the chat seed. The worker
    /// must also keep running afterward rather than wedging or crashing.
    /// </summary>
    [Fact]
    public async Task Create_SmtpSendFails_StillReturns200AndSeedsChat_WorkerKeepsRunning()
    {
        var coach = await TestActors.Trainer(factory).WithName("Coach", "Carl").CreateAsync();
        var client = await TestActors.Client(factory).WithName("Jane", "Doe").CreateAsync();
        await MarkEmailConfirmedAsync(client.UserId);

        EmailService.FailInvitationSendFor(client.Email);

        // CreateInviteAsync already asserts 200 OK internally -- the request must succeed even
        // though the background send for this email is guaranteed to throw.
        var invitePublicId = await CreateInviteAsync(coach.Http, client.Email, "Welcome aboard!");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var invite = await db.PendingInvites.FirstOrDefaultAsync(
                pi => pi.PublicId == invitePublicId, TestContext.Current.CancellationToken);
            invite.Should().NotBeNull("the invite must be persisted regardless of the background send outcome");

            var token = await db.InvitationTokens.FirstOrDefaultAsync(
                t => t.Email == client.Email, TestContext.Current.CancellationToken);
            token.Should().NotBeNull("the invitation token must be persisted regardless of the background send outcome");
        }

        var notificationsResponse = await client.Http.GetAsync(
            "/client/notifications", TestContext.Current.CancellationToken);
        notificationsResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            "the in-app notification is written synchronously and does not depend on the email send");

        var conversationId = await GetSoleConversationIdAsync(client.Http);
        var messages = await GetMessagesChronologicalAsync(client.Http, conversationId);
        messages.Should().ContainSingle(m => m.Text == "Coach Carl sent an invite to collaborate.",
            "the #1100 chat seed for a verified client happens synchronously and never depended on the email send");

        var queue = factory.Services.GetRequiredService<IBackgroundEmailQueue>();
        await FakeEmailService.WaitForAsync(() => queue.PendingCount == 0);
        queue.PendingCount.Should().Be(0,
            "the failed send must still be drained (MarkProcessed in a finally) rather than wedging the queue");

        // Prove the worker kept running after the failure, rather than crashing or stalling,
        // by draining a SECOND, unrelated send afterward.
        var secondClient = await TestActors.Client(factory).WithName("Second", "Client").CreateAsync();
        await MarkEmailConfirmedAsync(secondClient.UserId);
        await CreateInviteAsync(coach.Http, secondClient.Email, "You too!");

        await FakeEmailService.WaitForAsync(() => EmailService.SentInvitations.Any(i => i.Email == secondClient.Email));
        EmailService.SentInvitations.Should().Contain(i => i.Email == secondClient.Email,
            "the worker must still be alive and processing new items after a prior send failed");
    }

    private record CreatePendingInviteResult(Guid PublicId);
    private record ConversationResult(Guid Id, ParticipantResult Participant, string LastMessage);
    private record ParticipantResult(Guid Id, string Name);
    private record GetMessagesResult(List<MessageResult> Items, Guid? Cursor);
    private record MessageResult(Guid Id, Guid SenderId, string Text, DateTime Timestamp, bool IsRead);
}
