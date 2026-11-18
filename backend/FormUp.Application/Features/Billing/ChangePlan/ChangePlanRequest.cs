namespace FormUp.Application.Features.Billing.ChangePlan;

/// <summary>
/// Request for moving the caller's live paid subscription to another paid plan.
/// </summary>
public class ChangePlanRequest
{
    /// <summary>Code of the paid plan to move to. Never the Free plan.</summary>
    public string PlanCode { get; set; } = string.Empty;

    /// <summary>
    /// Client-generated id of this attempt. It keys the charge, so a double click does not bill twice; the client sends a
    /// fresh one after a declined card.
    /// </summary>
    public Guid AttemptId { get; set; }
}
