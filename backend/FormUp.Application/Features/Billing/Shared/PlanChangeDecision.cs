namespace FormUp.Application.Features.Billing.Shared;

/// <summary>
/// How a plan change plays out for the caller: now or at renewal, and whether the second-role add-on is carried.
/// </summary>
/// <param name="IsUpgrade">True when the target plan costs more per year, so it applies now.</param>
/// <param name="CarryAddOn">True when the caller has two active roles and the target plan does not include both.</param>
internal sealed record PlanChangeDecision(bool IsUpgrade, bool CarryAddOn);
