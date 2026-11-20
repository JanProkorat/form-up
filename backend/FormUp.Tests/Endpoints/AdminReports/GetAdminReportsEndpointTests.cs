using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.AdminReports.GetAdminReports;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.AdminReports;

/// <summary>
/// Integration tests for <c>GET /admin/reports</c> (<c>GetAdminReportsEndpoint</c>) on a host whose clock is pinned.
/// Snapshots, coach events and paid invoices are cleared before each test that reads them; money assertions use a
/// currency unique to the test because the live revenue summary covers the whole shared database.
/// </summary>
[Collection(TestCollection.Name)]
public class GetAdminReportsEndpointTests(FitnessApiFactory factory)
{
    private static readonly DateTimeOffset Now = new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static WebApplicationFactory<Program>? pinnedFactory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> PinnedFactory => pinnedFactory ??= factory.WithWebHostBuilder(builder =>
        builder.ConfigureServices(services =>
        {
            foreach (var descriptor in services.Where(d => d.ServiceType == typeof(TimeProvider)).ToList())
            {
                services.Remove(descriptor);
            }

            services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
        }));

    private static string NewCurrency() => Guid.NewGuid().ToString("N")[..3].ToUpperInvariant();

    private static DateOnly Day(DateTimeOffset instant) => DateOnly.FromDateTime(instant.UtcDateTime);

    private static DateTimeOffset PeriodStart(AdminReportPeriod period) => period switch
    {
        AdminReportPeriod.Days90 => Now.AddDays(-90),
        AdminReportPeriod.Months12 => Now.AddMonths(-12),
        _ => Now.AddDays(-30),
    };

    private async Task<HttpClient> AdminClientAsync()
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var client = PinnedFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = admin.Http.DefaultRequestHeaders.Authorization;

        return client;
    }

    private async Task<GetAdminReportsResponse> GetReportAsync(AdminReportPeriod period = AdminReportPeriod.Days30)
    {
        using var client = await AdminClientAsync();
        var response = await client.GetAsync($"/admin/reports?period={period}", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return (await response.Content.ReadFromJsonAsync<GetAdminReportsResponse>(JsonOptions, Ct))!;
    }

    private async Task<Guid> CoachIdAsync() => (await TestActors.Trainer(factory).CreateAsync(Ct)).UserId;

    private async Task ResetReportingTablesAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        await db.DailyReportSnapshots.ExecuteDeleteAsync(Ct);
        await db.CoachEvents.ExecuteDeleteAsync(Ct);
        await db.PaidInvoices.ExecuteDeleteAsync(Ct);
    }

    private async Task SeedAsync(params object[] rows)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.AddRange(rows);
        await db.SaveChangesAsync(Ct);
    }

    private static DailyReportSnapshot Snapshot(
        DateOnly day, string currency, long revenue, int payingCoaches = 1, int activeClients = 0, int totalCoaches = 0) =>
        new()
        {
            Day = day,
            TotalCoachCount = totalCoaches,
            PayingCoachCount = payingCoaches,
            ActiveClientsLast7Days = activeClients,
            MonthlyRevenueFull = [new ReportCurrencyAmount { Currency = currency, AmountMinorUnits = revenue + 1000 }],
            MonthlyRevenueAfterPromotions = [new ReportCurrencyAmount { Currency = currency, AmountMinorUnits = revenue }],
        };

    private static CoachEvent Event(
        CoachEventType type, DateTimeOffset at, Guid? coach = null, string? currency = null, long effect = 0) =>
        new()
        {
            CoachUserId = coach,
            EventType = type,
            OccurredAt = at,
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            Currency = currency,
            FullMonthlyRevenueEffectMinorUnits = effect,
            DiscountedMonthlyRevenueEffectMinorUnits = effect,
        };

    private static PaidInvoice Invoice(DateTimeOffset paidAt, string currency, long amount) =>
        new()
        {
            ExternalInvoiceId = $"in_{Guid.NewGuid():N}",
            Currency = currency.ToLowerInvariant(),
            AmountPaid = amount,
            PaidAt = paidAt,
            PeriodStart = paidAt,
            PeriodEnd = paidAt.AddMonths(1),
        };

    private static AdminReportCurrencyValueDto For(IEnumerable<AdminReportCurrencyValueDto> values, string currency) =>
        values.Single(value => value.Currency == currency);

    [Theory]
    [InlineData(AdminReportPeriod.Days30)]
    [InlineData(AdminReportPeriod.Days90)]
    [InlineData(AdminReportPeriod.Months12)]
    public async Task Get_BaselineSnapshotOnPeriodStartDay_ComparesAgainstItAndFillsDeltas(AdminReportPeriod period)
    {
        await ResetReportingTablesAsync();
        var currency = NewCurrency();
        var plan = await PaidPlanBuilder.CreateAsync(factory, currency, priceMinorUnits: 10000);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, plan);
        var startDay = Day(PeriodStart(period));
        await SeedAsync(
            Snapshot(startDay.AddDays(-1), currency, 1, payingCoaches: 99),
            Snapshot(startDay, currency, 7000, payingCoaches: 2, activeClients: 5),
            Snapshot(startDay.AddDays(2), currency, 8000, payingCoaches: 3),
            Snapshot(Day(Now), currency, 9000, payingCoaches: 4, activeClients: 9, totalCoaches: 87),
            Event(CoachEventType.Cancelled, PeriodStart(period).AddHours(1), await CoachIdAsync(), currency, -500),
            Event(CoachEventType.Cancelled, PeriodStart(period).AddHours(2), await CoachIdAsync(), currency, -300));

        var report = await GetReportAsync(period);

        report.Period.Should().Be(period);
        report.ComparedFrom.Should().Be(startDay);
        report.SnapshotDay.Should().Be(Day(Now));
        var revenue = For(report.Money.MonthlyRevenueAfterPromotions, currency);
        revenue.Value.Should().Be(10000);
        revenue.Previous.Should().Be(7000);
        For(report.Money.MonthlyRevenueFull, currency).Previous.Should().Be(8000);
        report.Money.AveragePerPayingCoach.Single(row => row.Currency == currency)
            .Should().BeEquivalentTo(new AdminReportAverageDto { Currency = currency, Value = 10000, Previous = 3500, CoachesBilled = 1 });
        report.Usage!.ActiveClientsLast7Days.Should().BeEquivalentTo(new AdminReportCountDto { Value = 9, Previous = 5 });
        report.Usage.TotalCoaches.Should().Be(87, "the denominator comes from the latest snapshot");
        report.Money.ChurnPercent.Should().Be(100m, "two coaches cancelled against two paying coaches at the baseline");
        report.Money.LostToCancellations.Count.Value.Should().Be(2);
        var lost = For(report.Money.LostToCancellations.Amounts, currency);
        lost.Value.Should().Be(800);
        lost.Previous.Should().Be(0, "nothing was cancelled in the window before");
        report.MrrSeries.Select(point => point.Day).Should().Equal(startDay, startDay.AddDays(2), Day(Now));
    }

    [Theory]
    [InlineData(AdminReportPeriod.Days30)]
    [InlineData(AdminReportPeriod.Days90)]
    [InlineData(AdminReportPeriod.Months12)]
    public async Task Get_StartDaySnapshotMissing_UsesEarliestSnapshotInsidePeriod(AdminReportPeriod period)
    {
        await ResetReportingTablesAsync();
        var currency = NewCurrency();
        var startDay = Day(PeriodStart(period));
        await SeedAsync(
            Snapshot(startDay.AddDays(-3), currency, 100),
            Snapshot(startDay.AddDays(3), currency, 4000, payingCoaches: 4),
            Snapshot(startDay.AddDays(9), currency, 5000));

        var report = await GetReportAsync(period);

        report.ComparedFrom.Should().Be(startDay.AddDays(3));
        For(report.Money.MonthlyRevenueAfterPromotions, currency).Previous.Should().Be(4000);
        report.MrrSeries.Should().HaveCount(2, "the snapshot before the period is not part of it");
    }

    [Fact]
    public async Task Get_NoSnapshotInsidePeriod_NullsEveryBaselineValueButKeepsCurrentOnes()
    {
        await ResetReportingTablesAsync();
        var currency = NewCurrency();
        var plan = await PaidPlanBuilder.CreateAsync(factory, currency, priceMinorUnits: 10000);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, await TestActors.Trainer(factory).CreateAsync(Ct), plan);
        await SeedAsync(
            Snapshot(Day(PeriodStart(AdminReportPeriod.Days30)).AddDays(-5), currency, 100),
            Event(CoachEventType.Cancelled, Now.AddDays(-2), await CoachIdAsync(), currency, -500));

        var report = await GetReportAsync();

        report.ComparedFrom.Should().BeNull();
        report.SnapshotDay.Should().NotBeNull("the older snapshot is still the latest one");
        var revenue = For(report.Money.MonthlyRevenueAfterPromotions, currency);
        revenue.Value.Should().Be(10000);
        revenue.Previous.Should().BeNull();
        report.Money.AveragePerPayingCoach.Single(row => row.Currency == currency).Previous.Should().BeNull();
        report.Money.ChurnPercent.Should().BeNull();
        report.Usage!.ActiveClientsLast7Days.Previous.Should().BeNull();
        report.MrrSeries.Should().BeEmpty();
        report.Money.LostToCancellations.Count.Value.Should().Be(1);
    }

    [Fact]
    public async Task Get_NoSnapshotAtAll_ReturnsNullSnapshotDayNullUsageAndEmptySeries()
    {
        await ResetReportingTablesAsync();
        var currency = NewCurrency();
        var plan = await PaidPlanBuilder.CreateAsync(factory, currency, priceMinorUnits: 10000);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, await TestActors.Trainer(factory).CreateAsync(Ct), plan);

        var report = await GetReportAsync();

        report.SnapshotDay.Should().BeNull();
        report.ComparedFrom.Should().BeNull();
        report.Usage.Should().BeNull();
        report.MrrSeries.Should().BeEmpty();
        For(report.Money.MonthlyRevenueFull, currency).Value.Should().Be(10000);
    }

    [Fact]
    public async Task Get_ChurnWithZeroPayingCoachesAtBaseline_IsNull()
    {
        await ResetReportingTablesAsync();
        await SeedAsync(
            Snapshot(Day(PeriodStart(AdminReportPeriod.Days30)), NewCurrency(), 0, payingCoaches: 0),
            Event(CoachEventType.Cancelled, Now.AddDays(-2), await CoachIdAsync()));

        var report = await GetReportAsync();

        report.ComparedFrom.Should().NotBeNull();
        report.Money.ChurnPercent.Should().BeNull();
    }

    [Fact]
    public async Task Get_RepeatedEventsOfOneCoach_CountOnceInTheFunnelAndPaymentFailures()
    {
        await ResetReportingTablesAsync();
        var coachA = await CoachIdAsync();
        var coachB = await CoachIdAsync();
        var coachOutside = await CoachIdAsync();
        var signUpAt = Now.AddDays(-10);
        await SeedAsync(
            Event(CoachEventType.SignedUp, signUpAt, coachA),
            Event(CoachEventType.SignedUp, signUpAt, coachB),
            Event(CoachEventType.SignedUp, signUpAt, null),
            Event(CoachEventType.SignedUp, Now.AddDays(-45), coachOutside),
            Event(CoachEventType.EmailVerified, signUpAt.AddHours(1), coachA),
            Event(CoachEventType.EmailVerified, signUpAt.AddHours(-1), coachB),
            Event(CoachEventType.ChosePlan, signUpAt.AddHours(2), coachA),
            Event(CoachEventType.ChosePlan, signUpAt.AddHours(3), coachA),
            Event(CoachEventType.ChosePlan, signUpAt.AddHours(3), coachOutside),
            Event(CoachEventType.StartedPaidPlan, signUpAt.AddDays(1), coachA),
            Event(CoachEventType.PaymentFailed, Now.AddDays(-5), coachA),
            Event(CoachEventType.PaymentFailed, Now.AddDays(-4), coachA),
            Event(CoachEventType.PaymentFailed, Now.AddDays(-3), coachB),
            Event(CoachEventType.PromotionCodeApplied, Now.AddDays(-3), coachA),
            Event(CoachEventType.PromotionCodeApplied, Now.AddDays(-2), coachB));

        var report = await GetReportAsync();

        report.Funnel.Should().BeEquivalentTo(new AdminReportFunnelDto
        {
            SignedUp = 3,
            EmailVerified = 1,
            ChosePlan = 1,
            StartedPaidPlan = 1,
        });
        report.PlanChanges.PaymentFailedCoaches.Should().Be(2);
        report.PlanChanges.PromotionCodesApplied.Should().Be(2);
    }

    [Fact]
    public async Task Get_PlanChanges_SumCountAndRevenueEffectPerCurrencyAndComparePreviousWindowForCancellations()
    {
        await ResetReportingTablesAsync();
        var currency = NewCurrency();
        await SeedAsync(
            Event(CoachEventType.Upgraded, Now.AddDays(-3), await CoachIdAsync(), currency, 2000),
            Event(CoachEventType.Upgraded, Now.AddDays(-2), await CoachIdAsync(), currency, 1000),
            Event(CoachEventType.Downgraded, Now.AddDays(-2), await CoachIdAsync(), currency, -400),
            Event(CoachEventType.Cancelled, Now.AddDays(-1), await CoachIdAsync(), currency, -900),
            Event(CoachEventType.Cancelled, Now.AddDays(-40), await CoachIdAsync(), currency, -250),
            Event(CoachEventType.Cancelled, Now.AddDays(-70), await CoachIdAsync(), currency, -999));

        var report = await GetReportAsync();

        report.PlanChanges.Upgraded.Count.Should().Be(2);
        report.PlanChanges.Upgraded.RevenueEffect.Should()
            .ContainSingle().Which.Should().BeEquivalentTo(new AdminReportCurrencyAmountDto { Currency = currency, AmountMinorUnits = 3000 });
        report.PlanChanges.Downgraded.RevenueEffect.Single().AmountMinorUnits.Should().Be(-400);
        report.PlanChanges.Cancelled.Count.Should().Be(1);
        report.PlanChanges.Cancelled.RevenueEffect.Single().AmountMinorUnits.Should().Be(-900);
        report.Money.LostToCancellations.Count.Should().BeEquivalentTo(new AdminReportCountDto { Value = 1, Previous = 1 });
        var lost = For(report.Money.LostToCancellations.Amounts, currency);
        lost.Value.Should().Be(900);
        lost.Previous.Should().Be(250, "the window before the last 30 days holds only the 40 days old event");
    }

    [Fact]
    public async Task Get_CollectedThisMonth_SplitsOnTheUtcMonthBoundaryAndComparesPreviousMonthToTheSameMoment()
    {
        await ResetReportingTablesAsync();
        var currency = NewCurrency();
        await SeedAsync(
            Invoice(new DateTimeOffset(2026, 5, 31, 23, 59, 59, TimeSpan.Zero), currency, 1000),
            Invoice(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero), currency, 200),
            Invoice(new DateTimeOffset(2026, 6, 10, 8, 0, 0, TimeSpan.Zero), currency, 30),
            Invoice(new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero), currency, 4),
            Invoice(new DateTimeOffset(2026, 5, 15, 11, 0, 0, TimeSpan.Zero), currency, 5),
            Invoice(new DateTimeOffset(2026, 4, 30, 23, 59, 59, TimeSpan.Zero), currency, 6000));

        var report = await GetReportAsync();

        var collected = For(report.Money.CollectedThisMonth, currency);
        collected.Value.Should().Be(230, "the invoice paid one second before June is last month's");
        collected.Previous.Should().Be(9, "May up to the 15th at noon, without the end-of-month invoice");
    }

    [Fact]
    public async Task Get_PayingCoaches_SplitRevenuePerPlanAndAddOnAfterPromotionsAndAveragePerBilledCoach()
    {
        await ResetReportingTablesAsync();
        var currency = NewCurrency();
        var plan = await PaidPlanBuilder.CreateAsync(
            factory, currency, priceMinorUnits: 30000, addOnMinorUnits: 10000, bothRolesIncluded: false);
        var promotion = await PromotionBuilder.CreateAsync(factory, [plan]);

        async Task SubscribeAsync(SubscriptionStatus status = SubscriptionStatus.Active, bool addOn = false, bool promoted = false)
        {
            var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
            await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, plan, status, paysSecondRoleAddOn: addOn);

            if (promoted)
            {
                using var scope = factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var subscription = await db.CoachSubscriptions.SingleAsync(cs => cs.ProfessionalProfileId == coach.ProfileId, Ct);
                subscription.CurrentPromotionId = promotion.Id;
                await db.SaveChangesAsync(Ct);
            }
        }

        await SubscribeAsync();
        await SubscribeAsync(addOn: true);
        await SubscribeAsync(addOn: true, promoted: true);
        await SubscribeAsync(SubscriptionStatus.Trialing);

        var report = await GetReportAsync();

        For(report.Money.MonthlyRevenueFull, currency).Value.Should().Be(110000);
        For(report.Money.MonthlyRevenueAfterPromotions, currency).Value.Should().Be(102000);
        report.Money.AveragePerPayingCoach.Single(row => row.Currency == currency)
            .Should().BeEquivalentTo(new AdminReportAverageDto { Currency = currency, Value = 34000, Previous = null, CoachesBilled = 3 });
        var planRevenue = report.Money.Plans.Single(row => row.PlanCode == plan);
        planRevenue.PayingCoaches.Should().Be(4, "a trialing coach is a paying coach but brings no revenue yet");
        planRevenue.Revenue.Should().ContainSingle()
            .Which.AmountMinorUnits.Should().Be(84000, "30000 + 30000 + 24000 plan lines, add-ons excluded");
        report.Money.AddOns.Single(row => row.Currency == currency)
            .Should().BeEquivalentTo(new AdminReportAddOnDto { Currency = currency, Count = 2, RevenueMinorUnits = 18000 });
    }

    [Fact]
    public async Task Get_InvalidPeriod_Returns400()
    {
        using var client = await AdminClientAsync();

        var response = await client.GetAsync("/admin/reports?period=7", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
