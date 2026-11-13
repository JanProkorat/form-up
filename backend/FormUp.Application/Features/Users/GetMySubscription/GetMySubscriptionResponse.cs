using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Features.Users.GetMySubscription;

/// <summary>
/// The caller's coach subscription: plan, standing, client usage, second-role state, monthly price and
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

    /// <summary>Subscription status, or null when the coach has no subscription and is on Free by default.</summary>
    public SubscriptionStatus? Status { get; set; }

    /// <summary>When the current billing period ends, if known.</summary>
    public DateTimeOffset? CurrentPeriodEndsAt { get; set; }

    /// <summary>True when the subscription will not renew.</summary>
    public bool CancelAtPeriodEnd { get; set; }

    /// <summary>While now is before this instant the assigned plan's features apply whatever the status is.</summary>
    public DateTimeOffset? FreeUntil { get; set; }

    /// <summary>True when an admin set the plan (given free) and it still applies.</summary>
    public bool IsSetByFormUp { get; set; }

    /// <summary>
    /// True when the paid plan has lapsed (Canceled or Incomplete with no free-features deadline in force). The plan
    /// fields then describe the Free plan and <see cref="ActiveClientCount"/> is the number of kept clients.
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

    /// <summary>Monthly price lines: the plan, plus the second-role add-on when it is paid.</summary>
    public List<SubscriptionPriceLineDto> PriceLines { get; set; } = [];

    /// <summary>The feature flags that currently apply.</summary>
    public SubscriptionFeaturesDto Features { get; set; } = new();
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

    /// <summary>The payments feature.</summary>
    public bool CanUsePayments { get; set; }
}
