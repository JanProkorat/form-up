using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Features.Billing.Shared;

/// <summary>
/// The open price row of a paid plan a coach asks to move to, as the plan-change endpoints read it.
/// </summary>
/// <param name="PriceId">Id of the open price row.</param>
/// <param name="PlanId">Id of the plan.</param>
/// <param name="Code">Plan code.</param>
/// <param name="IsActive">Whether the plan can still be bought.</param>
/// <param name="BothRolesIncluded">Whether the plan covers both roles at no extra charge.</param>
/// <param name="BillingInterval">Billing cadence.</param>
/// <param name="PriceMinorUnits">Plan price in the smallest currency unit.</param>
/// <param name="AddOnAmount">Second-role add-on price, or null when the plan has none.</param>
/// <param name="Currency">ISO 4217 currency.</param>
/// <param name="ExternalPriceId">The provider price id, or null when the price is not set up there.</param>
internal sealed record PlanChangeTarget(
    long PriceId,
    long PlanId,
    string Code,
    bool IsActive,
    bool BothRolesIncluded,
    BillingInterval BillingInterval,
    long PriceMinorUnits,
    long? AddOnAmount,
    string Currency,
    string? ExternalPriceId);
