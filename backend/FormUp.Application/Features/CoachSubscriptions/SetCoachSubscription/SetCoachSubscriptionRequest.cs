using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Features.CoachSubscriptions.SetCoachSubscription;

/// <summary>
/// Request for assigning a coach's subscription.
/// </summary>
public class SetCoachSubscriptionRequest
{
    /// <summary>Route parameter — the coach's user id; matches <c>{CoachId}</c> in the route exactly.</summary>
    public Guid CoachId { get; set; }

    /// <summary>Code of the plan to assign. An inactive plan may be assigned.</summary>
    public string PlanCode { get; set; } = string.Empty;

    /// <summary>Status to set.</summary>
    public SubscriptionStatus Status { get; set; }

    /// <summary>While now is before this instant the assigned plan's features apply whatever the status is.</summary>
    public DateTimeOffset? FreeUntil { get; set; }

    /// <summary>True when the coach pays for the second-role add-on.</summary>
    public bool PaysSecondRoleAddOn { get; set; }
}
