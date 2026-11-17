using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Infrastructure.Services.Billing;

/// <summary>
/// Pure phase building for the one subscription schedule a coach's Stripe subscription may have. A given-free end date and
/// a price change at renewal both need phase boundaries, so they are merged into one list instead of overwriting each other.
/// </summary>
public static class StripeSchedulePlanner
{
    /// <summary>
    /// Builds the phases from the current one onward. Returns an empty list when no schedule is needed: no pending price
    /// change and a gift that never ends (or no gift), which a plain subscription discount covers.
    /// </summary>
    /// <param name="currentStart">Start of the current billing phase.</param>
    /// <param name="currentEnd">End of the current billing phase, which is the next renewal.</param>
    /// <param name="currentPriceIds">Prices billed now.</param>
    /// <param name="intent">The price change and gift to achieve.</param>
    /// <param name="interval">Billing cadence, used to find the renewals that fall inside a gift.</param>
    /// <remarks>
    /// A gift ends at the end of a calendar day, but the paid period renews at the purchase time of day. A renewal that
    /// falls on the gift's last day (within 24 hours before its end) is charged in full: the gift phase then ends at that
    /// renewal instead of the end of the day. When the renewal is cancelled the schedule ends in one phase and cancels at
    /// that renewal, or at the end of the period already paid when that is later.
    /// </remarks>
    public static IReadOnlyList<SchedulePhasePlan> Build(
        DateTimeOffset currentStart,
        DateTimeOffset currentEnd,
        IReadOnlyList<string> currentPriceIds,
        ScheduleIntent intent,
        BillingInterval interval = BillingInterval.Monthly)
    {
        var giftActive = intent.GiftCouponId is not null && (intent.GiftEnd is null || intent.GiftEnd > currentStart);
        var giftEnd = giftActive ? intent.GiftEnd : null;
        var pending = intent.PendingPriceIds;

        if (pending is null && giftEnd is null)
        {
            return [];
        }

        if (giftEnd is { } rawGiftEnd)
        {
            giftEnd = AlignGiftEnd(currentEnd, rawGiftEnd, interval);
        }

        if (intent.CancelRenewal)
        {
            var cancelPoint = giftEnd is { } end && end > currentEnd ? end : currentEnd;

            return [new SchedulePhasePlan(currentStart, cancelPoint, currentPriceIds, giftActive ? intent.GiftCouponId : null)];
        }

        var boundaries = new List<DateTimeOffset>();

        if (pending is not null)
        {
            boundaries.Add(currentEnd);
        }

        if (giftEnd is { } giftBoundary)
        {
            boundaries.Add(giftBoundary);
        }

        var phases = new List<SchedulePhasePlan>();
        var start = currentStart;

        foreach (var boundary in boundaries.Where(b => b > currentStart).Distinct().Order())
        {
            phases.Add(PhaseAt(start, boundary));
            start = boundary;
        }

        phases.Add(PhaseAt(start, null));

        return phases;

        SchedulePhasePlan PhaseAt(DateTimeOffset phaseStart, DateTimeOffset? phaseEnd) => new(
            phaseStart,
            phaseEnd,
            pending is not null && phaseStart >= currentEnd ? pending : currentPriceIds,
            giftActive && (giftEnd is null || phaseStart < giftEnd) ? intent.GiftCouponId : null);
    }

    /// <summary>
    /// Reads back what an existing schedule already promises, so a new change can be merged with it. The first phase is the
    /// current one. A gift on the last phase has no end; a gift that stops earlier ends where its last phase ends.
    /// </summary>
    /// <param name="phases">The schedule's phases from the current one onward.</param>
    /// <param name="cancelRenewal">True when the schedule cancels after its last phase; that phase then has an end.</param>
    public static ScheduleIntent Read(IReadOnlyList<SchedulePhasePlan> phases, bool cancelRenewal = false)
    {
        if (phases.Count == 0)
        {
            return new ScheduleIntent(null, null, null, cancelRenewal);
        }

        var current = phases[0].PriceIds.Order(StringComparer.Ordinal).ToList();
        var pending = phases
            .Skip(1)
            .Select(phase => phase.PriceIds)
            .FirstOrDefault(ids => !ids.Order(StringComparer.Ordinal).SequenceEqual(current));

        var lastGiftIndex = -1;

        for (var i = 0; i < phases.Count; i++)
        {
            if (phases[i].CouponId is not null)
            {
                lastGiftIndex = i;
            }
        }

        if (lastGiftIndex < 0)
        {
            return new ScheduleIntent(pending, null, null, cancelRenewal);
        }

        var couponId = phases[lastGiftIndex].CouponId;
        var giftEnd = lastGiftIndex == phases.Count - 1 && !cancelRenewal ? null : phases[lastGiftIndex].End;

        return new ScheduleIntent(pending, couponId, giftEnd, cancelRenewal);
    }

    private static DateTimeOffset AlignGiftEnd(DateTimeOffset firstRenewal, DateTimeOffset giftEnd, BillingInterval interval)
    {
        var aligned = giftEnd;
        var windowStart = giftEnd.AddDays(-1);

        // UNVERIFIED: later renewals fall on the first renewal plus whole months or years, as a monthly or yearly billing anchor does — https://docs.stripe.com/billing/subscriptions/billing-cycle
        for (var step = 0; ; step++)
        {
            var renewal = interval == BillingInterval.Annual ? firstRenewal.AddYears(step) : firstRenewal.AddMonths(step);

            if (renewal > giftEnd)
            {
                return aligned;
            }

            if (renewal > windowStart)
            {
                aligned = renewal;
            }
        }
    }
}
