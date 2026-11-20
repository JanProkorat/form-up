using FormUp.Application.Domain.Common;
using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Domain.Entities;

/// <summary>
/// One row of the coach event history behind the admin Reports funnel and plan-change figures. Holds the coach and the
/// plan facts only, never a client's data. History starts when the table ships; nothing is backfilled.
/// </summary>
public class CoachEvent : TimestampableEntity
{
    /// <summary>
    /// The coach the event is about. Null once the coach account is deleted; <see cref="AnonymousCoachKey"/> then keeps
    /// the coach's events together without pointing at the person.
    /// </summary>
    public Guid? CoachUserId { get; set; }

    /// <summary>
    /// Random per-coach key set on all of a coach's events when the account is deleted, never derived from the user id.
    /// Null while the account exists, and on events that were orphaned before the key existed.
    /// </summary>
    public Guid? AnonymousCoachKey { get; set; }

    /// <summary>
    /// What happened.
    /// </summary>
    public CoachEventType EventType { get; set; }

    /// <summary>
    /// When it happened (UTC).
    /// </summary>
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>
    /// Dedupe key, unique: <c>signup:&lt;userId&gt;</c>, <c>verify:&lt;userId&gt;</c>, <c>change:&lt;attemptId&gt;</c>,
    /// <c>checkout:&lt;profileId&gt;:&lt;plan&gt;:&lt;interval&gt;:&lt;day&gt;</c>, <c>free:&lt;profileId&gt;:&lt;day&gt;</c>,
    /// <c>promo:&lt;profileId&gt;:&lt;promotionId&gt;</c> or <c>&lt;stripeEventId&gt;:&lt;eventType&gt;</c> for webhook events.
    /// </summary>
    public required string IdempotencyKey { get; set; }

    /// <summary>
    /// Code of the plan the coach is on after the event, where it applies.
    /// </summary>
    public string? PlanCode { get; set; }

    /// <summary>
    /// Code of the plan the coach was on before the event, where it applies.
    /// </summary>
    public string? PreviousPlanCode { get; set; }

    /// <summary>
    /// ISO 4217 currency of the revenue effect, null when the event has none.
    /// </summary>
    public string? Currency { get; set; }

    /// <summary>
    /// Change in the coach's monthly revenue at full price, in minor units (after minus before; a cancellation is
    /// negative). Annual prices count as one twelfth.
    /// </summary>
    public long FullMonthlyRevenueEffectMinorUnits { get; set; }

    /// <summary>
    /// Change in the coach's monthly revenue after promotions, in minor units, measured like the full-price effect.
    /// </summary>
    public long DiscountedMonthlyRevenueEffectMinorUnits { get; set; }
}
