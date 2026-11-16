namespace FormUp.Application.Features.Billing.PreviewPlanChange;

/// <summary>
/// Request for previewing a move of the caller's live paid subscription to another paid plan.
/// </summary>
public class PreviewPlanChangeRequest
{
    /// <summary>Code of the paid plan to move to. Never the Free plan.</summary>
    public string PlanCode { get; set; } = string.Empty;
}
