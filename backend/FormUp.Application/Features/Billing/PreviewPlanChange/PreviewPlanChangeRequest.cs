using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Features.Billing.PreviewPlanChange;

/// <summary>
/// Request for previewing a move of the caller's live paid subscription to another paid plan.
/// </summary>
public class PreviewPlanChangeRequest
{
    /// <summary>Code of the paid plan to move to. Never the Free plan.</summary>
    public string PlanCode { get; set; } = string.Empty;

    /// <summary>How often the target plan bills. Monthly when omitted, so clients that predate annual billing keep working.</summary>
    public BillingInterval BillingInterval { get; set; } = BillingInterval.Monthly;
}
