namespace FormUp.Application.Features.TrainingPlans.PublishTrainingWeek;

/// <summary>
/// Request to publish a specific week of a training plan.
/// </summary>
public class PublishTrainingWeekRequest
{
    /// <summary>Plan identifier.</summary>
    public Guid PlanId { get; set; }

    /// <summary>Week number to publish.</summary>
    public int WeekNumber { get; set; }
}
