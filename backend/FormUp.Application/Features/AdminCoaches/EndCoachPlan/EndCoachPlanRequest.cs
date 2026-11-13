namespace FormUp.Application.Features.AdminCoaches.EndCoachPlan;

/// <summary>
/// Request for ending a coach's paid or given plan now.
/// </summary>
public class EndCoachPlanRequest
{
    /// <summary>Route parameter — the coach's user id; matches <c>{CoachId}</c> in the route exactly.</summary>
    public Guid CoachId { get; set; }

    /// <summary>Why the plan is ended, at most 300 characters. Stored in the coach's history.</summary>
    public string Reason { get; set; } = string.Empty;
}
