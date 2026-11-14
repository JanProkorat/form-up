namespace FormUp.Application.Domain.Services;

/// <summary>
/// Decides which plans a new promotion covers and whether its Stripe coupon needs a product restriction. A coupon with no
/// restriction discounts every plan, so it is only left unrestricted when the promotion covers every plan Checkout can sell.
/// A restricted coupon names the products of the selected plans; the second-role add-on price lives on its plan's product,
/// so it is covered either way.
/// </summary>
public static class PromotionPlanScope
{
    /// <summary>
    /// Resolves the requested plan codes against the active paid plans. Returns null when a code is not one of them, or
    /// when the promotion covers only some purchasable plans and a selected plan has no provider price to restrict the
    /// coupon to.
    /// </summary>
    /// <param name="activePaidPlans">Every active paid plan with its provider price, if any.</param>
    /// <param name="requestedCodes">The plan codes the admin selected; compared ignoring case, duplicates collapsed.</param>
    public static (IReadOnlyList<PromotionPlanOption> Selected, bool CoversEveryPurchasablePlan)? Resolve(
        IReadOnlyList<PromotionPlanOption> activePaidPlans, IEnumerable<string> requestedCodes)
    {
        var requested = requestedCodes
            .Select(code => code.Trim().ToLowerInvariant())
            .Distinct()
            .ToList();
        var selected = activePaidPlans
            .Where(plan => requested.Contains(plan.Code.ToLowerInvariant()))
            .ToList();

        if (selected.Count != requested.Count)
        {
            return null;
        }

        var purchasable = activePaidPlans.Where(plan => plan.IsPurchasable).ToList();
        var coversEveryPurchasablePlan = purchasable.Count > 0 && purchasable.All(selected.Contains);

        if (!coversEveryPurchasablePlan && selected.Any(plan => !plan.IsPurchasable))
        {
            return null;
        }

        return (selected, coversEveryPurchasablePlan);
    }
}
