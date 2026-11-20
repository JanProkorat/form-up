namespace FormUp.Application.Domain.Services;

/// <summary>
/// The revenue breakdown of one currency: how many coaches bring revenue, the plan lines per plan and the add-on lines,
/// all after promotions. Plan lines plus add-on lines make up the currency's discounted monthly revenue.
/// </summary>
/// <param name="Currency">ISO 4217 currency.</param>
/// <param name="CoachesBilled">Coaches who bring revenue in this currency (trialing, free and gifted coaches excluded).</param>
/// <param name="AddOnCount">Billed coaches who pay the second-role add-on.</param>
/// <param name="AddOnDiscountedMonthlyMinorUnits">Monthly add-on revenue after promotions.</param>
/// <param name="DiscountedByPlan">Monthly plan-line revenue after promotions per plan, ordered by plan code.</param>
public sealed record CoachRevenueCurrencyDetail(
    string Currency,
    int CoachesBilled,
    int AddOnCount,
    long AddOnDiscountedMonthlyMinorUnits,
    IReadOnlyList<CoachRevenuePlanAmount> DiscountedByPlan);
