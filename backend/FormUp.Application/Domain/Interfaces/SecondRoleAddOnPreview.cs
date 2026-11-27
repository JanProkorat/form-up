namespace FormUp.Application.Domain.Interfaces;

/// <summary>
/// What adding the second-role add-on would charge today, as the provider calculates it.
/// </summary>
/// <param name="TodayAmountMinorUnits">The prorated amount due today including tax, in the smallest currency unit.</param>
/// <param name="TodayTaxMinorUnits">The tax part of <paramref name="TodayAmountMinorUnits"/>; zero when no VAT is charged.</param>
/// <param name="Currency">ISO 4217 currency of both amounts.</param>
public sealed record SecondRoleAddOnPreview(long TodayAmountMinorUnits, long TodayTaxMinorUnits, string Currency);
