namespace FormUp.Application.Infrastructure.Services.Billing;

/// <summary>
/// Pure phase building for the one subscription schedule a coach's Stripe subscription may have. The schedule exists only
/// to bill a different price from the next renewal on, optionally ending the subscription there.
/// </summary>
public static class StripeSchedulePlanner
{
    /// <summary>
    /// Builds the phases from the current one onward. Returns an empty list when no schedule is needed, which is when no
    /// price change is pending. A cancelled renewal with a pending price change ends in the current phase, because a
    /// coach who is leaving never reaches the new price.
    /// </summary>
    /// <param name="currentStart">Start of the current billing phase.</param>
    /// <param name="currentEnd">End of the current billing phase, which is the next renewal.</param>
    /// <param name="currentPriceIds">Prices billed now.</param>
    /// <param name="intent">The price change to achieve.</param>
    public static IReadOnlyList<SchedulePhasePlan> Build(
        DateTimeOffset currentStart,
        DateTimeOffset currentEnd,
        IReadOnlyList<string> currentPriceIds,
        ScheduleIntent intent)
    {
        if (intent.PendingPriceIds is not { } pending)
        {
            return [];
        }

        var current = new SchedulePhasePlan(currentStart, currentEnd, currentPriceIds);

        return intent.CancelRenewal
            ? [current]
            : [current, new SchedulePhasePlan(currentEnd, null, pending)];
    }

    /// <summary>
    /// Reads back what an existing schedule already promises, so a new change can be merged with it. The first phase is the
    /// current one.
    /// </summary>
    /// <param name="phases">The schedule's phases from the current one onward.</param>
    /// <param name="cancelRenewal">True when the schedule cancels after its last phase.</param>
    public static ScheduleIntent Read(IReadOnlyList<SchedulePhasePlan> phases, bool cancelRenewal = false)
    {
        if (phases.Count == 0)
        {
            return new ScheduleIntent(null, cancelRenewal);
        }

        var current = phases[0].PriceIds.Order(StringComparer.Ordinal).ToList();
        var pending = phases
            .Skip(1)
            .Select(phase => phase.PriceIds)
            .FirstOrDefault(ids => !ids.Order(StringComparer.Ordinal).SequenceEqual(current));

        return new ScheduleIntent(pending, cancelRenewal);
    }
}
