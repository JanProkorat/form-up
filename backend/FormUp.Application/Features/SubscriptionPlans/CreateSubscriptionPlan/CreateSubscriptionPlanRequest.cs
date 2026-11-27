namespace FormUp.Application.Features.SubscriptionPlans.CreateSubscriptionPlan;

/// <summary>
/// Request model for creating a new subscription tier.
/// </summary>
public class CreateSubscriptionPlanRequest
{
    /// <summary>
    /// Stable identifier for the plan (entitlement/Stripe mapping key). Immutable after create.
    /// </summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Czech plan name.</summary>
    public string NameCs { get; set; } = string.Empty;

    /// <summary>English plan name.</summary>
    public string NameEn { get; set; } = string.Empty;

    /// <summary>German plan name.</summary>
    public string NameDe { get; set; } = string.Empty;

    /// <summary>
    /// True when the plan covers both professional roles at no extra charge. Exactly one of this
    /// and <see cref="SecondRoleAddOnPriceMinorUnits"/> must be set.
    /// </summary>
    public bool BothRolesIncluded { get; set; }

    /// <summary>Price of the second-role add-on. Must be positive; omit when both roles are included.</summary>
    public long? SecondRoleAddOnPriceMinorUnits { get; set; }

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

    /// <summary>Whether the plan allows client payments. Defaults to false.</summary>
    public bool CanUsePayments { get; set; }

    /// <summary>Whether the plan allows nutrition plan templates. Defaults to false.</summary>
    public bool CanUsePlanTemplates { get; set; }

    /// <summary>Whether the plan allows the coach's own recipes. Defaults to false.</summary>
    public bool CanUseRecipes { get; set; }

    /// <summary>Whether the plan allows the coach's own ingredients. Defaults to false.</summary>
    public bool CanUseIngredients { get; set; }

    /// <summary>Whether the plan allows the coach side of progress photos. Defaults to false.</summary>
    public bool CanUseProgressPhotos { get; set; }

    /// <summary>Whether the plan allows the coach side of weight and measurements. Defaults to false.</summary>
    public bool CanUseWeightAndMeasurements { get; set; }

    /// <summary>Whether the plan allows automations. Defaults to false.</summary>
    public bool CanUseAutomations { get; set; }

    /// <summary>Whether the plan allows coach packages and discounts. Defaults to false.</summary>
    public bool CanUsePackages { get; set; }

    /// <summary>Maximum number of active clients allowed under this plan. Null means unlimited.</summary>
    public int? MaxActiveClients { get; set; }

    /// <summary>Monthly plan price, expressed in the smallest unit of <see cref="Currency"/>.</summary>
    public long PriceMinorUnits { get; set; }

    /// <summary>ISO 4217 currency code for <see cref="PriceMinorUnits"/>.</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>Annual plan price per year. Omit when the plan is not sold annually; never set on the Free plan.</summary>
    public long? AnnualPriceMinorUnits { get; set; }

    /// <summary>
    /// Annual price of the second-role add-on per year. Required with an annual price when the plan has a paid add-on;
    /// not allowed when both roles are included or there is no annual price.
    /// </summary>
    public long? AnnualSecondRoleAddOnPriceMinorUnits { get; set; }

    /// <summary>
    /// Ignored. The backend creates the Stripe price itself when a price row opens; the field stays only so existing
    /// clients keep working. The response returns the price id the backend stored.
    /// </summary>
    public string? ExternalPriceId { get; set; }

    /// <summary>Whether the plan is currently offered/selectable. Defaults to true on create.</summary>
    public bool IsActive { get; set; } = true;
}
