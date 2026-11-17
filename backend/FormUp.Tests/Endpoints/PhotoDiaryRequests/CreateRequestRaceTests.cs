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

namespace FormUp.Tests.Endpoints.PhotoDiaryRequests;

/// <summary>
/// Concurrency coverage for "one open photo request per coach and client" on
/// POST /trainer/photo-diary-requests. The open-request check and the insert are separate
/// statements, so without a lock two concurrent creates can both pass the check.
/// </summary>
[Collection(TestCollection.Name)]
public class CreateRequestRaceTests(FitnessApiFactory factory)
{
    private const int ConcurrentCreates = 8;

    private static string UniqueEmail(string prefix) => $"{prefix}-{Guid.NewGuid():N}@race-diary.com";

    /// <summary>
    /// While one transaction holds the coach's profile lock and has an uncommitted open request,
    /// a second caller cannot get the lock; once the first commits, the second's open-request
    /// read sees the row. The blocking is the assertion, so no sleep stands in for ordering.
    /// </summary>
    [Fact]
    public async Task LockProfessionalProfile_SecondCallerBlocksUntilFirstCommits_ThenSeesTheOpenRequest()
    {
        var ct = TestContext.Current.CancellationToken;

        var coach = await TestActors.Nutritionist(factory).WithEmail(UniqueEmail("race-coach")).CreateAsync(ct);
        var client = await TestActors.Client(factory).WithEmail(UniqueEmail("race-client")).CreateAsync(ct);
        var linkId = await TestActors.Link(factory, coach, client).CreateAsync(ct);

        using var scopeA = factory.Services.CreateScope();
        using var scopeB = factory.Services.CreateScope();
        var dbA = scopeA.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var dbB = scopeB.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        await using var transactionA = await dbA.BeginTransactionAsync(ct);
        await dbA.LockProfessionalProfileAsync(coach.ProfileId, ct);
        dbA.PhotoDiaryRequests.Add(NewRequest(coach.UserId, linkId));
        await dbA.SaveChangesAsync(ct);

        await using var transactionB = await dbB.BeginTransactionAsync(ct);
        var racerB = dbB.LockProfessionalProfileAsync(coach.ProfileId, ct);

        var raced = await Task.WhenAny(racerB, Task.Delay(TimeSpan.FromMilliseconds(500), ct));
        raced.Should().NotBeSameAs(
            racerB,
            "racer B must still be blocked on the row lock that racer A's open transaction holds");

        await transactionA.CommitAsync(ct);
        await racerB;

        var openCount = await dbB.PhotoDiaryRequests.AsNoTracking()
            .CountAsync(r => r.LinkId == linkId && r.Status == PhotoDiaryStatus.Pending, ct);
        openCount.Should().Be(1, "the loser's open-request read must observe the winner's committed row");

        await transactionB.RollbackAsync(ct);
    }

    [Fact]
    public async Task ConcurrentCreatesOnTheLink_ExactlyOneSucceeds_OthersGetAlreadyOpen()
    {
        var ct = TestContext.Current.CancellationToken;

        var coach = await TestActors.Nutritionist(factory).WithEmail(UniqueEmail("race-link-coach")).CreateAsync(ct);
        var client = await TestActors.Client(factory).WithEmail(UniqueEmail("race-link-client")).CreateAsync(ct);
        var linkId = await TestActors.Link(factory, coach, client).CreateAsync(ct);

        var responses = await Task.WhenAll(Enumerable.Range(0, ConcurrentCreates).Select(_ =>
            coach.Http.PostAsJsonAsync(
                "/trainer/photo-diary-requests",
                new { ClientId = client.PublicId, Mode = "Workflow", DurationDays = 7 },
                ct)));

        await AssertOneWinnerAsync(responses, ct);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.PhotoDiaryRequests.AsNoTracking().CountAsync(r => r.LinkId == linkId, ct))
            .Should().Be(1, "the invariant is about stored rows, not just the status codes");
    }

    [Fact]
    public async Task ConcurrentCreatesOnThePendingInvite_ExactlyOneSucceeds_OthersGetAlreadyOpen()
    {
        var ct = TestContext.Current.CancellationToken;

        var coach = await TestActors.Nutritionist(factory).WithEmail(UniqueEmail("race-invite-coach")).CreateAsync(ct);

        long inviteId;
        using (var seedScope = factory.Services.CreateScope())
        {
            var seedDb = seedScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var invite = new PendingInvite
            {
                ProfessionalProfileId = coach.ProfileId,
                Email = UniqueEmail("race-invitee"),
                SentAt = DateTime.UtcNow,
                Status = PendingInviteStatus.Pending,
                PublicId = Guid.NewGuid(),
                DateCreated = DateTime.UtcNow,
            };
            seedDb.PendingInvites.Add(invite);
            await seedDb.SaveChangesAsync(ct);
            inviteId = invite.Id;
        }

        var responses = await Task.WhenAll(Enumerable.Range(0, ConcurrentCreates).Select(_ =>
            coach.Http.PostAsJsonAsync(
                "/trainer/photo-diary-requests",
                new { PendingInviteId = inviteId, Mode = "Bulk" },
                ct)));

        await AssertOneWinnerAsync(responses, ct);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.PhotoDiaryRequests.AsNoTracking().CountAsync(r => r.PendingInviteId == inviteId, ct))
            .Should().Be(1, "the invariant is about stored rows, not just the status codes");
    }

    private static async Task AssertOneWinnerAsync(HttpResponseMessage[] responses, CancellationToken ct)
    {
        var statuses = string.Join(", ", responses.Select(r => (int)r.StatusCode));

        responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1, "got {0}", statuses);
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).Should().Be(ConcurrentCreates - 1, "got {0}", statuses);

        foreach (var loser in responses.Where(r => r.StatusCode == HttpStatusCode.Conflict))
        {
            (await loser.Content.ReadAsStringAsync(ct)).Should().Contain("PHOTO_DIARY_REQUEST_ALREADY_OPEN");
        }
    }

    private static PhotoDiaryRequest NewRequest(Guid professionalUserId, long linkId) => new()
    {
        Id = Guid.NewGuid(),
        ProfessionalId = professionalUserId,
        LinkId = linkId,
        DurationDays = 7,
        Status = PhotoDiaryStatus.Pending,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };
}
