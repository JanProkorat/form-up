namespace FormUp.Application.Domain.Interfaces;

/// <summary>
/// What a plan change does to the subscription's discount in the same provider update. A null change leaves the discount
/// alone; <see cref="Clear"/> removes it; <see cref="Replace"/> makes one coupon the only discount.
/// </summary>
/// <param name="CouponId">The provider coupon id that becomes the only discount, or null to remove every discount.</param>
public sealed record SubscriptionDiscountChange(string? CouponId)
{
    /// <summary>Removes the subscription's discount.</summary>
    public static SubscriptionDiscountChange Clear { get; } = new((string?)null);

    /// <summary>Makes the coupon the subscription's only discount.</summary>
    /// <param name="couponId">The provider coupon id.</param>
    public static SubscriptionDiscountChange Replace(string couponId) => new(couponId);
}
