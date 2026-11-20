using FormUp.Application.Domain.Common;

namespace FormUp.Application.Domain.Entities;

/// <summary>
/// Totals of one UTC day for the admin Reports trends. One row per day; a re-run of the same day overwrites it with the
/// latest figures. Holds counts and amounts only, never a client's or coach's data.
/// </summary>
public class DailyReportSnapshot : TimestampableEntity
{
    /// <summary>
    /// The UTC day the figures describe. Unique.
    /// </summary>
    public DateOnly Day { get; set; }

    /// <summary>
    /// All coach profiles.
    /// </summary>
    public int TotalCoachCount { get; set; }

    /// <summary>
    /// Paying coaches (a paid plan that is not a gift), including those with a failed payment or a cancelled renewal.
    /// </summary>
    public int PayingCoachCount { get; set; }

    /// <summary>
    /// Paying coaches per plan.
    /// </summary>
    public List<ReportPlanCount> PayingCoachesByPlan { get; set; } = [];

    /// <summary>
    /// Coaches on the Free plan, expired gifts included, admin-given plans excluded.
    /// </summary>
    public int FreeCoachCount { get; set; }

    /// <summary>
    /// Coaches whose plan an admin gave for free and whose gift is still in force.
    /// </summary>
    public int GivenFreeCoachCount { get; set; }

    /// <summary>
    /// Monthly recurring revenue at full price per currency (annual prices as one twelfth).
    /// </summary>
    public List<ReportCurrencyAmount> MonthlyRevenueFull { get; set; } = [];

    /// <summary>
    /// Monthly recurring revenue after promotions per currency, measured like <see cref="MonthlyRevenueFull"/>.
    /// </summary>
    public List<ReportCurrencyAmount> MonthlyRevenueAfterPromotions { get; set; } = [];

    /// <summary>
    /// Coaches with a refresh token issued in the last 30 days. Tokens are issued at sign-in and on every rotation.
    /// </summary>
    public int ActiveCoachesLast30Days { get; set; }

    /// <summary>
    /// Clients with a refresh token issued in the last 7 days. Clients use only the mobile app, so this tracks it.
    /// A user holding both roles counts under each.
    /// </summary>
    public int ActiveClientsLast7Days { get; set; }

    /// <summary>
    /// Nutrition and training plan weeks published in the last 7 days.
    /// </summary>
    public int PlanWeeksPublishedLast7Days { get; set; }

    /// <summary>
    /// Free coaches whose live clients have reached the Free plan's client limit.
    /// </summary>
    public int FreeCoachesAtClientCap { get; set; }

    /// <summary>
    /// Median number of live clients per coach, taken over all coach profiles including those with none.
    /// </summary>
    public decimal MedianClientsPerCoach { get; set; }
}
