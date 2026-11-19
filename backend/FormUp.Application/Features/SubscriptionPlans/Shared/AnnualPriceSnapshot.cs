namespace FormUp.Application.Features.SubscriptionPlans.Shared;

/// <summary>
/// The fields of a plan's open Annual price row that the admin DTO exposes.
/// </summary>
/// <param name="PriceMinorUnits">Annual price per year.</param>
/// <param name="SecondRoleAddOnPriceMinorUnits">Annual second-role add-on price per year, or null.</param>
/// <param name="ExternalPriceId">Stripe price id, or null when the row was saved without Stripe.</param>
public sealed record AnnualPriceSnapshot(long PriceMinorUnits, long? SecondRoleAddOnPriceMinorUnits, string? ExternalPriceId);
