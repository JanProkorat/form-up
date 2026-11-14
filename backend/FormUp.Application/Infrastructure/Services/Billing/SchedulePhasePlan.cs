namespace FormUp.Application.Infrastructure.Services.Billing;

/// <summary>
/// One phase of a subscription schedule: which prices are billed, whether the 100 % gift coupon applies, and when the
/// phase ends. The last phase has no end; it runs one billing interval and then the schedule releases.
/// </summary>
/// <param name="Start">When the phase starts.</param>
/// <param name="End">When the phase ends, or null for the open last phase.</param>
/// <param name="PriceIds">The provider price ids billed in the phase.</param>
/// <param name="CouponId">The gift coupon applied in the phase, or null for no discount.</param>
public sealed record SchedulePhasePlan(
    DateTimeOffset Start,
    DateTimeOffset? End,
    IReadOnlyList<string> PriceIds,
    string? CouponId);
