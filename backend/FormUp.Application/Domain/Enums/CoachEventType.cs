namespace FormUp.Application.Domain.Enums;

/// <summary>
/// What happened to a coach in the sign-up and billing funnel. Stored as an integer: append new values, never renumber.
/// </summary>
public enum CoachEventType
{
    /// <summary>The coach account was created.</summary>
    SignedUp = 0,

    /// <summary>The coach confirmed their email address.</summary>
    EmailVerified = 1,

    /// <summary>The coach picked a plan: a paid plan at checkout, or Free after a given plan ended.</summary>
    ChosePlan = 2,

    /// <summary>A paid provider subscription of the coach went live.</summary>
    StartedPaidPlan = 3,

    /// <summary>The coach moved to a dearer plan or price.</summary>
    Upgraded = 4,

    /// <summary>The coach's plan or price went down, normally at a renewal.</summary>
    Downgraded = 5,

    /// <summary>The coach's paid subscription ended.</summary>
    Cancelled = 6,

    /// <summary>A renewal payment of the coach failed.</summary>
    PaymentFailed = 7,

    /// <summary>The coach applied a promotion code to their subscription.</summary>
    PromotionCodeApplied = 8,
}
