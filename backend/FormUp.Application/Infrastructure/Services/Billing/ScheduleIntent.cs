namespace FormUp.Application.Infrastructure.Services.Billing;

/// <summary>
/// What a subscription's schedule must achieve: an optional price change at the next renewal. A cancelled renewal must
/// survive every rebuild.
/// </summary>
/// <param name="PendingPriceIds">Prices billed from the next renewal on, or null for no price change.</param>
/// <param name="CancelRenewal">True when the subscription must end instead of renewing after the schedule.</param>
public sealed record ScheduleIntent(
    IReadOnlyList<string>? PendingPriceIds,
    bool CancelRenewal = false);
