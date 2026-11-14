using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Features.SubscriptionPlans.Shared;

/// <summary>
/// Full wire representation of a <see cref="SubscriptionPlan"/> for the Admin CRUD surface —
/// reused by the list, create, and update actions (#595).
/// </summary>
public class SubscriptionPlanDto
{
    /// <summary>Stable identifier for the plan (entitlement/Stripe mapping key).</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Czech plan name.</summary>
    public string NameCs { get; set; } = string.Empty;

    /// <summary>English plan name.</summary>
    public string NameEn { get; set; } = string.Empty;

    /// <summary>German plan name.</summary>
    public string NameDe { get; set; } = string.Empty;

    /// <summary>True when the plan covers both professional roles at no extra charge.</summary>
    public bool BothRolesIncluded { get; set; }

    /// <summary>Whether the plan allows creating nutrition/training plans.</summary>
    public bool CanCreatePlans { get; set; }

    /// <summary>Whether the plan allows messaging clients.</summary>
    public bool CanMessage { get; set; }

    /// <summary>Whether the plan allows sending questionnaires.</summary>
    public bool CanSendQuestionnaires { get; set; }

    /// <summary>Whether the plan allows using weekly check-ins.</summary>
    public bool CanUseWeeklyCheckIns { get; set; }

    /// <summary>Whether the plan allows per-client check-in configuration.</summary>
    public bool CanUsePerClientCheckInConfig { get; set; }

    /// <summary>Whether the plan allows client payments.</summary>
    public bool CanUsePayments { get; set; }

    /// <summary>Whether the plan allows: nutrition plan templates.</summary>
    public bool CanUsePlanTemplates { get; set; }

    /// <summary>Whether the plan allows: the coach's own recipes.</summary>
    public bool CanUseRecipes { get; set; }

    /// <summary>Whether the plan allows: the coach's own ingredients.</summary>
    public bool CanUseIngredients { get; set; }

    /// <summary>Whether the plan allows: the coach side of progress photos.</summary>
    public bool CanUseProgressPhotos { get; set; }

    /// <summary>Whether the plan allows: the coach side of weight and measurements.</summary>
    public bool CanUseWeightAndMeasurements { get; set; }

    /// <summary>Whether the plan allows: automations.</summary>
    public bool CanUseAutomations { get; set; }

    /// <summary>Whether the plan allows: coach packages and discounts.</summary>
    public bool CanUsePackages { get; set; }

    /// <summary>Maximum number of active clients allowed under this plan. Null means unlimited.</summary>
    public int? MaxActiveClients { get; set; }

    /// <summary>Current plan price, expressed in the smallest unit of <see cref="Currency"/>.</summary>
    public long PriceMinorUnits { get; set; }

    /// <summary>Current price of the second-role add-on. Null when both roles are included.</summary>
    public long? SecondRoleAddOnPriceMinorUnits { get; set; }

    /// <summary>ISO 4217 currency code for the prices.</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>Billing cadence for this plan.</summary>
    public BillingInterval BillingInterval { get; set; }

    /// <summary>External payment-provider price identifier of the current price, once billing integration ships.</summary>
    public string? ExternalPriceId { get; set; }

    /// <summary>Whether the plan is currently offered/selectable.</summary>
    public bool IsActive { get; set; }

    /// <summary>
    /// Number of coaches with a live subscription (trialing, active or past due) on this plan.
    /// Coaches without a subscription row are not counted.
    /// </summary>
    public int CoachCount { get; set; }

    /// <summary>
    /// Maps a <see cref="SubscriptionPlan"/> entity to its wire representation.
    /// </summary>
    /// <param name="plan">The plan.</param>
    /// <param name="externalPriceId">External price id of the plan's open price row.</param>
    /// <param name="coachCount">Coaches currently on the plan.</param>
    public static SubscriptionPlanDto FromEntity(SubscriptionPlan plan, string? externalPriceId, int coachCount) => new()
    {
        Code = plan.Code,
        NameCs = plan.NameCs,
        NameEn = plan.NameEn,
        NameDe = plan.NameDe,
        BothRolesIncluded = plan.BothRolesIncluded,
        CanCreatePlans = plan.CanCreatePlans,
        CanMessage = plan.CanMessage,
        CanSendQuestionnaires = plan.CanSendQuestionnaires,
        CanUseWeeklyCheckIns = plan.CanUseWeeklyCheckIns,
        CanUsePerClientCheckInConfig = plan.CanUsePerClientCheckInConfig,
        CanUsePayments = plan.CanUsePayments,
        CanUsePlanTemplates = plan.CanUsePlanTemplates,
        CanUseRecipes = plan.CanUseRecipes,
        CanUseIngredients = plan.CanUseIngredients,
        CanUseProgressPhotos = plan.CanUseProgressPhotos,
        CanUseWeightAndMeasurements = plan.CanUseWeightAndMeasurements,
        CanUseAutomations = plan.CanUseAutomations,
        CanUsePackages = plan.CanUsePackages,
        MaxActiveClients = plan.MaxActiveClients,
        PriceMinorUnits = plan.PriceMinorUnits,
        SecondRoleAddOnPriceMinorUnits = plan.SecondRoleAddOnPriceMinorUnits,
        Currency = plan.Currency,
        BillingInterval = plan.BillingInterval,
        ExternalPriceId = externalPriceId,
        IsActive = plan.IsActive,
        CoachCount = coachCount,
    };
}
