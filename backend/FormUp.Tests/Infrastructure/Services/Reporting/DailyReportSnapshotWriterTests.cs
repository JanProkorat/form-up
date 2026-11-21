using FluentAssertions;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Application.Infrastructure.Services.Reporting;
using FormUp.Tests.Builders;
using FormUp.Tests.Endpoints.Billing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Infrastructure.Services.Reporting;

/// <summary>
/// <see cref="DailyReportSnapshotWriter"/> against the real databases. The totals are global over the whole database, so
/// the exact-totals test empties it first; the shared collection runs one class at a time.
/// </summary>
[Collection(TestCollection.Shard4)]
public class DailyReportSnapshotWriterTests(FitnessApiFactory factory)
{
    /// <summary>A currency no other test uses, so the revenue of the fixture can be asserted exactly.</summary>
    private const string RevenueCurrency = "XTS";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DailyReportSnapshotWriter Writer => factory.Services.GetRequiredService<DailyReportSnapshotWriter>();

    private async Task<List<DailyReportSnapshot>> SnapshotsOfAsync(DateOnly day)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.DailyReportSnapshots.AsNoTracking().Where(s => s.Day == day).ToListAsync(Ct);
    }

    private async Task AddTokenAsync(Guid userId, int daysAgo)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var token = $"snapshot-test-{Guid.NewGuid():N}";

        db.RefreshTokens.Add(new RefreshToken { UserId = userId, Token = token, ExpiresAt = DateTime.UtcNow.AddDays(7) });
        await db.SaveChangesAsync(Ct);

        // The context stamps DateCreated on insert, so the age is set afterwards.
        await db.RefreshTokens
            .Where(t => t.Token == token)
            .ExecuteUpdateAsync(setters => setters.SetProperty(t => t.DateCreated, DateTime.UtcNow.AddDays(-daysAgo)), Ct);
    }

    [Fact]
    public async Task WriteAsync_SeededFixture_StoresTheExactTotals()
    {
        // The totals are global over a database other tests write to, so the seeded fixture is measured against a baseline
        // written the same UTC day just before it. The collection runs one class at a time and the background loop is
        // stripped from the test host, so nothing else moves the totals in between.
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);
        await Writer.WriteAsync(DateTimeOffset.UtcNow, Ct);
        var before = (await SnapshotsOfAsync(DateOnly.FromDateTime(DateTime.UtcNow))).Should().ContainSingle().Subject;
        var paid = await PaidPlanBuilder.CreateAsync(factory, currency: RevenueCurrency, priceMinorUnits: 29900);
        var promotion = await PromotionBuilder.CreateAsync(factory, [paid]);

        var payingA = await TestActors.Trainer(factory).CreateAsync(Ct);
        var payingB = await TestActors.Trainer(factory).CreateAsync(Ct);
        var trialing = await TestActors.Trainer(factory).CreateAsync(Ct);
        var freeAtCap = await TestActors.Trainer(factory).CreateAsync(Ct);
        await TestActors.Trainer(factory).CreateAsync(Ct); // Free with no subscription row and no clients.
        var givenFree = await TestActors.Trainer(factory).CreateAsync(Ct);

        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, payingA, paid, SubscriptionStatus.Active);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, payingB, paid, SubscriptionStatus.PastDue);
        await BillingTestData.UpdateRowAsync(factory, payingB.ProfileId, row => row.CurrentPromotionId = promotion.Id);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, trialing, paid, SubscriptionStatus.Trialing);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, freeAtCap, "free");
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(
            factory, givenFree, paid, givenFreeAt: DateTimeOffset.UtcNow.AddDays(-1));

        // Live clients per coach: 1, 1, 0, 2, 0, 0 -> median (0 + 1) / 2.
        var clientOfA = (await TestActors.LinkClientsAsync(factory, payingA, UserRole.Trainer, 1)).Single();
        await TestActors.LinkClientsAsync(factory, payingB, UserRole.Trainer, 1);
        var clientsAtCap = await TestActors.LinkClientsAsync(factory, freeAtCap, UserRole.Trainer, 1, 1);

        await AddTokenAsync(payingA.UserId, daysAgo: 1);
        await AddTokenAsync(payingB.UserId, daysAgo: 29);
        await AddTokenAsync(trialing.UserId, daysAgo: 31);
        await AddTokenAsync(clientOfA.UserId, daysAgo: 2);
        await AddTokenAsync(clientsAtCap[0].UserId, daysAgo: 10);

        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
            await mongo.NutritionPlans.InsertOneAsync(
                new NutritionPlan
                {
                    ExternalId = Guid.NewGuid(),
                    Weeks =
                    [
                        new PlanWeek { WeekNumber = 1, DatePublished = DateTime.UtcNow.AddDays(-2) },
                        new PlanWeek { WeekNumber = 2, DatePublished = DateTime.UtcNow.AddDays(-20) },
                        new PlanWeek { WeekNumber = 3 },
                    ],
                },
                cancellationToken: Ct);
            await mongo.TrainingPlans.InsertOneAsync(
                new TrainingPlan
                {
                    ExternalId = Guid.NewGuid(),
                    Weeks = [new TrainingWeek { WeekNumber = 1, DatePublished = DateTime.UtcNow.AddDays(-1) }],
                },
                cancellationToken: Ct);
        }

        var now = DateTimeOffset.UtcNow;
        await Writer.WriteAsync(now, Ct);

        var after = (await SnapshotsOfAsync(DateOnly.FromDateTime(now.UtcDateTime))).Should().ContainSingle("the same day is one row").Subject;
        (after.TotalCoachCount - before.TotalCoachCount).Should().Be(6);
        (after.PayingCoachCount - before.PayingCoachCount).Should().Be(3, "Active, PastDue and Trialing count; the gift does not");
        after.PayingCoachesByPlan.Should().ContainSingle(plan => plan.PlanCode == paid).Which.Count.Should().Be(3);
        (after.FreeCoachCount - before.FreeCoachCount).Should().Be(2);
        (after.GivenFreeCoachCount - before.GivenFreeCoachCount).Should().Be(1);
        after.MonthlyRevenueFull.Should().ContainSingle(amount => amount.Currency == RevenueCurrency).Which.AmountMinorUnits
            .Should().Be(59800, "trialing and given plans bring no revenue");
        after.MonthlyRevenueAfterPromotions.Should().ContainSingle(amount => amount.Currency == RevenueCurrency).Which.AmountMinorUnits
            .Should().Be(29900 + 23920, "20 % off the PastDue coach");
        (after.ActiveCoachesLast30Days - before.ActiveCoachesLast30Days).Should().Be(2);
        (after.ActiveClientsLast7Days - before.ActiveClientsLast7Days).Should().Be(1);
        (after.PlanWeeksPublishedLast7Days - before.PlanWeeksPublishedLast7Days).Should()
            .Be(2, "one nutrition week and one training week were published within 7 days");
        (after.FreeCoachesAtClientCap - before.FreeCoachesAtClientCap).Should().Be(1);
    }

    [Theory]
    [InlineData(new int[0], 0)]
    [InlineData(new[] { 3 }, 3)]
    [InlineData(new[] { 2, 0, 1 }, 1)]
    [InlineData(new[] { 1, 1, 0, 2, 0, 0 }, 0.5)]
    public void Median_OverAllCoachCountsIncludingZeros_IsTheMiddleValue(int[] counts, double expected) =>
        DailyReportSnapshotWriter.Median([.. counts]).Should().Be((decimal)expected);

    [Fact]
    public async Task WriteAsync_RunTwiceOnTheSameDay_LeavesOneRowWithTheLatestTotals()
    {
        var firstRun = new DateTimeOffset(2041, 3, 3, 8, 0, 0, TimeSpan.Zero);
        var day = DateOnly.FromDateTime(firstRun.UtcDateTime);

        await Writer.WriteAsync(firstRun, Ct);
        var first = (await SnapshotsOfAsync(day)).Should().ContainSingle().Subject;
        await TestActors.Trainer(factory).CreateAsync(Ct);
        await Writer.WriteAsync(firstRun.AddHours(5), Ct);

        var second = (await SnapshotsOfAsync(day)).Should().ContainSingle("a re-run updates the day's row").Subject;
        second.Id.Should().Be(first.Id);
        second.TotalCoachCount.Should().Be(first.TotalCoachCount + 1);
    }

    [Fact]
    public async Task WriteAsync_TwoWritersRacingOnANewDay_EndWithOneRow()
    {
        var now = new DateTimeOffset(2042, 6, 6, 9, 0, 0, TimeSpan.Zero);

        await Task.WhenAll(Writer.WriteAsync(now, Ct), Writer.WriteAsync(now, Ct));

        (await SnapshotsOfAsync(DateOnly.FromDateTime(now.UtcDateTime))).Should().ContainSingle();
    }
}
