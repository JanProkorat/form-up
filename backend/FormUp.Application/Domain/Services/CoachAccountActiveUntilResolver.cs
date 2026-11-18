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
    /// no subscription, no end date, or the end already passed. A row given free by an admin returns its free-until
    /// date (or now when it has none or it passed), because a given plan and a paid subscription never run together.
    /// </summary>
    /// <param name="subscription">The professional's subscription, or null when none exists.</param>
    /// <param name="now">Current UTC time.</param>
    public static DateTime Resolve(CoachSubscription? subscription, DateTime now)
    {
        if (subscription?.GivenFreeAt is not null)
        {
            var giftEnd = subscription.FreeUntil?.UtcDateTime ?? now;

            return giftEnd > now ? giftEnd : now;
        }

        var periodEnd = subscription?.Status == SubscriptionStatus.Trialing
            ? subscription.TrialEndsAt
            : subscription?.CurrentPeriodEndsAt;

        return periodEnd is { } end && end.UtcDateTime > now ? end.UtcDateTime : now;
    }
}
