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

    /// <summary>Amount refunded to the card in the smallest currency unit; null when nothing was refunded.</summary>
    public long? RefundedMinorUnits { get; set; }

    /// <summary>Upper-case ISO 4217 currency of the refund; null when nothing was refunded.</summary>
    public string? RefundCurrency { get; set; }
}
