using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Domain.Services;

/// <summary>
/// What one subscription pays per billing period, at full price and after its promotion. The zero value stands for a
/// subscription that brings no revenue (Free, given free, trialing, not live).
/// </summary>
/// <param name="Currency">ISO 4217 currency, empty for the zero value.</param>
/// <param name="Interval">Billing interval the period amounts are for.</param>
/// <param name="FullPeriodMinorUnits">Plan line plus a paid add-on per period, before any promotion.</param>
/// <param name="DiscountedPeriodMinorUnits">The same after the promotion.</param>
public sealed record CoachRevenueAmount(
    string Currency,
    BillingInterval Interval,
    long FullPeriodMinorUnits,
    long DiscountedPeriodMinorUnits)
{
    private const int MonthsPerYear = 12;

    /// <summary>The amount of a subscription that brings no revenue.</summary>
    public static CoachRevenueAmount None { get; } = new(string.Empty, BillingInterval.Monthly, 0, 0);

    /// <summary>Full price per month; an annual price counts as one twelfth.</summary>
    public long FullMonthlyMinorUnits => ToMonthly(FullPeriodMinorUnits, Interval);

    /// <summary>Price after the promotion per month; an annual price counts as one twelfth.</summary>
    public long DiscountedMonthlyMinorUnits => ToMonthly(DiscountedPeriodMinorUnits, Interval);

    /// <summary>
    /// Converts a per-period total to a monthly one: an annual total is divided by twelve.
    /// </summary>
    /// <param name="periodMinorUnits">Amount per billing period.</param>
    /// <param name="interval">The billing interval.</param>
    public static long ToMonthly(long periodMinorUnits, BillingInterval interval) =>
        interval == BillingInterval.Annual ? periodMinorUnits / MonthsPerYear : periodMinorUnits;
}
