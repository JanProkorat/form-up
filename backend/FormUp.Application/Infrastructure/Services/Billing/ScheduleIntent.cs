namespace FormUp.Application.Infrastructure.Services.Billing;

/// <summary>
/// What a subscription's schedule must achieve: an optional price change at the next renewal and an optional gift.
/// A gift is a coupon id, ending on <paramref name="GiftEnd"/>, or never when that is null.
/// </summary>
/// <param name="PendingPriceIds">Prices billed from the next renewal on, or null for no price change.</param>
/// <param name="GiftCouponId">The 100 % gift coupon, or null for no gift.</param>
/// <param name="GiftEnd">When the gift ends, or null for a gift without an end.</param>
public sealed record ScheduleIntent(
    IReadOnlyList<string>? PendingPriceIds,
    string? GiftCouponId,
    DateTimeOffset? GiftEnd);
