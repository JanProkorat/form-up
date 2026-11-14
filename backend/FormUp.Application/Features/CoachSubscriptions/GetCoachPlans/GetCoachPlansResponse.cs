using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Services.Billing;

namespace FormUp.Application.Features.CoachSubscriptions.GetCoachPlans;

/// <summary>
/// The subscription plans a coach can choose between.
/// </summary>
public class GetCoachPlansResponse
{
    /// <summary>Active plans, cheapest first.</summary>
    public List<CoachPlanDto> Plans { get; set; } = [];

    /// <summary>VAT handling: with <see cref="BillingTaxMode.None"/> the listed prices carry no VAT line.</summary>
    public BillingTaxMode TaxMode { get; set; }

    /// <summary>
    /// The automatic promotion that applies to the caller, or null. One wins per coach; a plan it does not cover shows
    /// full price (no discounted price on that plan).
    /// </summary>
    public CoachPromotionDto? Promotion { get; set; }
}

/// <summary>
/// The automatic promotion shown on the plan picker.
/// </summary>
public class CoachPromotionDto
{
    /// <summary>Promotion name.</summary>
    public string Name { get; set; } = string.Empty;

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

    /// <summary>Last moment the offer can be claimed; null for no end.</summary>
    public DateTimeOffset? ClaimUntil { get; set; }

    /// <summary>True when the second-role add-on line is discounted too.</summary>
    public bool CoversSecondRoleAddOn { get; set; }

    /// <summary>Codes of the plans the promotion covers.</summary>
    public List<string> PlanCodes { get; set; } = [];
}

/// <summary>
/// One plan a coach can choose: names, current price, client cap and feature flags.
/// </summary>
public class CoachPlanDto
{
    /// <summary>Stable code of the plan.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Czech plan name.</summary>
    public string NameCs { get; set; } = string.Empty;

    /// <summary>English plan name.</summary>
    public string NameEn { get; set; } = string.Empty;

    /// <summary>German plan name.</summary>
    public string NameDe { get; set; } = string.Empty;

    /// <summary>Current price per billing interval, in the smallest unit of <see cref="Currency"/>.</summary>
    public long PriceMinorUnits { get; set; }

    /// <summary>Current price of the second-role add-on in the same unit; null when both roles are included.</summary>
    public long? SecondRoleAddOnPriceMinorUnits { get; set; }

    /// <summary>ISO 4217 currency code.</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>
    /// Display-only plan price after the automatic promotion; null when no promotion covers this plan. Stripe computes
    /// the real charge.
    /// </summary>
    public long? DiscountedPriceMinorUnits { get; set; }

    /// <summary>
    /// Display-only second-role add-on price after the promotion; null when no promotion covers this plan or the plan has
    /// no add-on. Equal to the add-on price when the promotion does not discount the add-on line.
    /// </summary>
    public long? DiscountedSecondRoleAddOnPriceMinorUnits { get; set; }

    /// <summary>How often the plan is billed.</summary>
    public BillingInterval BillingInterval { get; set; }

    /// <summary>Active-client cap; null means unlimited.</summary>
    public int? MaxActiveClients { get; set; }

    /// <summary>True when the plan covers both professional roles at no extra charge.</summary>
    public bool BothRolesIncluded { get; set; }

    /// <summary>Creating, editing and publishing plans.</summary>
    public bool CanCreatePlans { get; set; }

    /// <summary>Messaging clients.</summary>
    public bool CanMessage { get; set; }

    /// <summary>Sending questionnaires and forms.</summary>
    public bool CanSendQuestionnaires { get; set; }

    /// <summary>Weekly check-ins.</summary>
    public bool CanUseWeeklyCheckIns { get; set; }

    /// <summary>Per-client check-in configuration.</summary>
    public bool CanUsePerClientCheckInConfig { get; set; }

    /// <summary>The payments feature.</summary>
    public bool CanUsePayments { get; set; }
}
