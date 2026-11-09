namespace FormUp.Application.Domain.Services;

/// <summary>
/// Whether a coach's paid plan has lapsed and, when it has, which live clients the Free plan keeps.
/// </summary>
/// <param name="IsLapsed">True when the subscription is Canceled or Incomplete with no free-features deadline in force.</param>
/// <param name="Cap">The Free plan's active-client cap; null means unlimited. Meaningful only while lapsed.</param>
/// <param name="KeptClientProfileIds">Live clients the Free plan keeps, as <c>ClientProfile.Id</c> values.</param>
/// <param name="OverCapClientProfileIds">Every other live client, as <c>ClientProfile.Id</c> values.</param>
public sealed record KeepClientsState(
    bool IsLapsed,
    int? Cap,
    IReadOnlyList<long> KeptClientProfileIds,
    IReadOnlyList<long> OverCapClientProfileIds)
{
    /// <summary>The state of a coach whose plan has not lapsed: nobody is over the cap.</summary>
    public static KeepClientsState NotLapsed { get; } = new(false, null, [], []);
}
