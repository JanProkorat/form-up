using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Domain.Services;

/// <summary>
/// Default <see cref="ICoachRevenueService"/>. Discounts come from <see cref="PromotionPricing.ForSubscription"/>; this
/// class adds no discount formula of its own.
/// </summary>
/// <param name="db">Application database context.</param>
public sealed class CoachRevenueService(IApplicationDbContext db) : ICoachRevenueService
{
    /// <inheritdoc />
    public CoachRevenueAmount MonthlyAmount(
        CoachSubscription subscription,
        DateTimeOffset now,
        CoachRevenueLines lines,
        Promotion? promotion,
        DateTimeOffset? promotionEndsAt)
    {
        if (!BringsRevenue(subscription, lines.PlanCode))
        {
            return CoachRevenueAmount.None;
        }

        var full = lines.PlanMinorUnits + lines.AddOnMinorUnits;
        var discounted = PromotionRuns(promotion, promotionEndsAt, now)
            ? PromotionPricing.ForSubscription(
                promotion!,
                lines.PlanCode,
                lines.Currency,
                lines.Interval,
                lines.PlanMinorUnits,
                lines.AddOnMinorUnits > 0 ? lines.AddOnMinorUnits : null).DiscountedTotal
            : full;

        return new CoachRevenueAmount(lines.Currency, lines.Interval, full, discounted);
    }

    /// <inheritdoc />
    public async Task<CoachRevenueState> CaptureStateAsync(CoachSubscription subscription, DateTimeOffset now, CancellationToken ct)
    {
        var price = await db.SubscriptionPlanPrices
            .AsNoTracking()
            .Include(p => p.SubscriptionPlan)
            .FirstAsync(p => p.Id == subscription.SubscriptionPlanPriceId, ct);

        var promotion = subscription.CurrentPromotionId is { } promotionId
            ? await db.Promotions.AsNoTracking().FirstOrDefaultAsync(p => p.Id == promotionId, ct)
            : null;

        var lines = CoachRevenueLines.Of(price.SubscriptionPlan, price, subscription.PaysSecondRoleAddOn);

        return new CoachRevenueState(
            lines.PlanCode,
            price.Id,
            subscription.PendingSubscriptionPlanPriceId,
            IsPaying(subscription, lines.PlanCode),
            CoachRevenueAmount.ToMonthly(lines.PlanMinorUnits + lines.AddOnMinorUnits, lines.Interval),
            MonthlyAmount(subscription, now, lines, promotion, subscription.CurrentPromotionEndsAt));
    }

    /// <inheritdoc />
    public async Task<CoachRevenueSummary> GetSummaryAsync(DateTimeOffset now, CancellationToken ct)
    {
        var counts = await db.ProfessionalProfiles
            .AsNoTracking()
            .Select(CoachSubscriptionStatusClassifier.ProjectStatus(now))
            .GroupBy(row => row.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.Status, row => row.Count, ct);

        var paying = db.ProfessionalProfiles
            .AsNoTracking()
            .Where(CoachSubscriptionStatusClassifier.Matches(AdminCoachStatusFilter.Paying, now));

        var payingByPlan = await paying
            .GroupBy(profile => new
            {
                profile.CoachSubscription!.SubscriptionPlan.Code,
                profile.CoachSubscription.SubscriptionPlan.NameCs,
                profile.CoachSubscription.SubscriptionPlan.NameEn,
                profile.CoachSubscription.SubscriptionPlan.NameDe,
            })
            .Select(group => new { group.Key.Code, group.Key.NameCs, group.Key.NameEn, group.Key.NameDe, Count = group.Count() })
            .ToListAsync(ct);

        // Each coach pays the price row they were sold, not the plan's current price.
        var payingProfileIds = paying.Select(profile => profile.Id);
        var subscriptions = await db.CoachSubscriptions
            .AsNoTracking()
            .Include(cs => cs.SubscriptionPlan)
            .Include(cs => cs.SubscriptionPlanPrice)
            .Include(cs => cs.CurrentPromotion)
            .Where(cs => payingProfileIds.Contains(cs.ProfessionalProfileId))
            .ToListAsync(ct);

        var priced = subscriptions
            .Select(cs =>
            {
                var lines = CoachRevenueLines.Of(cs.SubscriptionPlan, cs.SubscriptionPlanPrice, cs.PaysSecondRoleAddOn);
                var amount = MonthlyAmount(cs, now, lines, cs.CurrentPromotion, cs.CurrentPromotionEndsAt);

                return (Subscription: cs, Lines: lines, Amount: amount);
            })
            .Where(row => row.Amount.Currency.Length > 0)
            .ToList();
        var amounts = priced.Select(row => row.Amount).ToList();

        int Count(AdminCoachStatus status) => counts.GetValueOrDefault(status);

        return new CoachRevenueSummary(
            counts.Values.Sum(),
            Count(AdminCoachStatus.Paying) + Count(AdminCoachStatus.PaymentFailed) + Count(AdminCoachStatus.Cancelling),
            Count(AdminCoachStatus.PaymentFailed),
            Count(AdminCoachStatus.Cancelling),
            Count(AdminCoachStatus.Free),
            Count(AdminCoachStatus.GivenFree),
            payingByPlan
                .OrderByDescending(row => row.Count)
                .ThenBy(row => row.Code, StringComparer.Ordinal)
                .Select(row => new CoachRevenuePlanCount(row.Code, row.NameCs, row.NameEn, row.NameDe, row.Count))
                .ToList(),
            SumPerCurrency(amounts, amount => amount.FullPeriodMinorUnits),
            SumPerCurrency(amounts, amount => amount.DiscountedPeriodMinorUnits))
        {
            ByCurrency = BuildCurrencyDetails(priced, now),
        };
    }

    private static bool PromotionRuns(Promotion? promotion, DateTimeOffset? promotionEndsAt, DateTimeOffset now) =>
        promotion is not null && (promotionEndsAt is not { } endsAt || endsAt > now);

    /// <summary>
    /// Splits each billed subscription's discounted period amount into its plan line and add-on line with the same
    /// <see cref="PromotionPricing.Apply"/> split <see cref="PromotionPricing.ForSubscription"/> uses, then sums per
    /// currency and plan.
    /// </summary>
    private static List<CoachRevenueCurrencyDetail> BuildCurrencyDetails(
        List<(CoachSubscription Subscription, CoachRevenueLines Lines, CoachRevenueAmount Amount)> priced,
        DateTimeOffset now)
    {
        var split = priced
            .Select(row =>
            {
                var (plan, addOn) = DiscountedLines(row.Subscription, row.Lines, now);

                return (row.Lines, row.Amount, DiscountedPlan: plan, DiscountedAddOn: addOn);
            })
            .ToList();

        return split
            .GroupBy(row => row.Amount.Currency)
            .Select(currency =>
            {
                var withAddOn = currency.Where(row => row.Lines.AddOnMinorUnits > 0).ToList();

                return new CoachRevenueCurrencyDetail(
                    currency.Key,
                    currency.Count(),
                    withAddOn.Count,
                    SumMonthly(withAddOn.Select(row => (row.Amount.Interval, row.DiscountedAddOn))),
                    currency
                        .GroupBy(row => row.Lines.PlanCode)
                        .Select(plan => new CoachRevenuePlanAmount(
                            plan.Key, SumMonthly(plan.Select(row => (row.Amount.Interval, row.DiscountedPlan)))))
                        .OrderBy(plan => plan.PlanCode, StringComparer.Ordinal)
                        .ToList());
            })
            .OrderBy(detail => detail.Currency, StringComparer.Ordinal)
            .ToList();
    }

    private static (long Plan, long AddOn) DiscountedLines(CoachSubscription subscription, CoachRevenueLines lines, DateTimeOffset now)
    {
        if (subscription.CurrentPromotion is { } promotion
            && PromotionRuns(promotion, subscription.CurrentPromotionEndsAt, now)
            && PromotionPricing.Applies(promotion, lines.PlanCode, lines.Currency, lines.Interval))
        {
            var (plan, addOn) = PromotionPricing.Apply(
                promotion, lines.PlanMinorUnits, lines.AddOnMinorUnits > 0 ? lines.AddOnMinorUnits : null);

            return (plan, addOn ?? 0);
        }

        return (lines.PlanMinorUnits, lines.AddOnMinorUnits);
    }

    /// <summary>
    /// Sums period amounts to a monthly figure, dividing the annual part by twelve once (like <see cref="SumPerCurrency"/>).
    /// </summary>
    private static long SumMonthly(IEnumerable<(BillingInterval Interval, long Period)> rows)
    {
        var list = rows.ToList();

        return CoachRevenueAmount.ToMonthly(
                   list.Where(row => row.Interval == BillingInterval.Monthly).Sum(row => row.Period), BillingInterval.Monthly)
               + CoachRevenueAmount.ToMonthly(
                   list.Where(row => row.Interval == BillingInterval.Annual).Sum(row => row.Period), BillingInterval.Annual);
    }

    /// <summary>
    /// Sums period amounts per currency. Monthly and annual totals are kept apart and the annual one is divided by twelve
    /// once, so many annual coaches do not each lose a rounding remainder.
    /// </summary>
    private static List<CoachRevenueCurrencyTotal> SumPerCurrency(
        List<CoachRevenueAmount> amounts, Func<CoachRevenueAmount, long> period) =>
        amounts
            .GroupBy(amount => amount.Currency)
            .Select(group => new CoachRevenueCurrencyTotal(
                group.Key,
                CoachRevenueAmount.ToMonthly(
                    group.Where(amount => amount.Interval == BillingInterval.Monthly).Sum(period), BillingInterval.Monthly)
                + CoachRevenueAmount.ToMonthly(
                    group.Where(amount => amount.Interval == BillingInterval.Annual).Sum(period), BillingInterval.Annual)))
            .OrderBy(total => total.Currency, StringComparer.Ordinal)
            .ToList();

    private static bool IsPaying(CoachSubscription subscription, string planCode) =>
        subscription.GivenFreeAt is null
        && planCode != SubscriptionPlanCodes.Free
        && subscription.Status is SubscriptionStatus.Trialing or SubscriptionStatus.Active or SubscriptionStatus.PastDue;

    private static bool BringsRevenue(CoachSubscription subscription, string planCode) =>
        IsPaying(subscription, planCode) && subscription.Status != SubscriptionStatus.Trialing;
}
