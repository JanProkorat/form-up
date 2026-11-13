namespace FormUp.Application.Features.AdminCoaches.EndCoachPlan;

/// <summary>
/// What was ended.
/// </summary>
public class EndCoachPlanResponse
{
    /// <summary>The coach's user id.</summary>
    public Guid CoachId { get; set; }

    /// <summary>Code of the plan that was ended.</summary>
    public string PlanCode { get; set; } = string.Empty;

    /// <summary>True when the ended plan was a gift rather than a paid plan.</summary>
    public bool WasGivenFree { get; set; }
}
