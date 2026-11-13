using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Features.CoachSubscriptions.SetCoachSubscription;

/// <summary>
/// The coach's subscription as it now stands.
/// </summary>
public class SetCoachSubscriptionResponse
{
    /// <summary>The coach's user id.</summary>
    public Guid CoachId { get; set; }

    /// <summary>Code of the assigned plan.</summary>
    public string PlanCode { get; set; } = string.Empty;

    /// <summary>Status now set.</summary>
    public SubscriptionStatus Status { get; set; }

    /// <summary>The free-features deadline, or null.</summary>
    public DateTimeOffset? FreeUntil { get; set; }

    /// <summary>True when the coach pays for the second-role add-on.</summary>
    public bool PaysSecondRoleAddOn { get; set; }
}
