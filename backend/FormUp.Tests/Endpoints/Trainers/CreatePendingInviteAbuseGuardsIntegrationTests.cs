using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Trainers;

/// <summary>
/// End-to-end (real Postgres via Testcontainers) coverage for the claude-security F8 fix on
/// POST /trainer/pending-invites: creating an invite for an email that already belongs to a
/// registered user must NOT write the caller's free-text message into that user's chat before
/// they have accepted anything — the conversation seed is deferred to acceptance time.
///
/// The in-app notification is deliberately NOT deferred, and this test pins that too: an invitee
/// who never opens the app would otherwise never learn an invite arrived. The two assertions
/// together are the point — a version that defers both, or neither, fails one of them. A unit
/// test against a mocked DbContext cannot observe either half; only a real read of the invited
/// user's own inbox shows what actually landed there.
/// </summary>
[Collection(TestCollection.Shard4)]
public class CreatePendingInviteAbuseGuardsIntegrationTests(FitnessApiFactory factory)
{
    private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.com";

    [Fact]
    public async Task CreateInvite_ForExistingUserWithMessage_DoesNotSeedConversationButDoesNotify()
    {
        // Arrange: the invited email ALREADY belongs to a registered client — the exact shape
        // the abuse case exploited (no relationship required between the professional and the
        // invited account).
        // Unverified on purpose: a verified account gets its invite thread at once (#1100 C3), and an unverified one
        // cannot read its own inbox over HTTP (#1517), so the assertions below read the database.
        var clientClient = factory.CreateClient();
        var clientEmail = UniqueEmail("existing-client");
        await TestHelpers.RegisterAsync(
            clientClient, clientEmail, "TestPass1!", "Existing", "Client", "Client", confirmEmail: false);

        var trainerClient = factory.CreateClient();
        var trainerEmail = UniqueEmail("stranger-trainer");
        await TestHelpers.RegisterAsync(trainerClient, trainerEmail, "TestPass1!", "Stranger", "Coach", "Trainer");
        var (trainerAccessToken, _) = await TestHelpers.LoginAsync(trainerClient, trainerEmail, "TestPass1!");
        TestHelpers.SetBearerToken(trainerClient, trainerAccessToken);

        // Act: the stranger trainer invites the already-registered client, carrying a message —
        // this is exactly the payload the finding describes as attacker-controlled text.
        var inviteResponse = await trainerClient.PostAsJsonAsync("/trainer/pending-invites", new
        {
            Email = clientEmail,
            Message = "This text must not land in the client's inbox before they accept."
        }, cancellationToken: TestContext.Current.CancellationToken);

        inviteResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            "the invite itself is still created successfully — only the immediate side effect is removed");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var clientUserId = await db.Users.Where(u => u.Email == clientEmail).Select(u => u.Id)
            .SingleAsync(TestContext.Current.CancellationToken);

        // Assert: no conversation was created for the invited client.
        (await db.Conversations.AnyAsync(c => c.ClientUserId == clientUserId, TestContext.Current.CancellationToken))
            .Should().BeFalse("the chat message must not be seeded until the client accepts the invite");

        // Assert the other half: the notification IS raised. Without this the invitee has no
        // signal at all until they happen to open the app, and a change that defers the
        // notification along with the conversation would slip through the assertion above.
        (await db.Notifications.CountAsync(n => n.RecipientUserId == clientUserId, TestContext.Current.CancellationToken))
            .Should().Be(1, "the invitee is told an invite arrived — it is the message body, not the alert, that waits for consent");
    }

}
