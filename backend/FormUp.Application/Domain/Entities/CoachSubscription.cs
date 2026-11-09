using FormUp.Application.Domain.Common;
using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Domain.Entities;

/// <summary>
/// Tracks a single professional's subscription to a <see cref="SubscriptionPlan"/>.
/// At most one row exists per <see cref="ProfessionalProfileId"/>.
/// </summary>
public class CoachSubscription : TimestampableEntity
{
    /// <summary>
    /// The professional this subscription belongs to. Unique — a professional has
    /// zero or one subscription.
    /// </summary>
    public required long ProfessionalProfileId { get; set; }

    /// <summary>
    /// Navigation property to the professional this subscription belongs to.
    /// </summary>
    public ProfessionalProfile ProfessionalProfile { get; set; } = null!;

    /// <summary>
    /// Foreign key to the plan this subscription is currently on.
    /// </summary>
    public required long SubscriptionPlanId { get; set; }

    /// <summary>
    /// Navigation property to the plan this subscription is currently on.
    /// </summary>
    public SubscriptionPlan SubscriptionPlan { get; set; } = null!;

    /// <summary>
    /// Foreign key to the price row the coach currently pays.
    /// </summary>
    public required long SubscriptionPlanPriceId { get; set; }

    /// <summary>
    /// Navigation property to the price row the coach currently pays.
    /// </summary>
    public SubscriptionPlanPrice SubscriptionPlanPrice { get; set; } = null!;

    /// <summary>
    /// Price row the coach moves to at the next renewal, or null when they stay on
    /// <see cref="SubscriptionPlanPriceId"/>. Only marks the intent; applying it is billing's job.
    /// </summary>
    public long? PendingSubscriptionPlanPriceId { get; set; }

    /// <summary>
    /// Navigation property to the pending price row.
    /// </summary>
    public SubscriptionPlanPrice? PendingSubscriptionPlanPrice { get; set; }

    /// <summary>
    /// True when the coach pays for the second-role add-on.
    /// </summary>
    public bool PaysSecondRoleAddOn { get; set; }

    /// <summary>
    /// Current lifecycle status of the subscription.
    /// </summary>
    public SubscriptionStatus Status { get; set; }

    /// <summary>
    /// When the trial period ends, if the subscription started as a trial.
    /// </summary>
    public DateTimeOffset? TrialEndsAt { get; set; }

    /// <summary>
    /// While the clock is before this instant, the assigned plan's features apply whatever the status is.
    /// For a gift (<see cref="GivenFreeAt"/> set) it is the end of the gift instead; null means no end.
    /// </summary>
    public DateTimeOffset? FreeUntil { get; set; }

    /// <summary>
    /// When an admin gave the assigned plan to the coach for free; null when the plan is not a gift. While set,
    /// the plan applies only until <see cref="FreeUntil"/> (no end when that is null), whatever the status is.
    /// </summary>
    public DateTimeOffset? GivenFreeAt { get; set; }

    /// <summary>
    /// When the current billing period ends.
    /// </summary>
    public DateTimeOffset? CurrentPeriodEndsAt { get; set; }

    /// <summary>
    /// True when the holder disabled the coach account and the subscription should not renew.
    /// </summary>
    public bool CancelAtPeriodEnd { get; set; }

    /// <summary>
    /// External payment-provider customer identifier.
    /// </summary>
    public string? ExternalCustomerId { get; set; }

    /// <summary>
    /// External payment-provider subscription identifier.
    /// </summary>
    public string? ExternalSubscriptionId { get; set; }
}
