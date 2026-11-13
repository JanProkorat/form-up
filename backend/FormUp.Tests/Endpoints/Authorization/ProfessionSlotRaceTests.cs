using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Authorization;

/// <summary>
/// Concurrency coverage for the one-active-coach-per-profession invariant (#1009).
/// </summary>
/// <remarks>
/// #980 added the application-level guard, but the guard's read and the link insert were two
/// separate autocommitting statements: under READ COMMITTED two concurrent requests could both
/// observe the slot as free and both insert. #1009 wraps the pair in a transaction that first
/// takes a <c>FOR NO KEY UPDATE</c> row lock on the client's own <c>client_profiles</c> row —
/// the only row both racers are guaranteed to touch, since neither can see the other's
/// uncommitted link.
///
/// Two tests, deliberately at different levels. The first pins the MECHANISM deterministically
/// (the second racer really does block on the first, and really does see the winner's link once
/// it commits). The second pins the OUTCOME through the HTTP surface. Only the first is
/// timing-independent; the second can serialize by luck on a given run, which is why it is not
/// the only coverage here.
///
/// A third test covered the same race on CreateCollaborationEndpoint. That endpoint let a coach
/// mint an active link for another professional to their client with no client consent, which
/// contradicts the rule that only the client chooses their coaches — it was deleted, and its
/// race test with it.
/// </remarks>
[Collection(TestCollection.Name)]
public class ProfessionSlotRaceTests(FitnessApiFactory factory)
{
    private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@test.com";

    /// <summary>
    /// Creates a user and returns their <c>ApplicationUser.Id</c>. #1104: built directly via
    /// <see cref="TestActors"/> — this test's subject is the row-lock, not registration.
    /// </summary>
    private async Task<Guid> RegisterAndResolveUserIdAsync(string email, string role)
    {
        var userRole = Enum.Parse<UserRole>(role, ignoreCase: true);
        var builder = userRole == UserRole.Client
            ? TestActors.Client(factory)
            : TestActors.Professional(factory, userRole);

        var actor = await builder.WithEmail(email).CreateAsync(TestContext.Current.CancellationToken);
        return actor.UserId;
    }

    /// <summary>
    /// The lock actually serializes: while racer A holds it, racer B cannot proceed past its own
    /// lock acquisition — and once A commits, B's guard read sees A's link and reports the slot
    /// as taken. Deterministic: no sleeps stand in for the ordering, the blocking IS the
    /// assertion.
    /// </summary>
    [Fact]
    public async Task LockClientProfile_SecondCallerBlocksUntilFirstCommits_ThenSeesTheWinnersLink()
    {
        var ct = TestContext.Current.CancellationToken;

        var clientUserId = await RegisterAndResolveUserIdAsync(UniqueEmail("race-client"), "Client");
        var coachAUserId = await RegisterAndResolveUserIdAsync(UniqueEmail("race-coach-a"), "Nutritionist");
        var coachBUserId = await RegisterAndResolveUserIdAsync(UniqueEmail("race-coach-b"), "Nutritionist");

        long clientProfileId;
        long coachAProfileId;
        long coachBProfileId;
        using (var lookupScope = factory.Services.CreateScope())
        {
            var db = lookupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            clientProfileId = (await db.ClientProfiles.AsNoTracking()
                .FirstAsync(cp => cp.UserId == clientUserId, ct)).Id;
            coachAProfileId = (await db.ProfessionalProfiles.AsNoTracking()
                .FirstAsync(pp => pp.UserId == coachAUserId, ct)).Id;
            coachBProfileId = (await db.ProfessionalProfiles.AsNoTracking()
                .FirstAsync(pp => pp.UserId == coachBUserId, ct)).Id;
        }

        using var scopeA = factory.Services.CreateScope();
        using var scopeB = factory.Services.CreateScope();
        var dbA = scopeA.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var dbB = scopeB.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // Racer A: lock, insert a link occupying the nutrition slot, hold the transaction open.
        await using var transactionA = await dbA.BeginTransactionAsync(ct);
        await dbA.LockClientProfileAsync(clientProfileId, ct);

        dbA.ClientProfessionalLinks.Add(new ClientProfessionalLink
        {
            ClientProfileId = clientProfileId,
            ProfessionalProfileId = coachAProfileId,
            ProfessionalRole = Application.Domain.Enums.UserRole.Nutritionist,
            ConnectionState = ConnectionState.Connected,
            CanViewNutritionPlans = true,
            CanViewTrainingPlans = false
        });
        await dbA.SaveChangesAsync(ct);

        // Racer B: same lock. This must not complete while A holds the row.
        await using var transactionB = await dbB.BeginTransactionAsync(ct);
        var racerB = dbB.LockClientProfileAsync(clientProfileId, ct);

        // Give B every chance to finish if the lock were not doing its job. This delay does not
        // create the ordering — it only bounds how long we wait before asserting B is stuck.
        var raced = await Task.WhenAny(racerB, Task.Delay(TimeSpan.FromSeconds(2), ct));
        raced.Should().NotBeSameAs(
            racerB,
            "racer B must still be blocked on the row lock that racer A's open transaction holds");

        await transactionA.CommitAsync(ct);

        // With A committed, B's lock is granted and — under READ COMMITTED, which takes a fresh
        // snapshot per statement — its guard read now sees A's link.
        await racerB;

        var slotTaken = await ProfessionSlotGuard.IsSlotTakenByAnotherProfessionalAsync(
            dbB.ClientProfessionalLinks.AsNoTracking(),
            clientProfileId,
            coachBProfileId,
            wantsNutritionPlans: true,
            wantsTrainingPlans: false,
            ct);

        slotTaken.Should().BeTrue(
            "the loser's guard read must observe the winner's committed link, which is the whole "
            + "point of serializing on the client row");

        await transactionB.RollbackAsync(ct);
    }

    /// <summary>
    /// End-to-end outcome: one client holding two pending invites from two different
    /// nutritionists accepts both at once. Exactly one accept may succeed, the other must get
    /// the coded 400 — never a 500 and never two active links in the same profession slot.
    /// </summary>
    /// <remarks>
    /// This is the single-actor path the issue calls out: <c>AcceptClientInviteEndpoint</c> is
    /// <c>Roles(Client)</c>, so the CLIENT drives it and can race their own two accepts from two
    /// tabs or a retried request — no two professionals need to coordinate.
    /// </remarks>
    [Fact]
    public async Task ConcurrentAcceptsOfTwoNutritionInvites_ExactlyOneSucceeds_OtherGets400()
    {
        var ct = TestContext.Current.CancellationToken;
        var clientEmail = UniqueEmail("dual-invite-client");

        var inviteA = await CreateNutritionistInviteAsync(clientEmail);
        var inviteB = await CreateNutritionistInviteAsync(clientEmail);

        // #1104: the client actor is built directly via TestActors — this test's subject is the
        // accept-invite race, not registration. Two separate HttpClients carrying the SAME
        // identity, matching "the client races their own two accepts from two tabs".
        var clientActor = await TestActors.Client(factory)
            .WithEmail(clientEmail)
            .CreateAsync(ct);

        var clientOne = clientActor.Http;
        var clientTwo = factory.CreateClient();
        TestHelpers.SetBearerToken(
            clientTwo,
            TestTokenFactory.CreateAccessToken(factory, clientActor.UserId, clientEmail, ["Client"]));

        var responses = await Task.WhenAll(
            clientOne.PostAsJsonAsync($"/client/invites/{inviteA}/accept", new { }, ct),
            clientTwo.PostAsJsonAsync($"/client/invites/{inviteB}/accept", new { }, ct));

        var statuses = responses.Select(r => r.StatusCode).ToList();
        var observed = string.Join(", ", statuses);

        statuses.Should().NotContain(HttpStatusCode.InternalServerError,
            "the lock must never surface as a 500 or a raw DbUpdateException (got {0})", observed);
        statuses.Count(s => s == HttpStatusCode.NoContent).Should().Be(1,
            "exactly one accept may take the nutrition slot (got {0})", observed);
        statuses.Count(s => s == HttpStatusCode.BadRequest).Should().Be(1,
            "the loser must get the coded PROFESSION_ALREADY_OCCUPIED 400 (got {0})", observed);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var clientProfile = await db.ClientProfiles.AsNoTracking()
            .FirstAsync(cp => cp.User!.Email == clientEmail, ct);

        var activeNutritionLinks = await db.ClientProfessionalLinks.AsNoTracking()
            .CountAsync(l => l.ClientProfileId == clientProfile.Id
                             && l.ConnectionState != ConnectionState.Ended
                             && l.CanViewNutritionPlans, ct);

        activeNutritionLinks.Should().Be(1,
            "the invariant is about stored state, not just the status codes — two links carrying "
            + "the nutrition flag is the forbidden state regardless of what the responses said");
    }

    /// <summary>
    /// Registers a fresh nutritionist and has them create a pending invite for
    /// <paramref name="clientEmail"/>. Returns the invite's PublicId.
    /// </summary>
    private async Task<Guid> CreateNutritionistInviteAsync(string clientEmail)
    {
        var coach = await TestActors.Nutritionist(factory)
            .WithEmail(UniqueEmail("dual-invite-coach"))
            .CreateAsync(TestContext.Current.CancellationToken);

        var response = await coach.Http.PostAsJsonAsync("/trainer/pending-invites", new
        {
            FirstName = "Dual",
            LastName = "Invitee",
            Email = clientEmail
        }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var invite = await response.Content.ReadFromJsonAsync<PendingInviteCreated>(
            cancellationToken: TestContext.Current.CancellationToken);
        return invite!.PublicId;
    }

    private sealed record PendingInviteCreated(Guid PublicId);
}
