using FormUp.Application.Domain.Documents;

namespace FormUp.Application.Features.ClientNutrition.GetWeekPlan;

/// <summary>
/// Response model for the client's active nutrition plan for the current week.
/// </summary>
public class GetWeekPlanResponse
{
    /// <summary>
    /// External identifier of the nutrition plan.
    /// </summary>
    public Guid PlanId { get; set; }

    /// <summary>
    /// Plan <c>Version</c> the client saw; echoed back as <c>BasedOnPlanVersion</c> on offline writes.
    /// </summary>
    public int PlanVersion { get; set; }

    /// <summary>
    /// Display name of the nutrition plan.
    /// </summary>
    public string PlanName { get; set; } = string.Empty;

    /// <summary>
    /// Current week number within the plan (1-based).
    /// </summary>
    public int WeekNumber { get; set; }

    /// <summary>
    /// All days in the current week.
    /// </summary>
    public List<PlanDay> Days { get; set; } = [];

    /// <summary>
    /// Global daily nutrition targets for the plan.
    /// </summary>
    public GlobalNutritionSettings? GlobalSettings { get; set; }

    /// <summary>
    /// Last day of the plan's last published week, set only when the plan's coach no longer has a plan that covers
    /// this client: after it the plan is read-only. Null otherwise.
    /// </summary>
    public DateOnly? RunsUntil { get; set; }
}
