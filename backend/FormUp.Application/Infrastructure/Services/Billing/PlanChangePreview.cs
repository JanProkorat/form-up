namespace FormUp.Application.Infrastructure.Services.Billing;

/// <summary>
/// What an immediate plan change would charge today, as the payment provider previews it.
/// </summary>
/// <param name="TodayAmountMinorUnits">The prorated amount invoiced now, with tax, in the smallest currency unit.</param>
/// <param name="TodayTaxMinorUnits">The tax included in <paramref name="TodayAmountMinorUnits"/>.</param>
/// <param name="Currency">ISO 4217 currency.</param>
public sealed record PlanChangePreview(long TodayAmountMinorUnits, long TodayTaxMinorUnits, string Currency);
