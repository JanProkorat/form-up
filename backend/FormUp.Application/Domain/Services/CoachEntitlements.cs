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
/// <param name="CanUsePayments">Whether the subscription allows client payments.</param>
/// <param name="CanUsePlanTemplates">Whether the subscription allows nutrition plan templates.</param>
/// <param name="CanUseRecipes">Whether the subscription allows the coach's own recipes.</param>
/// <param name="CanUseIngredients">Whether the subscription allows the coach's own ingredients.</param>
/// <param name="CanUseProgressPhotos">Whether the subscription allows the coach side of progress photos.</param>
/// <param name="CanUseWeightAndMeasurements">Whether the subscription allows the coach side of measurements.</param>
/// <param name="CanUseAutomations">Whether the subscription allows automations.</param>
/// <param name="CanUsePackages">Whether the subscription allows coach packages and discounts.</param>
/// <param name="MaxActiveClients">Maximum number of active clients the subscription allows. Null means unlimited.</param>
public readonly record struct CoachEntitlements(
    bool CanCreatePlans,
    bool CanMessage,
    bool CanSendQuestionnaires,
    bool CanUseWeeklyCheckIns,
    bool CanUsePerClientCheckInConfig,
    bool CanUsePayments,
    bool CanUsePlanTemplates,
    bool CanUseRecipes,
    bool CanUseIngredients,
    bool CanUseProgressPhotos,
    bool CanUseWeightAndMeasurements,
    bool CanUseAutomations,
    bool CanUsePackages,
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
        CanUsePlanTemplates: false,
        CanUseRecipes: false,
        CanUseIngredients: false,
        CanUseProgressPhotos: false,
        CanUseWeightAndMeasurements: false,
        CanUseAutomations: false,
        CanUsePackages: false,
        MaxActiveClients: 0);
}
