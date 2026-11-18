namespace FormUp.Application.Features.Billing.ScheduleSwitchToFree;

/// <summary>
/// The outcome of scheduling a switch to the Free plan.
/// </summary>
public class ScheduleSwitchToFreeResponse
{
    /// <summary>When the paid plan ends and Free starts (the period end); null when unknown.</summary>
    public DateTimeOffset? EndsAt { get; set; }
}
