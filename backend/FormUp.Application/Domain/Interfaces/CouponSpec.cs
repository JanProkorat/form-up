using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Domain.Interfaces;

/// <summary>
/// What the provider needs to create a discount coupon for a promotion.
/// </summary>
/// <param name="Name">Name shown on the provider's side.</param>
/// <param name="Kind">Percentage or fixed amount.</param>
/// <param name="Value">Percent (1 to 100), or the amount in the smallest unit of <paramref name="Currency"/>.</param>
/// <param name="Currency">ISO 4217 currency of a fixed amount; null for a percentage.</param>
/// <param name="Duration">How long the discount lasts for a coach.</param>
/// <param name="DurationMonths">Months for <see cref="PromotionDuration.Months"/>; otherwise null.</param>
/// <param name="MaxUses">Most redemptions the provider allows, or null for no limit.</param>
/// <param name="ClaimUntil">Last moment the coupon can be redeemed, or null for no end.</param>
/// <param name="PlanPriceExternalIds">Provider price ids of the covered plans; null leaves the coupon unrestricted.</param>
/// <param name="IdempotencyKey">Repeating a call with the same key returns the first coupon instead of a second one.</param>
public sealed record CouponSpec(
    string Name,
    PromotionKind Kind,
    long Value,
    string? Currency,
    PromotionDuration Duration,
    int? DurationMonths,
    int? MaxUses,
    DateTimeOffset? ClaimUntil,
    IReadOnlyList<string>? PlanPriceExternalIds,
    string IdempotencyKey);
