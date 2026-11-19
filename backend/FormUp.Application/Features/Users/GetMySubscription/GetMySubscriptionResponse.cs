using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Services.Billing;

namespace FormUp.Application.Features.Users.GetMySubscription;

/// <summary>
/// The caller's coach subscription: plan, standing, client usage, second-role state, price and
/// the feature flags that currently apply.
/// </summary>
public class GetMySubscriptionResponse
{
    /// <summary>Code of the plan the subscription is on (the Free plan when the coach has no subscription).</summary>
    public string PlanCode { get; set; } = string.Empty;

    /// <summary>Czech plan name.</summary>
    public string NameCs { get; set; } = string.Empty;

    /// <summary>English plan name.</summary>
    public string NameEn { get; set; } = string.Empty;

    /// <summary>German plan name.</summary>
    public string NameDe { get; set; } = string.Empty;

    /// <summary>When the provider retries a failed payment next; null unless the last payment failed.</summary>
    public DateTimeOffset? NextPaymentAttemptAt { get; set; }

    /// <summary>VAT handling, so the plan page knows whether to show a VAT line.</summary>
    public BillingTaxMode TaxMode { get; set; }

    /// <summary>True when the coach has a billing customer, so the billing portal can be opened.</summary>
    public bool HasBillingAccount { get; set; }

    /// <summary>
    /// Subscription status, or null when the coach has no subscription and is on Free by default. Also null once a plan
    /// has ended (<see cref="IsLapsed"/>).
    /// </summary>
    public SubscriptionStatus? Status { get; set; }

    /// <summary>When the current billing period ends, if known. Null once a plan has ended; see <see cref="EndedAt"/>.</summary>
    public DateTimeOffset? CurrentPeriodEndsAt { get; set; }

    /// <summary>True when the subscription will not renew. False once a plan has ended.</summary>
    public bool CancelAtPeriodEnd { get; set; }

    /// <summary>While now is before this instant the assigned plan's features apply whatever the status is.</summary>
    public DateTimeOffset? FreeUntil { get; set; }

    /// <summary>True when an admin set the plan (given free) and it still applies.</summary>
    public bool IsSetByFormUp { get; set; }

    /// <summary>
    /// True when the paid plan has lapsed (Canceled or Incomplete with no free-features deadline in force). The plan
    /// fields then describe the Free plan, <see cref="Status"/>, <see cref="CurrentPeriodEndsAt"/> and
    /// <see cref="CancelAtPeriodEnd"/> are empty, and <see cref="ActiveClientCount"/> is the number of kept clients.
    /// </summary>
    public bool IsLapsed { get; set; }

    /// <summary>Czech name of the plan that lapsed; null unless <see cref="IsLapsed"/>.</summary>
    public string? LapsedPlanNameCs { get; set; }

    /// <summary>English name of the plan that lapsed; null unless <see cref="IsLapsed"/>.</summary>
    public string? LapsedPlanNameEn { get; set; }

    /// <summary>German name of the plan that lapsed; null unless <see cref="IsLapsed"/>.</summary>
    public string? LapsedPlanNameDe { get; set; }

    /// <summary>When the lapsed plan ended (the period end), if known; null unless <see cref="IsLapsed"/>.</summary>
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>
    /// True when an admin-given plan has ended and the coach has not chosen a plan yet. The plan fields still describe
    /// the ended plan. Never true together with <see cref="IsLapsed"/>.
    /// </summary>
    public bool PlanChoiceRequired { get; set; }

    /// <summary>Czech name of the ended given plan; null unless <see cref="PlanChoiceRequired"/>.</summary>
    public string? EndedPlanNameCs { get; set; }

    /// <summary>English name of the ended given plan; null unless <see cref="PlanChoiceRequired"/>.</summary>
    public string? EndedPlanNameEn { get; set; }

    /// <summary>German name of the ended given plan; null unless <see cref="PlanChoiceRequired"/>.</summary>
    public string? EndedPlanNameDe { get; set; }

    /// <summary>
    /// True while the coach has live clients over the cap of the plan in force and at least one of them has no kept or
    /// released decision yet. Never true together with <see cref="PlanChoiceRequired"/>.
    /// </summary>
    public bool KeepClientsChoiceRequired { get; set; }

    /// <summary>Live clients above the cap of the plan in force; zero unless <see cref="IsLapsed"/> or the coach has more live clients than the plan's limit.</summary>
    public int OverCapClientCount { get; set; }

    /// <summary>
    /// Kept slots still empty while clients are over the cap (cap minus kept clients); zero when nobody is over
    /// the cap, the plan is unlimited or a plan choice is required.
    /// </summary>
    public int FreeSlotCount { get; set; }

    /// <summary>Distinct active clients across all the coach's roles; the kept clients while lapsed.</summary>
    public int ActiveClientCount { get; set; }

    /// <summary>Active-client limit that applies now; null means unlimited.</summary>
    public int? ClientLimit { get; set; }

    /// <summary>Whether a second professional role is included, paid for as an add-on, or not available.</summary>
    public SecondRoleState SecondRole { get; set; }

    /// <summary>
    /// How often the <see cref="PriceLines"/> bill: the interval of the subscription's own price row. Monthly for the Free
    /// plan and for a lapsed plan.
    /// </summary>
    public BillingInterval BillingInterval { get; set; }

    /// <summary>Price lines per <see cref="BillingInterval"/>: the plan, plus the second-role add-on when it is paid.</summary>
    public List<SubscriptionPriceLineDto> PriceLines { get; set; } = [];

    /// <summary>The feature flags that currently apply.</summary>
    public SubscriptionFeaturesDto Features { get; set; } = new();

    /// <summary>The promotion discounting the live subscription now; null when there is none.</summary>
    public CurrentPromotionDto? Promotion { get; set; }

    /// <summary>
    /// The plan that replaces the current one at the end of the period: a scheduled downgrade, or Free when the
    /// subscription is set to end. Null when nothing is scheduled.
    /// </summary>
    public ScheduledPlanDto? ScheduledPlan { get; set; }

    /// <summary>
    /// When the second-role add-on stops being billed, while its removal is scheduled (the coach removed a role); the
    /// <see cref="PriceLines"/> keep the add-on line until then. Null when nothing is scheduled, the subscription is not
    /// live or it is set to end anyway.
    /// </summary>
    public DateTimeOffset? SecondRoleAddOnEndsAt { get; set; }
}

/// <summary>
/// The plan a coach's subscription moves to at the end of the current period.
/// </summary>
public class ScheduledPlanDto
{
    /// <summary>Code of the plan that starts next.</summary>
    public string PlanCode { get; set; } = string.Empty;

    /// <summary>Czech plan name.</summary>
    public string NameCs { get; set; } = string.Empty;

    /// <summary>English plan name.</summary>
    public string NameEn { get; set; } = string.Empty;

    /// <summary>German plan name.</summary>
    public string NameDe { get; set; } = string.Empty;

    /// <summary>When the plan starts: the end of the current period; null when unknown.</summary>
    public DateTimeOffset? StartsAt { get; set; }

    /// <summary>
    /// True when the coach can cancel this change: a live provider subscription, no admin gift in force, no pending
    /// coach-account disable and no renewal cancelled by Form Up.
    /// </summary>
    public bool Cancellable { get; set; }

    /// <summary>How often the scheduled plan's <see cref="PriceLines"/> bill; Monthly when it is Free.</summary>
    public BillingInterval BillingInterval { get; set; }

    /// <summary>Price lines of the plan that starts next: the plan, plus the second-role add-on when the coach would pay it.</summary>
    public List<SubscriptionPriceLineDto> PriceLines { get; set; } = [];
}

/// <summary>
/// The promotion discounting the coach's subscription.
/// </summary>
public class CurrentPromotionDto
{
    /// <summary>Promotion name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Percentage or fixed amount.</summary>
    public PromotionKind Kind { get; set; }

    /// <summary>Percent (1 to 100), or the amount in the smallest unit of <see cref="Currency"/>.</summary>
    public long Value { get; set; }

    /// <summary>ISO 4217 currency of a fixed amount; null for a percentage.</summary>
    public string? Currency { get; set; }

    /// <summary>How long the discount lasts for this coach.</summary>
    public PromotionDuration Duration { get; set; }

    /// <summary>Months the discount lasts; set only for <see cref="PromotionDuration.Months"/>.</summary>
    public int? DurationMonths { get; set; }

    /// <summary>Which billing cadences the promotion discounts.</summary>
    public PromotionBillingPeriod BillingPeriod { get; set; }

    /// <summary>When the discount ends, as Stripe reports it; null for a forever discount.</summary>
    public DateTimeOffset? EndsAt { get; set; }

    /// <summary>
    /// Display-only amount the promotion takes off one billing period: the plan line plus the add-on line where the coupon
    /// covers it, in the currency of <see cref="GetMySubscriptionResponse.PriceLines"/>. Zero when the promotion does not
    /// apply to the coach's plan. Stripe computes the real charge.
    /// </summary>
    public long DiscountMinorUnits { get; set; }

    /// <summary>Display-only total of the price lines after <see cref="DiscountMinorUnits"/>.</summary>
    public long DiscountedTotalMinorUnits { get; set; }
}

/// <summary>
/// One line of the monthly price.
/// </summary>
public class SubscriptionPriceLineDto
{
    /// <summary>What the line charges for.</summary>
    public SubscriptionPriceLineKind Kind { get; set; }

    /// <summary>Amount in the smallest unit of <see cref="Currency"/>.</summary>
    public long AmountMinorUnits { get; set; }

    /// <summary>ISO 4217 currency code.</summary>
    public string Currency { get; set; } = string.Empty;
}

/// <summary>
/// What a <see cref="SubscriptionPriceLineDto"/> charges for.
/// </summary>
public enum SubscriptionPriceLineKind
{
    /// <summary>The plan itself.</summary>
    Plan,

    /// <summary>The second professional role add-on.</summary>
    SecondRoleAddOn
}

/// <summary>
/// The resolved feature flags of the coach's subscription.
/// </summary>
public class SubscriptionFeaturesDto
{
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

    /// <summary>Client payments.</summary>
    public bool CanUsePayments { get; set; }

    /// <summary>Nutrition plan templates.</summary>
    public bool CanUsePlanTemplates { get; set; }

    /// <summary>The coach's own recipes.</summary>
    public bool CanUseRecipes { get; set; }

    /// <summary>The coach's own ingredients.</summary>
    public bool CanUseIngredients { get; set; }

    /// <summary>The coach side of progress photos.</summary>
    public bool CanUseProgressPhotos { get; set; }

    /// <summary>The coach side of weight and measurements.</summary>
    public bool CanUseWeightAndMeasurements { get; set; }

    /// <summary>Automations.</summary>
    public bool CanUseAutomations { get; set; }

    /// <summary>Coach packages and discounts.</summary>
    public bool CanUsePackages { get; set; }
}
