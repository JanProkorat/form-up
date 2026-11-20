namespace FormUp.Application.Domain.Entities;

/// <summary>
/// An amount in one currency, stored inside a <see cref="DailyReportSnapshot"/> as a JSON element.
/// </summary>
public class ReportCurrencyAmount
{
    /// <summary>
    /// ISO 4217 currency.
    /// </summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>
    /// Amount in the smallest currency unit.
    /// </summary>
    public long AmountMinorUnits { get; set; }
}
