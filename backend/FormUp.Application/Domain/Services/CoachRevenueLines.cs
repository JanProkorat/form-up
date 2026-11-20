using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Domain.Services;

/// <summary>
/// The price lines a coach pays for one billing period: the plan and, when paid, the second-role add-on.
/// </summary>
/// <param name="PlanCode">Plan code.</param>
/// <param name="Currency">ISO 4217 currency.</param>
/// <param name="Interval">Billing interval.</param>
/// <param name="PlanMinorUnits">Plan line.</param>
/// <param name="AddOnMinorUnits">Add-on line the coach pays, zero when none.</param>
public sealed record CoachRevenueLines(
    string PlanCode,
    string Currency,
    BillingInterval Interval,
    long PlanMinorUnits,
    long AddOnMinorUnits)
{
    /// <summary>
    /// The lines of a plan's price row. The add-on counts only when the coach pays it and the plan does not already
    /// include both roles.
    /// </summary>
    /// <param name="plan">The plan.</param>
    /// <param name="price">The price row the coach pays.</param>
    /// <param name="paysSecondRoleAddOn">Whether the coach pays the second-role add-on.</param>
    public static CoachRevenueLines Of(SubscriptionPlan plan, SubscriptionPlanPrice price, bool paysSecondRoleAddOn) =>
        new(
            plan.Code,
            price.Currency,
            price.BillingInterval,
            price.PriceMinorUnits,
            paysSecondRoleAddOn && !plan.BothRolesIncluded ? price.SecondRoleAddOnPriceMinorUnits ?? 0 : 0);
}
