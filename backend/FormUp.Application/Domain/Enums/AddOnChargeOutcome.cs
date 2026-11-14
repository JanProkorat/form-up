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

    /// <summary>The card needs authentication that an immediate charge cannot do; nothing was added or charged.</summary>
    AuthenticationRequired
}
