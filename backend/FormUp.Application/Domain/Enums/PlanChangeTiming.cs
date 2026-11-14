namespace FormUp.Application.Domain.Enums;

/// <summary>
/// When a coach's plan change takes effect.
/// </summary>
public enum PlanChangeTiming
{
    /// <summary>Applied immediately, with proration.</summary>
    Now,

    /// <summary>Applied at the next renewal; the current plan stays until then.</summary>
    AtNextRenewal
}
