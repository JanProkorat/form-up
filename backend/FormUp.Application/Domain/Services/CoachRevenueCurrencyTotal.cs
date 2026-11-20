namespace FormUp.Application.Domain.Services;

/// <summary>
/// A monthly revenue total in one currency.
/// </summary>
/// <param name="Currency">ISO 4217 currency.</param>
/// <param name="AmountMinorUnits">Monthly amount in the smallest currency unit.</param>
public sealed record CoachRevenueCurrencyTotal(string Currency, long AmountMinorUnits);
