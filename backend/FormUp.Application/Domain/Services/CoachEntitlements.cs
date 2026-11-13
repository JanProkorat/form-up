namespace FormUp.Application.Domain.Services;

/// <summary>
/// The feature capabilities and client-count limit a coach's subscription currently grants.
/// Deliberately named distinctly from <see cref="Entities.LinkCapabilities"/> — that type answers
/// a different axis (what a single <c>ClientProfessionalLink</c> lets a professional see about one
/// client); this type answers what the coach's own subscription tier unlocks account-wide.
/// </summary>
/// <param name="CanCreatePlans">Whether the subscription allows creating nutrition/training plans.</param>
/// <param name="CanMessage">Whether the subscription allows messaging clients.</param>
/// <param name="CanSendQuestionnaires">Whether the subscription allows sending questionnaires.</param>
/// <param name="CanUseWeeklyCheckIns">Whether the subscription allows using weekly check-ins.</param>
/// <param name="CanUsePerClientCheckInConfig">Whether the subscription allows per-client check-in configuration.</param>
/// <param name="CanUsePayments">Whether the subscription allows the payments feature.</param>
/// <param name="MaxActiveClients">Maximum number of active clients the subscription allows. Null means unlimited.</param>
public readonly record struct CoachEntitlements(
    bool CanCreatePlans,
    bool CanMessage,
    bool CanSendQuestionnaires,
    bool CanUseWeeklyCheckIns,
    bool CanUsePerClientCheckInConfig,
    bool CanUsePayments,
    int? MaxActiveClients)
{
    /// <summary>
    /// Every feature flag off and no client slots. Applied when the Free plan row is missing, so the
    /// service fails closed.
    /// </summary>
    public static CoachEntitlements Locked { get; } = new(
        CanCreatePlans: false,
        CanMessage: false,
        CanSendQuestionnaires: false,
        CanUseWeeklyCheckIns: false,
        CanUsePerClientCheckInConfig: false,
        CanUsePayments: false,
        MaxActiveClients: 0);
}
