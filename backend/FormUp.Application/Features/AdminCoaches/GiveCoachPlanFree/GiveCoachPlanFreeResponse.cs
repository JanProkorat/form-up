namespace FormUp.Application.Features.AdminCoaches.GiveCoachPlanFree;

/// <summary>
/// The coach's plan after it was given for free.
/// </summary>
public class GiveCoachPlanFreeResponse
{
    /// <summary>The coach's user id.</summary>
    public Guid CoachId { get; set; }

    /// <summary>Code of the plan given.</summary>
    public string PlanCode { get; set; } = string.Empty;

    /// <summary>When the plan was given.</summary>
    public DateTimeOffset GivenFreeAt { get; set; }

    /// <summary>When the gift ends; null means it has no end.</summary>
    public DateTimeOffset? FreeUntil { get; set; }
}
