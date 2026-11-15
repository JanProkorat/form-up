namespace FormUp.Application.Features.SubscriptionPlans.GetPublicSubscriptionPlans;

/// <summary>
/// Chooses which offered plan carries the "most popular" badge on the public pricing page.
/// </summary>
public static class MostPopularPlanPicker
{
    /// <summary>
    /// Returns the code of the paid plan with the most live, non-gifted coaches, or null when there is no
    /// single leader (no paid plan, no coach on any paid plan, or a tie for the top count).
    /// </summary>
    /// <param name="candidates">The offered plans with their price and live non-gifted coach count.</param>
    public static string? Pick(IEnumerable<(string Code, long PriceMinorUnits, int LiveCoachCount)> candidates)
    {
        var paid = candidates
            .Where(candidate => candidate.PriceMinorUnits > 0)
            .OrderByDescending(candidate => candidate.LiveCoachCount)
            .Take(2)
            .ToList();

        if (paid.Count == 0 || paid[0].LiveCoachCount == 0)
        {
            return null;
        }

        if (paid.Count == 2 && paid[1].LiveCoachCount == paid[0].LiveCoachCount)
        {
            return null;
        }

        return paid[0].Code;
    }
}
