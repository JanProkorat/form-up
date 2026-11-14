using FormUp.Application.Domain.Common;
using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Domain.Entities;

/// <summary>
/// Defines a coach subscription tier: pricing, billing cadence, and the feature
/// flags/limits that <see cref="Services.EntitlementService"/> resolves against a
/// coach's active <see cref="CoachSubscription"/>.
/// </summary>
public class SubscriptionPlan : TimestampableEntity
{
    /// <summary>
    /// Stable, human-readable identifier used to reference the plan (addressed by this,
    /// not a public GUID — see #595 for the admin CRUD surface).
    /// </summary>
    public required string Code { get; set; }

    /// <summary>
    /// Czech plan name — the repo's primary/fallback locale.
    /// </summary>
    public required string NameCs { get; set; }

    /// <summary>
    /// English plan name.
    /// </summary>
    public required string NameEn { get; set; }

    /// <summary>
    /// German plan name.
    /// </summary>
    public required string NameDe { get; set; }

    /// <summary>
    /// True when the plan covers both professional roles at no extra charge. When false,
    /// <see cref="SecondRoleAddOnPriceMinorUnits"/> is the price of the second role.
    /// </summary>
    public bool BothRolesIncluded { get; set; }

    /// <summary>
    /// Whether the plan allows creating nutrition/training plans.
    /// </summary>
    public bool CanCreatePlans { get; set; }

    /// <summary>
    /// Whether the plan allows messaging clients.
    /// </summary>
    public bool CanMessage { get; set; }

    /// <summary>
    /// Whether the plan allows sending questionnaires.
    /// </summary>
    public bool CanSendQuestionnaires { get; set; }

    /// <summary>
    /// Whether the plan allows using weekly check-ins.
    /// </summary>
    public bool CanUseWeeklyCheckIns { get; set; }

    /// <summary>
    /// Whether the plan allows per-client check-in configuration.
    /// </summary>
    public bool CanUsePerClientCheckInConfig { get; set; }

    /// <summary>
    /// Whether the plan allows client payments (bank account, payment settings, client payment tracking).
    /// </summary>
    public bool CanUsePayments { get; set; }

    /// <summary>
    /// Whether the plan allows nutrition plan templates.
    /// </summary>
    public bool CanUsePlanTemplates { get; set; }

    /// <summary>
    /// Whether the plan allows a coach's own recipe library.
    /// </summary>
    public bool CanUseRecipes { get; set; }

    /// <summary>
    /// Whether the plan allows a coach's own ingredient (food) library.
    /// </summary>
    public bool CanUseIngredients { get; set; }

    /// <summary>
    /// Whether the plan allows the coach side of progress photos.
    /// </summary>
    public bool CanUseProgressPhotos { get; set; }

    /// <summary>
    /// Whether the plan allows the coach side of weight and body measurements.
    /// </summary>
    public bool CanUseWeightAndMeasurements { get; set; }

    /// <summary>
    /// Whether the plan allows automations.
    /// </summary>
    public bool CanUseAutomations { get; set; }

    /// <summary>
    /// Whether the plan allows coach packages and discounts.
    /// </summary>
    public bool CanUsePackages { get; set; }

    /// <summary>
    /// Maximum number of active clients allowed under this plan. Null means unlimited.
    /// </summary>
    public int? MaxActiveClients { get; set; }

    /// <summary>
    /// Current plan price, expressed in the smallest unit of <see cref="Currency"/> (e.g. cents).
    /// Mirrors the open <see cref="Prices"/> row.
    /// </summary>
    public long PriceMinorUnits { get; set; }

    /// <summary>
    /// Current price of the second-role add-on in the same unit. Null when
    /// <see cref="BothRolesIncluded"/>. Mirrors the open <see cref="Prices"/> row.
    /// </summary>
    public long? SecondRoleAddOnPriceMinorUnits { get; set; }

    /// <summary>
    /// ISO 4217 currency code for <see cref="PriceMinorUnits"/>.
    /// </summary>
    public required string Currency { get; set; }

    /// <summary>
    /// Billing cadence for this plan.
    /// </summary>
    public BillingInterval BillingInterval { get; set; }

    /// <summary>
    /// Whether the plan is currently offered/selectable.
    /// </summary>
    public bool IsActive { get; set; }

    /// <summary>
    /// Price history, one row per price the plan has been sold at. Exactly one row is open.
    /// </summary>
    public ICollection<SubscriptionPlanPrice> Prices { get; set; } = [];
}
