namespace FormUp.Application.Domain.Services;

/// <summary>
/// Display-only saving of paying a plan yearly instead of twelve times monthly.
/// </summary>
public static class AnnualSaving
{
    /// <summary>
    /// Whole percent the annual plan line saves against twelve monthly plan lines, or null when there is no annual price,
    /// the monthly price is zero or the annual price does not save anything. The add-on line is not part of it.
    /// </summary>
    /// <param name="monthlyPriceMinorUnits">The monthly plan price.</param>
    /// <param name="annualPriceMinorUnits">The annual plan price, or null when the plan is not sold annually.</param>
    public static int? Percent(long monthlyPriceMinorUnits, long? annualPriceMinorUnits)
    {
        if (annualPriceMinorUnits is not { } annual || monthlyPriceMinorUnits <= 0)
        {
            return null;
        }

        var yearOfMonths = 12m * monthlyPriceMinorUnits;
        var percent = (int)Math.Round((yearOfMonths - annual) / yearOfMonths * 100m, MidpointRounding.AwayFromZero);

        return percent > 0 ? percent : null;
    }
}
