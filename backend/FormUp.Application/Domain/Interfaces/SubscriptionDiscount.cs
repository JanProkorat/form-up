namespace FormUp.Application.Domain.Interfaces;

/// <summary>
/// The discount a provider subscription carries now.
/// </summary>
/// <param name="CouponId">The provider coupon id, or null when unknown.</param>
/// <param name="PromotionCodeId">The provider promotion code id when the discount came from a code; otherwise null.</param>
/// <param name="EndsAt">When the discount stops, or null for a forever discount.</param>
public sealed record SubscriptionDiscount(string? CouponId, string? PromotionCodeId, DateTimeOffset? EndsAt);
