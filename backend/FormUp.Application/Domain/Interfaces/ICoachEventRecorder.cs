using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Services;

namespace FormUp.Application.Domain.Interfaces;

/// <summary>
/// Adds <see cref="CoachEvent"/> rows to the request's database context. It never saves: the caller's own
/// <c>SaveChangesAsync</c> commits the event together with the change that caused it (in the Stripe webhook, together with
/// the processed-event row), so a failed or duplicate request leaves no event behind. An idempotency key that already
/// exists, saved or pending, writes nothing.
/// </summary>
public interface ICoachEventRecorder
{
    /// <summary>
    /// Adds one event unless its key is already taken.
    /// </summary>
    /// <param name="type">What happened.</param>
    /// <param name="coachUserId">The coach's user id.</param>
    /// <param name="idempotencyKey">Unique key of this occurrence.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="planCode">The plan after the event, where it applies.</param>
    /// <param name="previousPlanCode">The plan before the event, where it applies.</param>
    /// <param name="before">Revenue before the event; null counts as none.</param>
    /// <param name="after">Revenue after the event; null counts as none.</param>
    /// <returns>True when a row was added.</returns>
    Task<bool> RecordAsync(
        CoachEventType type,
        Guid coachUserId,
        string idempotencyKey,
        CancellationToken ct,
        string? planCode = null,
        string? previousPlanCode = null,
        CoachRevenueAmount? before = null,
        CoachRevenueAmount? after = null);

    /// <summary>
    /// Compares a subscription row the webhook has just updated with its captured earlier state and adds the event it
    /// amounts to: StartedPaidPlan when the coach was not paying and now is, Upgraded or Downgraded when the price row
    /// changed. Nothing is added when the plan and price are unchanged.
    /// </summary>
    /// <param name="stripeEventId">Id of the webhook event, the first part of the key.</param>
    /// <param name="before">State captured before the row was changed.</param>
    /// <param name="row">The updated row.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RecordSubscriptionChangeAsync(string stripeEventId, CoachRevenueState before, CoachSubscription row, CancellationToken ct);

    /// <summary>
    /// Adds a Cancelled event, with the coach's earlier revenue as a negative effect, when the coach was paying.
    /// </summary>
    /// <param name="stripeEventId">Id of the webhook event, the first part of the key.</param>
    /// <param name="before">State captured before the row was changed.</param>
    /// <param name="row">The updated row.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RecordCancellationAsync(string stripeEventId, CoachRevenueState before, CoachSubscription row, CancellationToken ct);

    /// <summary>
    /// Adds a PaymentFailed event for the coach of the row.
    /// </summary>
    /// <param name="stripeEventId">Id of the webhook event, the first part of the key.</param>
    /// <param name="row">The coach's subscription row.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RecordPaymentFailedAsync(string stripeEventId, CoachSubscription row, CancellationToken ct);

    /// <summary>
    /// Adds a PromotionCodeApplied event for the coach of the row, with the drop in monthly revenue the promotion causes
    /// (the promotion is priced as running with no end, since the provider's end date is not known yet).
    /// </summary>
    /// <param name="row">The coach's subscription row, before the promotion is mirrored onto it.</param>
    /// <param name="promotion">The promotion that was applied.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RecordPromotionAppliedAsync(CoachSubscription row, Promotion promotion, CancellationToken ct);

    /// <summary>
    /// Captures the revenue state of a subscription row before a change, for the Record methods above.
    /// </summary>
    /// <param name="row">The coach's subscription row.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<CoachRevenueState> CaptureAsync(CoachSubscription row, CancellationToken ct);
}
