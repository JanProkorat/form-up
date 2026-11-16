using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Features.Billing.CancelScheduledPlanChange;

/// <summary>
/// The plan the subscription renews on after the scheduled change was cancelled.
/// </summary>
public class CancelScheduledPlanChangeResponse
{
    /// <summary>What was cancelled.</summary>
    public ScheduledPlanChangeKind Kind { get; set; }

    /// <summary>Code of the plan the subscription stays on.</summary>
    public string PlanCode { get; set; } = string.Empty;

    /// <summary>When the subscription renews next, if known.</summary>
    public DateTimeOffset? RenewsAt { get; set; }
}
