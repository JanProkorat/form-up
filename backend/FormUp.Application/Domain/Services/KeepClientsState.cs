namespace FormUp.Application.Domain.Services;

/// <summary>
/// Whether a coach's plan leaves some live clients over the Free plan's cap and, when it does, which live clients
/// the Free plan keeps.
/// </summary>
/// <param name="IsLapsed">True when the subscription is Canceled or Incomplete with no free-features deadline in force.</param>
/// <param name="Cap">The Free plan's active-client cap; null means unlimited. Meaningful only while lapsed, on an explicit Free plan with clients over the cap, or while a plan choice is required.</param>
/// <param name="KeptClientProfileIds">Live clients the Free plan keeps, as <c>ClientProfile.Id</c> values.</param>
/// <param name="OverCapClientProfileIds">Every other live client, as <c>ClientProfile.Id</c> values.</param>
/// <param name="IsPlanChoiceRequired">True when an admin-given plan ended and the coach has not chosen a plan yet; nobody is over the cap then.</param>
public sealed record KeepClientsState(
    bool IsLapsed,
    int? Cap,
    IReadOnlyList<long> KeptClientProfileIds,
    IReadOnlyList<long> OverCapClientProfileIds,
    bool IsPlanChoiceRequired = false)
{
    /// <summary>The state of a coach whose plan has not lapsed: nobody is over the cap.</summary>
    public static KeepClientsState NotLapsed { get; } = new(false, null, [], []);
}
