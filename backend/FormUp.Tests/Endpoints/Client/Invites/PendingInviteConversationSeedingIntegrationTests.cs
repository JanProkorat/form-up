using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using FormUp.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Client.Invites;

/// <summary>
/// End-to-end (real Postgres via Testcontainers) coverage for the email-verification-time
/// conversation seed (#803/#817, moved from RegisterEndpoint to VerifyEmailEndpoint for
/// #1100 — R4: invite threads exist only for VERIFIED accounts): a prospective client
/// invited before they have an account must see the coach's Invited cooperation event (and
/// opening message, if any) in Messages as soon as they verify their email — not only after
/// they accept the invite, and never while the account is still unverified.
/// </summary>
/// <remarks>
/// Root cause: <c>Conversation</c> is keyed on (ProfessionalUserId, ClientUserId) — both real
/// ApplicationUser ids — so it cannot be seeded at invite-creation time for a prospective
/// client with no account yet. <c>PendingInviteConversationSeeder</c> closes the gap at the
/// earliest VERIFIED seam (email verification), called from VerifyEmailEndpoint /
/// GoogleSocialLoginEndpoint / AppleSocialLoginEndpoint.
/// </remarks>
[Collection(TestCollection.Shard3)]
public class PendingInviteConversationSeedingIntegrationTests(FitnessApiFactory factory)
{
    // Per-host singleton (#726 refinement) — resolved from this factory's own DI
    // container so assertions never see another factory's zombie worker traffic.
    private FakeEmailService EmailService => factory.Services.GetRequiredService<FakeEmailService>();

    private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.com";

    /// <summary>
    /// Registers a trainer, logs in, and creates a pending invite for <paramref name="clientEmail"/>
    /// with the given message (or none). Returns the created invite's PublicId.
    /// </summary>
    private async Task<Guid> CreateTrainerInviteAsync(HttpClient trainerClient, string clientEmail, string? message)
    {
        var trainerEmail = UniqueEmail("trainer");
        await TestHelpers.RegisterAsync(trainerClient, trainerEmail, "TestPass1!", "Coach", "Carl", "Trainer");
        var (trainerAccessToken, _) = await TestHelpers.LoginAsync(trainerClient, trainerEmail, "TestPass1!");
        TestHelpers.SetBearerToken(trainerClient, trainerAccessToken);

        var inviteResponse = await trainerClient.PostAsJsonAsync("/trainer/pending-invites", new
        {
            FirstName = "Prospective",
            LastName = "Client",
            Email = clientEmail,
            Message = message
        }, cancellationToken: TestContext.Current.CancellationToken);

        inviteResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var invite = await inviteResponse.Content.ReadFromJsonAsync<CreatePendingInviteResult>(
            cancellationToken: TestContext.Current.CancellationToken);
        return invite!.PublicId;
    }

    /// <summary>
    /// Registers the client and verifies their email via the real <c>/auth/verify-email</c>
    /// endpoint, using the token the (fake) email service captured for
    /// <paramref name="clientEmail"/> during registration. This is the seam under test.
    /// </summary>
    private async Task RegisterAndVerifyAsync(HttpClient clientClient, string clientEmail)
    {
        var registerResponse = await TestHelpers.RegisterAsync(
            clientClient, clientEmail, "TestPass1!", "Prospective", "Client", "Client");
        registerResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var verification = EmailService.SentVerifications.Should().ContainSingle(
                v => v.Email == clientEmail)
            .Subject;

        var verifyResponse = await clientClient.PostAsJsonAsync(
            "/auth/verify-email", new { Token = verification.Token },
            cancellationToken: TestContext.Current.CancellationToken);
        verifyResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task VerifyEmail_WithMessageBearingPendingInvite_ConversationVisibleBeforeAccept()
    {
        var trainerClient = factory.CreateClient();
        var clientEmail = UniqueEmail("client");

        await CreateTrainerInviteAsync(trainerClient, clientEmail, "Welcome aboard, let's get started!");

        var clientClient = factory.CreateClient();
        await RegisterAndVerifyAsync(clientClient, clientEmail);

        var (clientAccessToken, _) = await TestHelpers.LoginAsync(clientClient, clientEmail, "TestPass1!");
        TestHelpers.SetBearerToken(clientClient, clientAccessToken);

        // The conversation — and the coach's opening message — must already be visible,
        // BEFORE the client has accepted or declined the invite.
        var conversationsResponse = await clientClient.GetAsync("/conversations", TestContext.Current.CancellationToken);
        conversationsResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var conversations = await conversationsResponse.Content.ReadFromJsonAsync<List<ConversationResult>>(
            cancellationToken: TestContext.Current.CancellationToken);

        conversations.Should().ContainSingle();
        conversations![0].LastMessage.Should().Be("Welcome aboard, let's get started!");
        conversations[0].Participant.Name.Should().Be("Coach Carl");
    }

    /// <summary>
    /// R4: invite threads exist only for VERIFIED accounts. Registering alone (no email
    /// verification yet) must not seed anything — this is the behavior the old #803/#817
    /// seed-at-RegisterEndpoint test used to prove; it now proves the opposite, since the
    /// seed moved to VerifyEmailEndpoint.
    /// </summary>
    [Fact]
    public async Task Register_WithoutVerifyingEmail_DoesNotSeedConversation()
    {
        var trainerClient = factory.CreateClient();
        var clientEmail = UniqueEmail("client-unverified");

        await CreateTrainerInviteAsync(trainerClient, clientEmail, "Welcome aboard, let's get started!");

        var clientClient = factory.CreateClient();
        var registerResponse = await TestHelpers.RegisterAsync(
            clientClient, clientEmail, "TestPass1!", "Prospective", "Client", "Client");
        registerResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var (clientAccessToken, _) = await TestHelpers.LoginAsync(clientClient, clientEmail, "TestPass1!");
        TestHelpers.SetBearerToken(clientClient, clientAccessToken);

        var conversationsResponse = await clientClient.GetAsync("/conversations", TestContext.Current.CancellationToken);
        conversationsResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var conversations = await conversationsResponse.Content.ReadFromJsonAsync<List<ConversationResult>>(
            cancellationToken: TestContext.Current.CancellationToken);

        conversations.Should().BeEmpty("the account is not yet verified — no invite thread must exist");
    }

    /// <summary>
    /// R4 + the maintainer's message-less=yes ruling: a message-less invite still gets its
    /// own thread once the account is verified — the Invited banner alone, with no personal
    /// message beneath it. This used to assert the opposite (no conversation at all) before
    /// #1100; superseded on purpose.
    /// </summary>
    [Fact]
    public async Task VerifyEmail_WithMessagelessPendingInvite_CreatesThreadWithBannerOnly()
    {
        var trainerClient = factory.CreateClient();
        var clientEmail = UniqueEmail("client-nomsg");

        // Invite carries no message at all.
        await CreateTrainerInviteAsync(trainerClient, clientEmail, message: null);

        var clientClient = factory.CreateClient();
        await RegisterAndVerifyAsync(clientClient, clientEmail);

        var (clientAccessToken, _) = await TestHelpers.LoginAsync(clientClient, clientEmail, "TestPass1!");
        TestHelpers.SetBearerToken(clientClient, clientAccessToken);

        var conversationsResponse = await clientClient.GetAsync("/conversations", TestContext.Current.CancellationToken);
        conversationsResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var conversations = await conversationsResponse.Content.ReadFromJsonAsync<List<ConversationResult>>(
            cancellationToken: TestContext.Current.CancellationToken);

        conversations.Should().ContainSingle(
            "a message-less invite still creates a thread now — the Invited banner alone");
        conversations![0].LastMessage.Should().Be(
            "Coach Carl sent an invite to collaborate.",
            "the write-time English fallback line from ChatEventTemplates, absent a personal message");
    }

    [Fact]
    public async Task Accept_AfterVerifyEmailEarlySeed_IsIdempotent_NoDuplicateMessage()
    {
        var trainerClient = factory.CreateClient();
        var clientEmail = UniqueEmail("client-idem");

        await CreateTrainerInviteAsync(trainerClient, clientEmail, "Looking forward to working with you!");

        var clientClient = factory.CreateClient();
        await RegisterAndVerifyAsync(clientClient, clientEmail);
        var (clientAccessToken, _) = await TestHelpers.LoginAsync(clientClient, clientEmail, "TestPass1!");
        TestHelpers.SetBearerToken(clientClient, clientAccessToken);

        // Sanity: the conversation was already seeded at verify-email time.
        var conversationsBeforeAccept = await (await clientClient.GetAsync(
                "/conversations", TestContext.Current.CancellationToken))
            .Content.ReadFromJsonAsync<List<ConversationResult>>(cancellationToken: TestContext.Current.CancellationToken);
        conversationsBeforeAccept.Should().ContainSingle();
        var conversationId = conversationsBeforeAccept![0].Id;

        // Find + accept the pending invite.
        var pendingResponse = await clientClient.GetAsync("/client/invites/pending", TestContext.Current.CancellationToken);
        pendingResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var pending = await pendingResponse.Content.ReadFromJsonAsync<PendingInviteResult>(
            cancellationToken: TestContext.Current.CancellationToken);

        var acceptResponse = await clientClient.PostAsync(
            $"/client/invites/{pending!.Id}/accept",
            new StringContent("{}", Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        acceptResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // The accept path's "ensure Invited" re-check is a no-op once the Invited event (and
        // message) already exist — no duplicate message, still one conversation.
        var conversationsAfterAccept = await (await clientClient.GetAsync(
                "/conversations", TestContext.Current.CancellationToken))
            .Content.ReadFromJsonAsync<List<ConversationResult>>(cancellationToken: TestContext.Current.CancellationToken);
        conversationsAfterAccept.Should().ContainSingle();

        var messagesResponse = await clientClient.GetAsync(
            $"/conversations/{conversationId}/messages", TestContext.Current.CancellationToken);
        messagesResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var messages = await messagesResponse.Content.ReadFromJsonAsync<GetMessagesResult>(
            cancellationToken: TestContext.Current.CancellationToken);

        // Rows now include the Invited event and the Accepted event alongside the message —
        // the read side (GetMessagesEndpoint) does not yet distinguish Kind (that lands in a
        // later commit), so this asserts on occurrence count of the personal message text
        // rather than the total row count.
        messages!.Items.Count(m => m.Text == "Looking forward to working with you!").Should().Be(
            1, "the invite message must be delivered exactly once, not duplicated by the later accept");
    }

    [Fact]
    public async Task Decline_AfterVerifyEmailEarlySeedAndMessageExchange_PreservesConversationHistory()
    {
        var trainerClient = factory.CreateClient();
        var clientEmail = UniqueEmail("client-decline");

        await CreateTrainerInviteAsync(trainerClient, clientEmail, "Hope to hear from you soon.");

        var clientClient = factory.CreateClient();
        await RegisterAndVerifyAsync(clientClient, clientEmail);
        var (clientAccessToken, _) = await TestHelpers.LoginAsync(clientClient, clientEmail, "TestPass1!");
        TestHelpers.SetBearerToken(clientClient, clientAccessToken);

        var conversationsBeforeDecline = await (await clientClient.GetAsync(
                "/conversations", TestContext.Current.CancellationToken))
            .Content.ReadFromJsonAsync<List<ConversationResult>>(cancellationToken: TestContext.Current.CancellationToken);
        conversationsBeforeDecline.Should().ContainSingle();
        var conversationId = conversationsBeforeDecline![0].Id;

        // The client exchanges a message with the coach WHILE the invite is still pending —
        // this is the #803 "chat before deciding" behavior.
        var sendResponse = await clientClient.PostAsJsonAsync(
            $"/conversations/{conversationId}/messages",
            new { ConversationId = conversationId, Text = "Thanks, I have a question before I decide." },
            cancellationToken: TestContext.Current.CancellationToken);
        sendResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Now the client declines the invite.
        var pendingResponse = await clientClient.GetAsync("/client/invites/pending", TestContext.Current.CancellationToken);
        var pending = await pendingResponse.Content.ReadFromJsonAsync<PendingInviteResult>(
            cancellationToken: TestContext.Current.CancellationToken);

        var declineResponse = await clientClient.PostAsync(
            $"/client/invites/{pending!.Id}/decline",
            new StringContent("{}", Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        declineResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Decline must never erase conversation history — the seeded opening message AND the
        // client's own reply must both survive (the decline itself also writes a Declined
        // event row, so LastMessage now reflects that, not the earlier plain-text reply).
        var conversationsAfterDecline = await (await clientClient.GetAsync(
                "/conversations", TestContext.Current.CancellationToken))
            .Content.ReadFromJsonAsync<List<ConversationResult>>(cancellationToken: TestContext.Current.CancellationToken);
        conversationsAfterDecline.Should().ContainSingle();

        var messagesResponse = await clientClient.GetAsync(
            $"/conversations/{conversationId}/messages", TestContext.Current.CancellationToken);
        var messages = await messagesResponse.Content.ReadFromJsonAsync<GetMessagesResult>(
            cancellationToken: TestContext.Current.CancellationToken);

        messages!.Items.Should().Contain(m => m.Text == "Thanks, I have a question before I decide.",
            "the client's own reply, sent before declining, must survive");
    }

    private record CreatePendingInviteResult(Guid PublicId);
    private record PendingInviteResult(string Id);
    private record ConversationResult(Guid Id, ParticipantResult Participant, string LastMessage);
    private record ParticipantResult(Guid Id, string Name);
    private record GetMessagesResult(List<MessageResult> Items, Guid? Cursor);
    private record MessageResult(Guid Id, Guid SenderId, string Text, DateTime Timestamp, bool IsRead);
}
