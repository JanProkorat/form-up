using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Domain.Services;

/// <summary>
/// Outcome of disabling a coach account.
/// </summary>
/// <param name="ActiveUntil">When the account stops being active; meaningless when <paramref name="Failure"/> is set.</param>
/// <param name="RolesRemoved">The coach roles removed by this call; empty while the end date is still pending.</param>
/// <param name="Failure">Set when the payment provider blocked the disable; nothing was saved then.</param>
public sealed record CoachAccountDisableResult(
    DateTime ActiveUntil,
    IReadOnlyList<string> RolesRemoved,
    CoachAccountDisableFailure? Failure = null)
{
    /// <summary>A result for a disable the payment provider blocked.</summary>
    /// <param name="failure">Why it was blocked.</param>
    public static CoachAccountDisableResult Refused(CoachAccountDisableFailure failure) => new(default, [], failure);
}
