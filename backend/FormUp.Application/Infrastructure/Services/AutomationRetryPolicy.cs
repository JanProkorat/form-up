namespace FormUp.Application.Infrastructure.Services;

/// <summary>
/// How long the worker waits before retrying an automation send that threw, and how many times it
/// tries. A rule-caused failure is never retried.
/// </summary>
public static class AutomationRetryPolicy
{
    /// <summary>Waits after the first, second and third failure; the failure after the last one is final.</summary>
    public static readonly IReadOnlyList<TimeSpan> Delays =
    [
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(30),
    ];

    /// <summary>
    /// When the next retry is due after the failure that makes <paramref name="attemptCount"/> failed
    /// attempts; null when the retries are used up.
    /// </summary>
    /// <param name="attemptCount">Failed attempts so far, counting the one that just failed.</param>
    /// <param name="nowUtc">The current instant.</param>
    public static DateTime? NextRetryAt(int attemptCount, DateTime nowUtc) =>
        attemptCount >= 1 && attemptCount <= Delays.Count ? nowUtc + Delays[attemptCount - 1] : null;
}
