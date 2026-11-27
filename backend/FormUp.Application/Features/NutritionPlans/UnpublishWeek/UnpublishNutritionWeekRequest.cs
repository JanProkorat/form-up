namespace FormUp.Application.Features.NutritionPlans.UnpublishWeek;

/// <summary>
/// Request to unpublish a specific week of a nutrition plan. Route-bound only; no body.
/// </summary>
public class UnpublishNutritionWeekRequest
{
    /// <summary>Plan identifier.</summary>
    public Guid PlanId { get; set; }

    /// <summary>Week number to unpublish.</summary>
    public int WeekNumber { get; set; }
}
