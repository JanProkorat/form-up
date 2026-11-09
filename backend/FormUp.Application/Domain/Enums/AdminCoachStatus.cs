namespace FormUp.Application.Domain.Enums;

/// <summary>
/// The one status an admin sees on a coach: the most specific of the filter buckets.
/// </summary>
public enum AdminCoachStatus
{
    /// <summary>On a paid plan they pay for, in good standing.</summary>
    Paying,

    /// <summary>Paying, and the last payment failed.</summary>
    PaymentFailed,

    /// <summary>Paying, and the renewal is cancelled.</summary>
    Cancelling,

    /// <summary>On the Free plan, or on a plan that no longer applies.</summary>
    Free,

    /// <summary>An admin gave the plan for free and the gift is still in force.</summary>
    GivenFree,
}
