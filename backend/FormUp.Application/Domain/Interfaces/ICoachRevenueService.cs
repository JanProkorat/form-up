using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Services;

namespace FormUp.Application.Domain.Interfaces;

/// <summary>
/// The one place that turns coach subscriptions into revenue: what a single subscription pays per month (full price and
/// after its promotion), and the status counts and monthly totals over all coaches. The admin Coaches stats, the daily
/// snapshot and the coach event revenue effects all read it, so they cannot drift apart.
/// </summary>
public interface ICoachRevenueService
{
    /// <summary>
    /// What a subscription brings per billing period for the given price lines and promotion. Zero unless the status is
    /// Active or PastDue, the plan is a paid one and no admin gift is in force (trialing coaches bring no revenue yet).
    /// A promotion that has ended or does not cover the plan or interval takes nothing off.
    /// </summary>
    /// <param name="subscription">The subscription, read for its status and gift marker.</param>
    /// <param name="now">Current time.</param>
    /// <param name="lines">The price lines to price.</param>
    /// <param name="promotion">The promotion discounting the subscription, or null.</param>
    /// <param name="promotionEndsAt">When the promotion's discount ends, or null for no end.</param>
    CoachRevenueAmount MonthlyAmount(
        CoachSubscription subscription,
        DateTimeOffset now,
        CoachRevenueLines lines,
        Promotion? promotion,
        DateTimeOffset? promotionEndsAt);

    /// <summary>
    /// Reads the subscription's current plan, price and promotion from the database and prices them. Works on a row that
    /// is not saved yet and does not touch the tracked entities.
    /// </summary>
    /// <param name="subscription">The subscription row.</param>
    /// <param name="now">Current time.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<CoachRevenueState> CaptureStateAsync(CoachSubscription subscription, DateTimeOffset now, CancellationToken ct);

    /// <summary>
    /// Counts coaches per status bucket and sums the monthly revenue per currency, at full price and after promotions.
    /// </summary>
    /// <param name="now">Current time.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<CoachRevenueSummary> GetSummaryAsync(DateTimeOffset now, CancellationToken ct);
}
