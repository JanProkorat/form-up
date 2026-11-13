namespace FormUp.Application.Domain.Services;

/// <summary>
/// Whether a coach's plan leaves some live clients over the cap of the plan in force and, when it does, which live
/// clients the plan keeps.
/// </summary>
/// <param name="IsLapsed">True when the subscription is Canceled or Incomplete with no free-features deadline in force.</param>
/// <param name="Cap">The Free plan's active-client cap; null means unlimited. Meaningful only while lapsed, on an explicit Free plan with clients over the cap, or while a plan choice is required.</param>
/// <param name="KeptClientProfileIds">Live clients the Free plan keeps, as <c>ClientProfile.Id</c> values.</param>
/// <param name="OverCapClientProfileIds">Every other live client, as <c>ClientProfile.Id</c> values.</param>
/// <param name="IsPlanChoiceRequired">True when an admin-given plan ended and the coach has not chosen a plan yet; nobody is over the cap then.</param>
/// <param name="IsChoicePending">True when at least one over-cap client has no kept or released decision yet (a link still Connected).</param>
public sealed record KeepClientsState(
    bool IsLapsed,
    int? Cap,
    IReadOnlyList<long> KeptClientProfileIds,
    IReadOnlyList<long> OverCapClientProfileIds,
    bool IsPlanChoiceRequired = false,
    bool IsChoicePending = false)
{
    /// <summary>
    /// True when the coach has a kept-clients choice to make or change: a lapsed paid plan, or an explicit Free plan with
    /// live clients over the cap.
    /// </summary>
    public bool CanChooseKeptClients => IsLapsed || OverCapClientProfileIds.Count > 0;

    /// <summary>
    /// How many more clients the coach may still mark as kept: the cap minus the kept clients while some
    /// live clients are over the cap and the cap is limited, otherwise zero.
    /// </summary>
    public int FreeSlotCount =>
        Cap is { } cap && OverCapClientProfileIds.Count > 0 ? Math.Max(0, cap - KeptClientProfileIds.Count) : 0;

    /// <summary>The state of a coach whose plan has not lapsed: nobody is over the cap.</summary>
    public static KeepClientsState NotLapsed { get; } = new(false, null, [], []);
}
