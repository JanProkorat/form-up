using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Domain.Services;

/// <summary>
/// Display-only discounted prices. Stripe computes the real charge; this mirrors its rules closely enough to show the coach
/// what to expect. A promotion covers a plan together with that plan's second-role add-on (the add-on price lives on the
/// plan's own Stripe product), so a covered plan has both lines discounted: a percentage is taken off each line, and a
/// fixed amount comes off the plan line first and then the add-on line.
/// </summary>
public static class PromotionPricing
{
    /// <summary>
    /// The discounted plan price and add-on price of a plan the promotion covers. The add-on result is null when the plan
    /// has no add-on.
    /// </summary>
    /// <param name="promotion">The promotion that applies.</param>
    /// <param name="planPriceMinorUnits">The plan price.</param>
    /// <param name="addOnPriceMinorUnits">The second-role add-on price, or null.</param>
    public static (long Plan, long? AddOn) Apply(Promotion promotion, long planPriceMinorUnits, long? addOnPriceMinorUnits)
    {
        if (promotion.Kind == PromotionKind.Percent)
        {
            var addOn = addOnPriceMinorUnits is { } addOnPrice ? Reduce(addOnPrice, promotion.Value) : (long?)null;

            return (Reduce(planPriceMinorUnits, promotion.Value), addOn);
        }

        var planOff = Math.Min(promotion.Value, planPriceMinorUnits);
        var left = promotion.Value - planOff;
        var discountedAddOn = addOnPriceMinorUnits is { } price ? price - Math.Min(left, price) : (long?)null;

        return (planPriceMinorUnits - planOff, discountedAddOn);
    }

    /// <summary>
    /// What a coach's promotion takes off one billing period of their current price lines, and the resulting total. Both
    /// lines are discounted when the promotion covers the coach's plan; neither is when it does not. A fixed amount in
    /// another currency than the plan takes nothing off, because Stripe cannot apply it.
    /// </summary>
    /// <param name="promotion">The coach's current promotion.</param>
    /// <param name="planCode">The coach's plan code.</param>
    /// <param name="currency">ISO 4217 currency of the price lines.</param>
    /// <param name="planPriceMinorUnits">The plan line.</param>
    /// <param name="addOnPriceMinorUnits">The second-role add-on line when the coach pays it, otherwise null.</param>
    public static (long Discount, long DiscountedTotal) ForSubscription(
        Promotion promotion, string planCode, string currency, long planPriceMinorUnits, long? addOnPriceMinorUnits)
    {
        var total = planPriceMinorUnits + (addOnPriceMinorUnits ?? 0);
        var currencyFits = promotion.Kind == PromotionKind.Percent
                           || string.Equals(promotion.Currency, currency, StringComparison.OrdinalIgnoreCase);

        if (!currencyFits || !PromotionRules.CoversPlan(promotion, planCode))
        {
            return (0, total);
        }

        var (plan, addOn) = Apply(promotion, planPriceMinorUnits, addOnPriceMinorUnits);
        var discountedTotal = plan + (addOn ?? 0);

        return (total - discountedTotal, discountedTotal);
    }

    private static long Reduce(long price, long percent) =>
        price - (long)Math.Round(price * percent / 100m, MidpointRounding.AwayFromZero);
}
