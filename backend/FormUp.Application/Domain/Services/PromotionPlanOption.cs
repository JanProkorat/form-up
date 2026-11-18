namespace FormUp.Application.Domain.Services;

/// <summary>
/// An active paid plan an admin can attach a promotion to.
/// </summary>
/// <param name="Code">The plan code.</param>
/// <param name="StripePriceId">The provider price id of the plan's open price row; null or empty when it has none.</param>
public sealed record PromotionPlanOption(string Code, string? StripePriceId)
{
    /// <summary>
    /// True when Checkout can sell the plan, which needs a provider price. A plan without one never needs a coupon
    /// restriction because nobody can buy it.
    /// </summary>
    public bool IsPurchasable => !string.IsNullOrEmpty(StripePriceId);
}
