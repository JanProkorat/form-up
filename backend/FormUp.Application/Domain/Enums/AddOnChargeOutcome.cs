namespace FormUp.Application.Domain.Enums;

/// <summary>
/// How an immediate second-role add-on charge ended.
/// </summary>
public enum AddOnChargeOutcome
{
    /// <summary>The add-on was added and the prorated amount was paid.</summary>
    Charged,

    /// <summary>The card was declined; nothing was added or charged.</summary>
    Declined,

    /// <summary>The card needs authentication; the add-on is held until the coach pays on the provider's hosted page.</summary>
    AwaitingPayment,

    /// <summary>Another pending provider update (such as a plan upgrade) is open; nothing was changed.</summary>
    PlanChangePending
}
