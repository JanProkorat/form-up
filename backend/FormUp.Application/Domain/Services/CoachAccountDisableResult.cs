namespace FormUp.Application.Domain.Services;

/// <summary>
/// Outcome of disabling a coach account.
/// </summary>
/// <param name="ActiveUntil">When the account stops being active.</param>
/// <param name="RolesRemoved">The coach roles removed by this call; empty while the end date is still pending.</param>
public sealed record CoachAccountDisableResult(DateTime ActiveUntil, IReadOnlyList<string> RolesRemoved);
