using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Features.Billing.ChangePlan;

/// <summary>
/// The outcome of a plan change request.
/// </summary>
public class ChangePlanResponse
{
    /// <summary>Code of the plan the coach asked for.</summary>
    public string PlanCode { get; set; } = string.Empty;

    /// <summary>Whether the change applied now (upgrade) or is scheduled for the next renewal (downgrade).</summary>
    public PlanChangeTiming Timing { get; set; }

    /// <summary>When the new plan starts: now for an upgrade, the period end for a scheduled change (null when unknown).</summary>
    public DateTimeOffset? EffectiveAt { get; set; }
}
