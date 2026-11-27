using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Features.SubscriptionPlans.GetPublicSubscriptionPlans;

/// <summary>
/// The plans shown on the public pricing page.
/// </summary>
public class GetPublicSubscriptionPlansResponse
{
    /// <summary>Offered plans, cheapest first.</summary>
    public List<PublicSubscriptionPlanDto> Plans { get; set; } = [];

    /// <summary>
    /// The active automatic promotion for a new coach, or null. Same for every caller; a plan it does not cover shows
    /// no discounted price.
    /// </summary>
    public PublicPromotionDto? Promotion { get; set; }
}

/// <summary>
/// The automatic promotion shown on the public pricing page. Carries no code, usage, limit or Stripe data.
/// </summary>
public class PublicPromotionDto
{
    /// <summary>Promotion name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>English promotion name; null when none was entered.</summary>
    public string? NameEn { get; set; }

    /// <summary>German promotion name; null when none was entered.</summary>
    public string? NameDe { get; set; }

    /// <summary>Percentage or fixed amount.</summary>
    public PromotionKind Kind { get; set; }

    /// <summary>Percent (1 to 100), or the amount in the smallest unit of <see cref="Currency"/>.</summary>
    public long Value { get; set; }

    /// <summary>ISO 4217 currency of a fixed amount; null for a percentage.</summary>
    public string? Currency { get; set; }

    /// <summary>How long the discount lasts once claimed.</summary>
    public PromotionDuration Duration { get; set; }

    /// <summary>Months the discount lasts; set only for <see cref="PromotionDuration.Months"/>.</summary>
    public int? DurationMonths { get; set; }

    /// <summary>Which billing cadences the promotion discounts; the discounted fields of a cadence it does not cover stay null.</summary>
    public PromotionBillingPeriod BillingPeriod { get; set; }

    /// <summary>Last moment the offer can be claimed; null for no end.</summary>
    public DateTimeOffset? ClaimUntil { get; set; }

    /// <summary>Codes of the plans the promotion covers.</summary>
    public List<string> PlanCodes { get; set; } = [];
}

/// <summary>
/// One offered plan as the public pricing page shows it. Carries no ids, no activity flag and no coach counts.
/// </summary>
public class PublicSubscriptionPlanDto
{
    /// <summary>Stable plan code.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Czech name.</summary>
    public string NameCs { get; set; } = string.Empty;

    /// <summary>English name.</summary>
    public string NameEn { get; set; } = string.Empty;

    /// <summary>German name.</summary>
    public string NameDe { get; set; } = string.Empty;

    /// <summary>Current monthly price in minor units of <see cref="Currency"/>.</summary>
    public long PriceMinorUnits { get; set; }

    /// <summary>Second-role add-on price; null when both roles are included.</summary>
    public long? SecondRoleAddOnPriceMinorUnits { get; set; }

    /// <summary>Display-only price after the promotion; null when no promotion covers this plan.</summary>
    public long? DiscountedPriceMinorUnits { get; set; }

    /// <summary>Display-only add-on price after the promotion; null when not covered or both roles are included.</summary>
    public long? DiscountedSecondRoleAddOnPriceMinorUnits { get; set; }

    /// <summary>Annual plan price per year; null when the plan is not sold annually.</summary>
    public long? AnnualPriceMinorUnits { get; set; }

    /// <summary>Annual second-role add-on price per year; null when not sold annually, none is set or both roles are included.</summary>
    public long? AnnualSecondRoleAddOnPriceMinorUnits { get; set; }

    /// <summary>Display-only annual price after the promotion; null when not sold annually or no promotion covers this plan.</summary>
    public long? AnnualDiscountedPriceMinorUnits { get; set; }

    /// <summary>Display-only annual add-on price after the promotion; null when not covered or there is no annual add-on.</summary>
    public long? AnnualDiscountedSecondRoleAddOnPriceMinorUnits { get; set; }

    /// <summary>Whole percent the annual plan line saves against twelve monthly ones; null when there is no saving.</summary>
    public int? AnnualSavingPercent { get; set; }

    /// <summary>True when the plan covers both professional roles.</summary>
    public bool BothRolesIncluded { get; set; }

    /// <summary>ISO 4217 currency code.</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>Billing cadence of the monthly price fields; always Monthly, the annual price has its own fields.</summary>
    public BillingInterval BillingInterval { get; set; }

    /// <summary>Active-client cap; null means unlimited.</summary>
    public int? MaxActiveClients { get; set; }

    /// <summary>Whether the plan allows creating nutrition and training plans.</summary>
    public bool CanCreatePlans { get; set; }

    /// <summary>Whether the plan allows messaging clients.</summary>
    public bool CanMessage { get; set; }

    /// <summary>Whether the plan allows sending questionnaires.</summary>
    public bool CanSendQuestionnaires { get; set; }

    /// <summary>Whether the plan allows weekly check-ins.</summary>
    public bool CanUseWeeklyCheckIns { get; set; }

    /// <summary>Whether the plan allows per-client check-in configuration.</summary>
    public bool CanUsePerClientCheckInConfig { get; set; }

    /// <summary>Whether the plan allows client payments.</summary>
    public bool CanUsePayments { get; set; }

    /// <summary>Whether the plan allows plan templates.</summary>
    public bool CanUsePlanTemplates { get; set; }

    /// <summary>Whether the plan allows a recipe library.</summary>
    public bool CanUseRecipes { get; set; }

    /// <summary>Whether the plan allows an ingredient library.</summary>
    public bool CanUseIngredients { get; set; }

    /// <summary>Whether the plan allows progress photos.</summary>
    public bool CanUseProgressPhotos { get; set; }

    /// <summary>Whether the plan allows weight and body measurements.</summary>
    public bool CanUseWeightAndMeasurements { get; set; }

    /// <summary>Whether the plan allows automations.</summary>
    public bool CanUseAutomations { get; set; }

    /// <summary>Whether the plan allows coach packages and discounts.</summary>
    public bool CanUsePackages { get; set; }

    /// <summary>True for the single plan with the most live coaches; no plan is flagged on a tie.</summary>
    public bool IsMostPopular { get; set; }
}
