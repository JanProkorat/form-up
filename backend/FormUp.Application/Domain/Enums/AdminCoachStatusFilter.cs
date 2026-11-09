namespace FormUp.Application.Domain.Enums;

/// <summary>
/// Filter of the admin coaches list. <see cref="Paying"/>, <see cref="Free"/> and <see cref="GivenFree"/> split
/// every coach exactly once; <see cref="PaymentFailed"/> and <see cref="Cancelling"/> are subsets of Paying.
/// </summary>
public enum AdminCoachStatusFilter
{
    /// <summary>Every coach.</summary>
    All,

    /// <summary>On a paid plan they pay for.</summary>
    Paying,

    /// <summary>Paying, and the last payment failed.</summary>
    PaymentFailed,

    /// <summary>Paying, and the renewal is cancelled so the plan ends with the paid period.</summary>
    Cancelling,

    /// <summary>On the Free plan, or on a plan that no longer applies.</summary>
    Free,

    /// <summary>An admin gave the plan for free and the gift is still in force.</summary>
    GivenFree,
}
