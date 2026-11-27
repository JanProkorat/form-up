namespace FormUp.Application.Features.NutritionPlans.PublishWeek;

/// <summary>
/// Request to publish a specific week of a nutrition plan.
/// </summary>
public class PublishWeekRequest
{
    /// <summary>Plan identifier.</summary>
    public Guid PlanId { get; set; }

    /// <summary>Week number to publish.</summary>
    public int WeekNumber { get; set; }
}
