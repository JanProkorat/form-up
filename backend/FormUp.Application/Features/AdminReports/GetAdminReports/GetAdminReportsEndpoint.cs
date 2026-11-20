using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.AdminReports.GetAdminReports;

/// <summary>
/// Totals for the admin Reports page. Current money comes from the shared live revenue summary, trends from the daily
/// snapshots, and the funnel, plan changes and cancellations from the coach event history. Counts and amounts only.
/// </summary>
/// <param name="db">Application database context.</param>
/// <param name="timeProvider">Clock.</param>
/// <param name="revenue">Shared coach counts and revenue, also read by the admin Coaches stats and the daily snapshot.</param>
internal sealed class GetAdminReportsEndpoint(IApplicationDbContext db, TimeProvider timeProvider, ICoachRevenueService revenue)
    : Endpoint<GetAdminReportsRequest, GetAdminReportsResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/admin/reports");
        Roles(AppRoles.Admin);
        Summary(s =>
        {
            s.Summary = "Admin reports";
            s.Description = "Returns totals for the admin Reports page over a 30 day, 90 day or 12 month period: monthly revenue " +
                            "(full and after promotions), average per paying coach, collected this month, cancellations, churn, " +
                            "revenue per plan, add-ons, the sign-up funnel, plan changes, the monthly revenue series and usage. " +
                            "Each metric carries a current value and a raw previous value (null without a baseline); percentages " +
                            "are computed by the client. Money is in minor units per currency, with no VAT, fees, refunds or " +
                            "payouts. Only totals are returned, never a per-client or per-coach row.";
            s.Response<GetAdminReportsResponse>(StatusCodes.Status200OK, "The report totals");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest, "Invalid period");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or invalid credentials");
            s.Response(StatusCodes.Status403Forbidden, "Caller is not an Admin");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(GetAdminReportsRequest req, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow().ToUniversalTime();
        var (periodStart, previousStart) = Window(req.Period, now);
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var startDay = DateOnly.FromDateTime(periodStart.UtcDateTime);

        var inPeriod = db.DailyReportSnapshots.AsNoTracking().Where(s => s.Day >= startDay && s.Day <= today);
        var snapshots = await inPeriod.OrderBy(s => s.Day).ToListAsync(ct);
        var baseline = snapshots.FirstOrDefault();
        var latest = await db.DailyReportSnapshots.AsNoTracking()
            .Where(s => s.Day <= today)
            .OrderByDescending(s => s.Day)
            .FirstOrDefaultAsync(ct);

        var summary = await revenue.GetSummaryAsync(now, ct);
        var cancelledEvents = await LoadEventTotalsAsync(CoachEventType.Cancelled, periodStart, now, ct);
        var previousCancelledEvents = await LoadEventTotalsAsync(CoachEventType.Cancelled, previousStart, periodStart.AddTicks(-1), ct);
        var cancelledCoaches = await CountDistinctCoachesAsync(CoachEventType.Cancelled, periodStart, now, ct);

        await Send.OkAsync(new GetAdminReportsResponse
        {
            Period = req.Period,
            SnapshotDay = latest?.Day,
            ComparedFrom = baseline?.Day,
            Money = new AdminReportMoneyDto
            {
                MonthlyRevenueFull = WithPrevious(
                    Pairs(summary.FullMonthlyRevenue), baseline is null ? null : Pairs(baseline.MonthlyRevenueFull)),
                MonthlyRevenueAfterPromotions = WithPrevious(
                    Pairs(summary.DiscountedMonthlyRevenue), baseline is null ? null : Pairs(baseline.MonthlyRevenueAfterPromotions)),
                AveragePerPayingCoach = BuildAverages(summary, baseline),
                CollectedThisMonth = await LoadCollectedAsync(now, ct),
                LostToCancellations = new AdminReportLostDto
                {
                    Count = new AdminReportCountDto
                    {
                        Value = cancelledEvents.Sum(row => row.Count),
                        Previous = previousCancelledEvents.Sum(row => row.Count),
                    },
                    Amounts = WithPrevious(Lost(cancelledEvents), Lost(previousCancelledEvents)),
                },
                ChurnPercent = baseline is { PayingCoachCount: > 0 }
                    ? Math.Round(cancelledCoaches * 100m / baseline.PayingCoachCount, 2, MidpointRounding.AwayFromZero)
                    : null,
                Plans = BuildPlans(summary),
                AddOns = summary.ByCurrency
                    .Where(detail => detail.AddOnCount > 0)
                    .Select(detail => new AdminReportAddOnDto
                    {
                        Currency = detail.Currency.ToUpperInvariant(),
                        Count = detail.AddOnCount,
                        RevenueMinorUnits = detail.AddOnDiscountedMonthlyMinorUnits,
                    })
                    .ToList(),
            },
            Funnel = await BuildFunnelAsync(periodStart, now, ct),
            PlanChanges = await BuildPlanChangesAsync(cancelledEvents, periodStart, now, ct),
            MrrSeries = snapshots
                .Select(s => new AdminReportMrrPointDto
                {
                    Day = s.Day,
                    Full = Amounts(Pairs(s.MonthlyRevenueFull)),
                    AfterPromotions = Amounts(Pairs(s.MonthlyRevenueAfterPromotions)),
                })
                .ToList(),
            Usage = latest is null
                ? null
                : new AdminReportUsageDto
                {
                    TotalCoaches = latest.TotalCoachCount,
                    ActiveCoachesLast30Days = latest.ActiveCoachesLast30Days,
                    ActiveClientsLast7Days = new AdminReportCountDto
                    {
                        Value = latest.ActiveClientsLast7Days,
                        Previous = baseline?.ActiveClientsLast7Days,
                    },
                    PlanWeeksPublishedLast7Days = latest.PlanWeeksPublishedLast7Days,
                    FreeCoachesAtClientCap = latest.FreeCoachesAtClientCap,
                    MedianClientsPerCoach = latest.MedianClientsPerCoach,
                },
        }, ct);
    }

    /// <summary>
    /// The period start and the start of the previous window of the same length.
    /// </summary>
    private static (DateTimeOffset PeriodStart, DateTimeOffset PreviousStart) Window(AdminReportPeriod period, DateTimeOffset now) =>
        period switch
        {
            AdminReportPeriod.Days90 => (now.AddDays(-90), now.AddDays(-180)),
            AdminReportPeriod.Months12 => (now.AddMonths(-12), now.AddMonths(-24)),
            _ => (now.AddDays(-30), now.AddDays(-60)),
        };

    private static List<(string Currency, long Amount)> Pairs(IEnumerable<CoachRevenueCurrencyTotal> totals) =>
        totals.Select(total => (total.Currency, total.AmountMinorUnits)).ToList();

    private static List<(string Currency, long Amount)> Pairs(IEnumerable<ReportCurrencyAmount> amounts) =>
        amounts.Select(amount => (amount.Currency, amount.AmountMinorUnits)).ToList();

    private static List<AdminReportCurrencyAmountDto> Amounts(IEnumerable<(string Currency, long Amount)> pairs) =>
        Sum(pairs)
            .Select(pair => new AdminReportCurrencyAmountDto { Currency = pair.Key, AmountMinorUnits = pair.Value })
            .ToList();

    /// <summary>
    /// Sums amounts per upper-case currency, ordered by currency.
    /// </summary>
    private static SortedDictionary<string, long> Sum(IEnumerable<(string Currency, long Amount)> pairs)
    {
        var totals = new SortedDictionary<string, long>(StringComparer.Ordinal);

        foreach (var (currency, amount) in pairs)
        {
            var key = currency.ToUpperInvariant();
            totals[key] = totals.GetValueOrDefault(key) + amount;
        }

        return totals;
    }

    /// <summary>
    /// Pairs current amounts with baseline amounts per currency. A currency missing on one side counts as zero there;
    /// without a baseline every previous is null.
    /// </summary>
    private static List<AdminReportCurrencyValueDto> WithPrevious(
        IEnumerable<(string Currency, long Amount)> current, IEnumerable<(string Currency, long Amount)>? previous)
    {
        var currentTotals = Sum(current);
        var previousTotals = previous is null ? null : Sum(previous);
        var currencies = currentTotals.Keys
            .Union(previousTotals is null ? Enumerable.Empty<string>() : previousTotals.Keys)
            .Order(StringComparer.Ordinal);

        return currencies
            .Select(currency => new AdminReportCurrencyValueDto
            {
                Currency = currency,
                Value = currentTotals.GetValueOrDefault(currency),
                Previous = previousTotals is null ? null : previousTotals.GetValueOrDefault(currency),
            })
            .ToList();
    }

    /// <summary>
    /// Average after-promotion revenue per billed coach per currency. The baseline divides by the snapshot's total paying
    /// coach count, which covers every currency, so the delta is approximate with more than one currency.
    /// </summary>
    private static List<AdminReportAverageDto> BuildAverages(CoachRevenueSummary summary, DailyReportSnapshot? baseline)
    {
        var current = Sum(Pairs(summary.DiscountedMonthlyRevenue));
        var previous = baseline is null ? null : Sum(Pairs(baseline.MonthlyRevenueAfterPromotions));
        var billed = summary.ByCurrency.ToDictionary(detail => detail.Currency.ToUpperInvariant(), detail => detail.CoachesBilled);

        return current.Keys.Union(previous is null ? Enumerable.Empty<string>() : previous.Keys).Order(StringComparer.Ordinal)
            .Select(currency => new AdminReportAverageDto
            {
                Currency = currency,
                CoachesBilled = billed.GetValueOrDefault(currency),
                Value = Divide(current.GetValueOrDefault(currency), billed.GetValueOrDefault(currency)),
                Previous = previous is null ? null : Divide(previous.GetValueOrDefault(currency), baseline!.PayingCoachCount),
            })
            .ToList();
    }

    private static long Divide(long amount, int divisor) =>
        divisor <= 0 ? 0 : (long)Math.Round(amount / (decimal)divisor, MidpointRounding.AwayFromZero);

    private static List<AdminReportPlanRevenueDto> BuildPlans(CoachRevenueSummary summary) =>
        summary.PayingByPlan
            .Select(plan => new AdminReportPlanRevenueDto
            {
                PlanCode = plan.Code,
                NameCs = plan.NameCs,
                NameEn = plan.NameEn,
                NameDe = plan.NameDe,
                PayingCoaches = plan.Count,
                Revenue = summary.ByCurrency
                    .SelectMany(detail => detail.DiscountedByPlan
                        .Where(amount => amount.PlanCode == plan.Code)
                        .Select(amount => new AdminReportCurrencyAmountDto
                        {
                            Currency = detail.Currency.ToUpperInvariant(),
                            AmountMinorUnits = amount.DiscountedMonthlyMinorUnits,
                        }))
                    .ToList(),
            })
            .ToList();

    /// <summary>
    /// Invoices paid since the start of the current UTC month against the previous calendar month up to the same moment
    /// of the month (cut off where the current month starts, for a shorter previous month).
    /// </summary>
    private async Task<List<AdminReportCurrencyValueDto>> LoadCollectedAsync(DateTimeOffset now, CancellationToken ct)
    {
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var previousMonthStart = monthStart.AddMonths(-1);
        var previousEnd = previousMonthStart + (now - monthStart);

        if (previousEnd >= monthStart)
        {
            previousEnd = monthStart.AddTicks(-1);
        }

        return WithPrevious(
            await SumInvoicesAsync(monthStart, now, ct),
            await SumInvoicesAsync(previousMonthStart, previousEnd, ct));
    }

    private async Task<List<(string Currency, long Amount)>> SumInvoicesAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var rows = await db.PaidInvoices
            .AsNoTracking()
            .Where(invoice => invoice.PaidAt >= from && invoice.PaidAt <= to)
            .GroupBy(invoice => invoice.Currency)
            .Select(group => new { Currency = group.Key, Amount = group.Sum(invoice => invoice.AmountPaid) })
            .ToListAsync(ct);

        return rows.Select(row => (row.Currency, row.Amount)).ToList();
    }

    private sealed record EventTotal(CoachEventType EventType, string? Currency, int Count, long Effect);

    /// <summary>
    /// Count and summed after-promotion revenue effect of the events of one type in a window, per currency.
    /// </summary>
    private async Task<List<EventTotal>> LoadEventTotalsAsync(
        CoachEventType type, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        await db.CoachEvents
            .AsNoTracking()
            .Where(e => e.EventType == type && e.OccurredAt >= from && e.OccurredAt <= to)
            .GroupBy(e => e.Currency)
            .Select(group => new EventTotal(
                type, group.Key, group.Count(), group.Sum(e => e.DiscountedMonthlyRevenueEffectMinorUnits)))
            .ToListAsync(ct);

    /// <summary>
    /// The monthly revenue the cancellations took away per currency, as positive amounts.
    /// </summary>
    private static List<(string Currency, long Amount)> Lost(IEnumerable<EventTotal> totals) =>
        totals.Where(row => row.Currency is not null).Select(row => (row.Currency!, -row.Effect)).ToList();

    /// <summary>
    /// Distinct coaches with an event of the type in the window. Events of deleted coaches have no coach link, so each
    /// counts as one.
    /// </summary>
    private async Task<int> CountDistinctCoachesAsync(
        CoachEventType type, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var events = db.CoachEvents.AsNoTracking().Where(e => e.EventType == type && e.OccurredAt >= from && e.OccurredAt <= to);

        return await events.Where(e => e.CoachUserId != null).Select(e => e.CoachUserId).Distinct().CountAsync(ct)
               + await events.CountAsync(e => e.CoachUserId == null, ct);
    }

    /// <summary>
    /// Step one counts the sign-ups of the period. Each later step counts distinct sign-up coaches with such an event on
    /// or after their sign-up. Deleted coaches drop out of the later steps, because their events lose the coach link.
    /// </summary>
    private async Task<AdminReportFunnelDto> BuildFunnelAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var signUps = db.CoachEvents
            .AsNoTracking()
            .Where(e => e.EventType == CoachEventType.SignedUp && e.OccurredAt >= from && e.OccurredAt <= to);

        var later = await (
                from e in db.CoachEvents.AsNoTracking()
                join signUp in signUps on e.CoachUserId equals signUp.CoachUserId
                where e.OccurredAt >= signUp.OccurredAt
                      && (e.EventType == CoachEventType.EmailVerified
                          || e.EventType == CoachEventType.ChosePlan
                          || e.EventType == CoachEventType.StartedPaidPlan)
                group e.CoachUserId by e.EventType into byType
                select new { EventType = byType.Key, Coaches = byType.Distinct().Count() })
            .ToDictionaryAsync(row => row.EventType, row => row.Coaches, ct);

        return new AdminReportFunnelDto
        {
            SignedUp = await signUps.CountAsync(ct),
            EmailVerified = later.GetValueOrDefault(CoachEventType.EmailVerified),
            ChosePlan = later.GetValueOrDefault(CoachEventType.ChosePlan),
            StartedPaidPlan = later.GetValueOrDefault(CoachEventType.StartedPaidPlan),
        };
    }

    private async Task<AdminReportPlanChangesDto> BuildPlanChangesAsync(
        List<EventTotal> cancelled, DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        new()
        {
            Upgraded = ToPlanChange(await LoadEventTotalsAsync(CoachEventType.Upgraded, from, to, ct)),
            Downgraded = ToPlanChange(await LoadEventTotalsAsync(CoachEventType.Downgraded, from, to, ct)),
            Cancelled = ToPlanChange(cancelled),
            PaymentFailedCoaches = await CountDistinctCoachesAsync(CoachEventType.PaymentFailed, from, to, ct),
            PromotionCodesApplied = await db.CoachEvents
                .AsNoTracking()
                .CountAsync(e => e.EventType == CoachEventType.PromotionCodeApplied && e.OccurredAt >= from && e.OccurredAt <= to, ct),
        };

    private static AdminReportPlanChangeDto ToPlanChange(List<EventTotal> totals) =>
        new()
        {
            Count = totals.Sum(row => row.Count),
            RevenueEffect = Amounts(totals.Where(row => row.Currency is not null).Select(row => (row.Currency!, row.Effect))),
        };
}
