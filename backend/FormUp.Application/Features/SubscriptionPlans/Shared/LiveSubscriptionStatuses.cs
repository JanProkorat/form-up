using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Features.SubscriptionPlans.Shared;

/// <summary>
/// The subscription statuses that count a coach as being on a plan.
/// </summary>
public static class LiveSubscriptionStatuses
{
    /// <summary>Trialing, active and past-due subscriptions.</summary>
    public static readonly SubscriptionStatus[] All =
    [
        SubscriptionStatus.Trialing,
        SubscriptionStatus.Active,
        SubscriptionStatus.PastDue,
    ];
}
