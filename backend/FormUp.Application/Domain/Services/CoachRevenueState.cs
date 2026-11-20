namespace FormUp.Application.Domain.Services;

/// <summary>
/// What a coach's subscription row looked like at one moment, captured so a later write can tell what changed.
/// </summary>
/// <param name="PlanCode">The plan the row was on.</param>
/// <param name="PriceId">The price row the coach paid.</param>
/// <param name="PendingPriceId">The price row scheduled for the next renewal, or null.</param>
/// <param name="IsPaying">Whether the coach was in the Paying bucket (paid plan, not a gift, Trialing, Active or PastDue).</param>
/// <param name="ListMonthlyMinorUnits">The price lines per month at full price, whatever the status; compares two plans.</param>
/// <param name="Amount">The revenue the subscription brought (zero while trialing or not paying).</param>
public sealed record CoachRevenueState(
    string PlanCode,
    long PriceId,
    long? PendingPriceId,
    bool IsPaying,
    long ListMonthlyMinorUnits,
    CoachRevenueAmount Amount);
