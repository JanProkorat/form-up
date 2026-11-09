namespace FormUp.Application.Features.AdminCoaches.GiveCoachPlanFree;

/// <summary>
/// Request for giving a coach a paid plan for free.
/// </summary>
public class GiveCoachPlanFreeRequest
{
    /// <summary>Route parameter — the coach's user id; matches <c>{CoachId}</c> in the route exactly.</summary>
    public Guid CoachId { get; set; }

    /// <summary>Code of the plan to give. Must not be the Free plan.</summary>
    public string PlanCode { get; set; } = string.Empty;

    /// <summary>When the gift ends; null means it has no end. Must be in the future.</summary>
    public DateTimeOffset? FreeUntil { get; set; }

    /// <summary>Why the plan is given, at most 300 characters. Stored in the coach's history.</summary>
    public string Reason { get; set; } = string.Empty;
}
