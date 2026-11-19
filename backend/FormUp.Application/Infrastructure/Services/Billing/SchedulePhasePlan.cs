using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Infrastructure.Services.Billing;

/// <summary>
/// One phase of a subscription schedule: which prices are billed and when the phase ends. The last phase has no end; it
/// runs one billing interval of its own prices and then the schedule releases.
/// </summary>
/// <param name="Start">When the phase starts.</param>
/// <param name="End">When the phase ends, or null for the open last phase.</param>
/// <param name="PriceIds">The provider price ids billed in the phase.</param>
/// <param name="Interval">
/// The billing interval of <paramref name="PriceIds"/> when known. The open last phase needs it; when it is null the service
/// reads it from the phase's own price before sending the schedule.
/// </param>
public sealed record SchedulePhasePlan(
    DateTimeOffset Start,
    DateTimeOffset? End,
    IReadOnlyList<string> PriceIds,
    BillingInterval? Interval = null);
