namespace FormUp.Application.Domain.Enums;

/// <summary>
/// What a coach's scheduled plan change was before it was cancelled.
/// </summary>
public enum ScheduledPlanChangeKind
{
    /// <summary>A move to a cheaper plan at the next renewal.</summary>
    Downgrade,

    /// <summary>A switch to Free at the end of the period.</summary>
    SwitchToFree
}
