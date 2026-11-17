using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Domain.Services;

/// <summary>
/// Works out when a coach account would stop being active if the holder disabled it now.
/// </summary>
public static class CoachAccountActiveUntilResolver
{
    /// <summary>
    /// Returns the paid period end (trial end for a trialing row), or <paramref name="now"/> when there is
    /// no subscription, no end date, or the end already passed. A row given free by an admin returns the later of
    /// its free-until date (or now) and the paid period end, the latter only while the provider subscription is live.
    /// </summary>
    /// <param name="subscription">The professional's subscription, or null when none exists.</param>
    /// <param name="now">Current UTC time.</param>
    public static DateTime Resolve(CoachSubscription? subscription, DateTime now)
    {
        if (subscription?.GivenFreeAt is not null)
        {
            var giftEnd = subscription.FreeUntil?.UtcDateTime ?? now;
            var paidEnd = subscription.HasLiveProviderSubscription && subscription.CurrentPeriodEndsAt is { } stored
                ? stored.UtcDateTime
                : now;

            return new[] { giftEnd, paidEnd, now }.Max();
        }

        var periodEnd = subscription?.Status == SubscriptionStatus.Trialing
            ? subscription.TrialEndsAt
            : subscription?.CurrentPeriodEndsAt;

        return periodEnd is { } end && end.UtcDateTime > now ? end.UtcDateTime : now;
    }
}
