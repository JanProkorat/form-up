namespace FormUp.Application.Domain.Services;

/// <summary>
/// Monthly revenue of one plan's plan line (add-on excluded) after promotions, within one currency.
/// </summary>
/// <param name="PlanCode">Plan code.</param>
/// <param name="DiscountedMonthlyMinorUnits">Monthly amount after promotions in the smallest currency unit.</param>
public sealed record CoachRevenuePlanAmount(string PlanCode, long DiscountedMonthlyMinorUnits);
