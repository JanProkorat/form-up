using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Domain.Services;

/// <summary>
/// The derived state of a <see cref="Promotion"/>: status and plan coverage. One place, so the admin list, checkout,
/// the plans read and the code endpoint agree.
/// </summary>
public static class PromotionRules
{
    /// <summary>
    /// Ended when an admin ended it, after the claim window closes or once every use is taken; Scheduled before the
    /// window opens; otherwise Active.
    /// </summary>
    /// <param name="promotion">The promotion.</param>
    /// <param name="usedCount">How many coaches claimed it.</param>
    /// <param name="now">The current instant.</param>
    public static PromotionStatus StatusAt(Promotion promotion, int usedCount, DateTimeOffset now)
    {
        if (promotion.EndedAt is not null || HasClaimWindowClosed(promotion, now) || IsUsedUp(promotion, usedCount))
        {
            return PromotionStatus.Ended;
        }

        return now < promotion.ClaimFrom ? PromotionStatus.Scheduled : PromotionStatus.Active;
    }

    /// <summary>True when the claim window has a close and it has passed.</summary>
    /// <param name="promotion">The promotion.</param>
    /// <param name="now">The current instant.</param>
    public static bool HasClaimWindowClosed(Promotion promotion, DateTimeOffset now) =>
        promotion.ClaimUntil is { } until && now > until;

    /// <summary>True when the promotion has a use limit and the coaches who claimed it reach it.</summary>
    /// <param name="promotion">The promotion.</param>
    /// <param name="usedCount">How many coaches claimed it.</param>
    public static bool IsUsedUp(Promotion promotion, int usedCount) =>
        promotion.MaxUses is { } max && usedCount >= max;

    /// <summary>True when the plan is one the promotion covers.</summary>
    /// <param name="promotion">The promotion.</param>
    /// <param name="planCode">The plan code.</param>
    public static bool CoversPlan(Promotion promotion, string planCode) =>
        promotion.PlanCodes.Contains(planCode, StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the promotion discounts subscriptions billed at the given interval.</summary>
    /// <param name="promotion">The promotion.</param>
    /// <param name="interval">The billing interval.</param>
    public static bool CoversInterval(Promotion promotion, BillingInterval interval) =>
        promotion.BillingPeriod switch
        {
            PromotionBillingPeriod.Monthly => interval == BillingInterval.Monthly,
            PromotionBillingPeriod.Annual => interval == BillingInterval.Annual,
            _ => true,
        };

    /// <summary>
    /// The Stripe coupon to attach for a billing interval, or null when the promotion does not cover it. An annual interval
    /// uses the annual coupon when the promotion has one; every other case uses the single coupon. Every place that attaches
    /// a coupon goes through here, so no one can attach the monthly coupon to a yearly subscription.
    /// </summary>
    /// <param name="promotion">The promotion.</param>
    /// <param name="interval">The billing interval of the subscription or checkout.</param>
    public static string? CouponIdFor(Promotion promotion, BillingInterval interval)
    {
        if (!CoversInterval(promotion, interval))
        {
            return null;
        }

        return interval == BillingInterval.Annual && !string.IsNullOrEmpty(promotion.StripeAnnualCouponId)
            ? promotion.StripeAnnualCouponId
            : promotion.StripeCouponId;
    }

    /// <summary>True when a promotion of this shape needs two coupons, one per interval.</summary>
    /// <param name="billingPeriod">Which cadences the promotion covers.</param>
    /// <param name="duration">How long the discount lasts.</param>
    public static bool NeedsAnnualCoupon(PromotionBillingPeriod billingPeriod, PromotionDuration duration) =>
        billingPeriod == PromotionBillingPeriod.Both && duration != PromotionDuration.Forever;
}
