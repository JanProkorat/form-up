using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.SubscriptionPlans.Shared;

namespace FormUp.Application.Features.SubscriptionPlans.UpdateSubscriptionPlan;

/// <summary>
/// Request model for updating an existing subscription tier. <c>Code</c> is bound from the
/// route and identifies which plan to update — it is not an updatable field (immutable after
/// create; see <see cref="Domain.Entities.SubscriptionPlan.Code"/>).
/// </summary>
/// <remarks>
/// A PUT replaces the full resource, so every field below is required — a body that omits
/// one 400s in <see cref="UpdateSubscriptionPlanValidator"/> rather than silently defaulting
/// (an omitted <c>bool</c> would otherwise bind <c>false</c> and could revoke an entitlement
/// or deactivate a live tier; an omitted <c>MaxActiveClients</c> would bind <c>null</c> and
/// could silently grant unlimited clients). <see cref="MaxActiveClients"/> uses
/// <see cref="OptionalField{T}"/> specifically so an explicit <c>null</c> can still mean
/// "unlimited" while an omitted field still 400s.
/// </remarks>
public class UpdateSubscriptionPlanRequest
{
    /// <summary>Route-bound identifier of the plan to update. Not itself updatable.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>Czech plan name.</summary>
    public string NameCs { get; set; } = string.Empty;

    /// <summary>English plan name.</summary>
    public string NameEn { get; set; } = string.Empty;

    /// <summary>German plan name.</summary>
    public string NameDe { get; set; } = string.Empty;

    /// <summary>
    /// True when the plan covers both professional roles at no extra charge. Required; exactly
    /// one of this and <see cref="SecondRoleAddOnPriceMinorUnits"/> must be set.
    /// </summary>
    public bool? BothRolesIncluded { get; set; }

    /// <summary>Price of the second-role add-on. Must be positive; omit when both roles are included.</summary>
    public long? SecondRoleAddOnPriceMinorUnits { get; set; }

    /// <summary>
    /// When the price changes, mark every coach currently on the plan to move to the new price
    /// at their next renewal. Defaults to false: coaches keep the price row they pay.
    /// </summary>
    public bool MoveExistingCoachesAtNextRenewal { get; set; }

    /// <summary>Whether the plan allows creating nutrition/training plans. Required.</summary>
    public bool? CanCreatePlans { get; set; }

    /// <summary>Whether the plan allows messaging clients. Required.</summary>
    public bool? CanMessage { get; set; }

    /// <summary>Whether the plan allows sending questionnaires. Required.</summary>
    public bool? CanSendQuestionnaires { get; set; }

    /// <summary>Whether the plan allows using weekly check-ins. Required.</summary>
    public bool? CanUseWeeklyCheckIns { get; set; }

    /// <summary>Whether the plan allows per-client check-in configuration. Required.</summary>
    public bool? CanUsePerClientCheckInConfig { get; set; }

    /// <summary>Whether the plan allows the payments feature. Required.</summary>
    public bool? CanUsePayments { get; set; }

    /// <summary>
    /// Maximum number of active clients allowed under this plan. Required — must be present
    /// in the body. An explicit <c>null</c> means unlimited; omitting the field entirely 400s.
    /// </summary>
    public OptionalField<int?> MaxActiveClients { get; set; }

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

    /// <summary>
    /// Whether the plan is currently offered/selectable. Required — reactivation/deactivation
    /// happens through this field, and an omitted value must not silently deactivate a live
    /// tier. There is no separate "reactivate" endpoint.
    /// </summary>
    public bool? IsActive { get; set; }
}
