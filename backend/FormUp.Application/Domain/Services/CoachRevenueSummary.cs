namespace FormUp.Application.Domain.Services;

/// <summary>
/// Coach counts per status bucket and monthly revenue over all coaches, as the admin Coaches stats and the daily
/// snapshot both read them. Paying + Free + GivenFree make up All; PaymentFailed and Cancelling are subsets of Paying.
/// </summary>
/// <param name="AllCount">All coach profiles.</param>
/// <param name="PayingCount">Paying coaches including failed and cancelling ones.</param>
/// <param name="PaymentFailedCount">Paying coaches whose last payment failed.</param>
/// <param name="CancellingCount">Paying coaches whose renewal is cancelled.</param>
/// <param name="FreeCount">Coaches on Free.</param>
/// <param name="GivenFreeCount">Coaches on an admin-given plan.</param>
/// <param name="PayingByPlan">Paying coaches per plan, most first.</param>
/// <param name="FullMonthlyRevenue">Monthly revenue at full price per currency.</param>
/// <param name="DiscountedMonthlyRevenue">Monthly revenue after promotions per currency.</param>
public sealed record CoachRevenueSummary(
    int AllCount,
    int PayingCount,
    int PaymentFailedCount,
    int CancellingCount,
    int FreeCount,
    int GivenFreeCount,
    IReadOnlyList<CoachRevenuePlanCount> PayingByPlan,
    IReadOnlyList<CoachRevenueCurrencyTotal> FullMonthlyRevenue,
    IReadOnlyList<CoachRevenueCurrencyTotal> DiscountedMonthlyRevenue);
