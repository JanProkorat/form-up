using FormUp.Application.Domain.Enums;

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

    /// <summary>Whether the plan allows the payments feature. Defaults to false.</summary>
    public bool CanUsePayments { get; set; }

    /// <summary>Maximum number of active clients allowed under this plan. Null means unlimited.</summary>
    public int? MaxActiveClients { get; set; }

    /// <summary>Plan price, expressed in the smallest unit of <see cref="Currency"/>.</summary>
    public long PriceMinorUnits { get; set; }

    /// <summary>ISO 4217 currency code for <see cref="PriceMinorUnits"/>.</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>Billing cadence for this plan.</summary>
    public BillingInterval BillingInterval { get; set; }

    /// <summary>
    /// Ignored. The backend creates the Stripe price itself when a price row opens; the field stays only so existing
    /// clients keep working. The response returns the price id the backend stored.
    /// </summary>
    public string? ExternalPriceId { get; set; }

    /// <summary>Whether the plan is currently offered/selectable. Defaults to true on create.</summary>
    public bool IsActive { get; set; } = true;
}
