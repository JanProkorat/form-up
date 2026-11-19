namespace FormUp.Application.Features.Billing.Shared;

/// <summary>
/// How a plan change plays out for the caller: now or at renewal, and whether the second-role add-on is carried.
/// The rule, with every amount normalised to a per-year figure by its own row's interval and including the second-role
/// add-on the caller pays (current side) or would carry (target side):
/// the same plan at the same interval is refused; annual to monthly always waits for the next renewal; monthly to annual
/// applies now for the same plan, and for another plan when its price ranks the same or higher at one shared interval
/// (the annual rows, or the monthly rows when the current plan has no annual row); the same interval applies now only
/// when the target costs strictly more per year.
/// </summary>
/// <param name="IsUpgrade">True when the change applies now.</param>
/// <param name="CarryAddOn">True when the caller has two active roles and the target plan does not include both.</param>
internal sealed record PlanChangeDecision(bool IsUpgrade, bool CarryAddOn);
