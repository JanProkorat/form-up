namespace FormUp.Application.Features.AdminReports.GetAdminReports;

/// <summary>
/// Totals for the admin Reports page. Holds counts and amounts only, never a per-client or per-coach row. Money is in
/// minor units per currency, with no VAT, Stripe fees, refunds or payouts. Every <c>previous</c> is the raw baseline value
/// (null when there is no baseline); the client computes percentages.
/// </summary>
public class GetAdminReportsResponse
{
    /// <summary>The window the figures were read for.</summary>
    public AdminReportPeriod Period { get; set; }

    /// <summary>UTC day of the latest daily snapshot, null when none exists yet.</summary>
    public DateOnly? SnapshotDay { get; set; }

    /// <summary>
    /// UTC day of the snapshot the baseline comes from: the one on the period start day, else the earliest one inside
    /// the period. Null when no snapshot lies inside the period.
    /// </summary>
    public DateOnly? ComparedFrom { get; set; }

    /// <summary>Revenue, collections and churn.</summary>
    public AdminReportMoneyDto Money { get; set; } = new();

    /// <summary>Sign-up funnel of the coaches who signed up in the period.</summary>
    public AdminReportFunnelDto Funnel { get; set; } = new();

    /// <summary>Plan changes in the period.</summary>
    public AdminReportPlanChangesDto PlanChanges { get; set; } = new();

    /// <summary>One point per snapshot day inside the period, oldest first. Days without a snapshot are omitted.</summary>
    public List<AdminReportMrrPointDto> MrrSeries { get; set; } = [];

    /// <summary>Usage figures from the latest snapshot, null when no snapshot exists.</summary>
    public AdminReportUsageDto? Usage { get; set; }
}

/// <summary>
/// Revenue, collections and churn.
/// </summary>
public class AdminReportMoneyDto
{
    /// <summary>Monthly revenue at full price per currency. Previous comes from the baseline snapshot.</summary>
    public List<AdminReportCurrencyValueDto> MonthlyRevenueFull { get; set; } = [];

    /// <summary>Monthly revenue after promotions per currency. Previous comes from the baseline snapshot.</summary>
    public List<AdminReportCurrencyValueDto> MonthlyRevenueAfterPromotions { get; set; } = [];

    /// <summary>
    /// Revenue after promotions divided by the coaches billed in that currency. Previous divides the baseline snapshot's
    /// revenue by its total paying coach count, so it is approximate when there is more than one currency.
    /// </summary>
    public List<AdminReportAverageDto> AveragePerPayingCoach { get; set; } = [];

    /// <summary>
    /// Invoices paid since the start of the current UTC month. Previous is the previous calendar month up to the same
    /// moment of the month.
    /// </summary>
    public List<AdminReportCurrencyValueDto> CollectedThisMonth { get; set; } = [];

    /// <summary>
    /// Subscriptions cancelled in the period and the monthly revenue (after promotions) they took away. Previous is the
    /// previous window of the same length.
    /// </summary>
    public AdminReportLostDto LostToCancellations { get; set; } = new();

    /// <summary>
    /// Distinct coaches who cancelled in the period divided by the baseline snapshot's paying coach count, in percent.
    /// Null when there is no baseline or it had no paying coach.
    /// </summary>
    public decimal? ChurnPercent { get; set; }

    /// <summary>Paying coaches and plan-line revenue after promotions per plan.</summary>
    public List<AdminReportPlanRevenueDto> Plans { get; set; } = [];

    /// <summary>Second-role add-ons per currency.</summary>
    public List<AdminReportAddOnDto> AddOns { get; set; } = [];
}

/// <summary>
/// An amount in one currency with its baseline.
/// </summary>
public class AdminReportCurrencyValueDto
{
    /// <summary>ISO 4217 currency, upper case.</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>Current amount in minor units.</summary>
    public long Value { get; set; }

    /// <summary>Baseline amount in minor units, null when there is no baseline.</summary>
    public long? Previous { get; set; }
}

/// <summary>
/// An amount in one currency.
/// </summary>
public class AdminReportCurrencyAmountDto
{
    /// <summary>ISO 4217 currency, upper case.</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>Amount in minor units.</summary>
    public long AmountMinorUnits { get; set; }
}

/// <summary>
/// Average revenue per paying coach in one currency.
/// </summary>
public class AdminReportAverageDto
{
    /// <summary>ISO 4217 currency, upper case.</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>Current average in minor units.</summary>
    public long Value { get; set; }

    /// <summary>Baseline average in minor units, null when there is no baseline.</summary>
    public long? Previous { get; set; }

    /// <summary>Coaches billed in this currency now.</summary>
    public int CoachesBilled { get; set; }
}

/// <summary>
/// A count with its baseline.
/// </summary>
public class AdminReportCountDto
{
    /// <summary>Current count.</summary>
    public int Value { get; set; }

    /// <summary>Baseline count, null when there is no baseline.</summary>
    public int? Previous { get; set; }
}

/// <summary>
/// Subscriptions lost to cancellations.
/// </summary>
public class AdminReportLostDto
{
    /// <summary>Cancellation events in the window. A coach who cancels twice counts twice.</summary>
    public AdminReportCountDto Count { get; set; } = new();

    /// <summary>Monthly revenue after promotions the cancellations took away, per currency, as positive amounts.</summary>
    public List<AdminReportCurrencyValueDto> Amounts { get; set; } = [];
}

/// <summary>
/// One plan's paying coaches and revenue.
/// </summary>
public class AdminReportPlanRevenueDto
{
    /// <summary>Plan code.</summary>
    public string PlanCode { get; set; } = string.Empty;

    /// <summary>Czech plan name.</summary>
    public string NameCs { get; set; } = string.Empty;

    /// <summary>English plan name.</summary>
    public string NameEn { get; set; } = string.Empty;

    /// <summary>German plan name.</summary>
    public string NameDe { get; set; } = string.Empty;

    /// <summary>Paying coaches on the plan.</summary>
    public int PayingCoaches { get; set; }

    /// <summary>Monthly plan-line revenue after promotions per currency, add-ons excluded.</summary>
    public List<AdminReportCurrencyAmountDto> Revenue { get; set; } = [];
}

/// <summary>
/// Second-role add-ons in one currency.
/// </summary>
public class AdminReportAddOnDto
{
    /// <summary>ISO 4217 currency, upper case.</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>Billed coaches who pay the add-on.</summary>
    public int Count { get; set; }

    /// <summary>Monthly add-on revenue after promotions in minor units.</summary>
    public long RevenueMinorUnits { get; set; }
}

/// <summary>
/// The sign-up funnel of the coaches who signed up in the period. Deleted coaches drop out of the later steps, because
/// their events lose the coach link. Until sign-up records the Free choice, a coach who stays on Free writes no
/// "chose a plan" event, so <see cref="ChosePlan"/> undercounts.
/// </summary>
public class AdminReportFunnelDto
{
    /// <summary>Sign-ups in the period.</summary>
    public int SignedUp { get; set; }

    /// <summary>Distinct sign-ups who verified their email, on or after sign-up.</summary>
    public int EmailVerified { get; set; }

    /// <summary>Distinct sign-ups who chose a plan, on or after sign-up.</summary>
    public int ChosePlan { get; set; }

    /// <summary>Distinct sign-ups whose paid plan went live, on or after sign-up.</summary>
    public int StartedPaidPlan { get; set; }
}

/// <summary>
/// Plan changes in the period.
/// </summary>
public class AdminReportPlanChangesDto
{
    /// <summary>Upgrades.</summary>
    public AdminReportPlanChangeDto Upgraded { get; set; } = new();

    /// <summary>Downgrades.</summary>
    public AdminReportPlanChangeDto Downgraded { get; set; } = new();

    /// <summary>Cancellations.</summary>
    public AdminReportPlanChangeDto Cancelled { get; set; } = new();

    /// <summary>Distinct coaches with a failed payment.</summary>
    public int PaymentFailedCoaches { get; set; }

    /// <summary>Promotion codes applied. A count only; the names are not returned.</summary>
    public int PromotionCodesApplied { get; set; }
}

/// <summary>
/// One kind of plan change.
/// </summary>
public class AdminReportPlanChangeDto
{
    /// <summary>Events in the period.</summary>
    public int Count { get; set; }

    /// <summary>Summed change of monthly revenue after promotions per currency (negative for a cancellation).</summary>
    public List<AdminReportCurrencyAmountDto> RevenueEffect { get; set; } = [];
}

/// <summary>
/// Monthly recurring revenue of one snapshot day.
/// </summary>
public class AdminReportMrrPointDto
{
    /// <summary>The UTC day.</summary>
    public DateOnly Day { get; set; }

    /// <summary>Monthly revenue at full price per currency.</summary>
    public List<AdminReportCurrencyAmountDto> Full { get; set; } = [];

    /// <summary>Monthly revenue after promotions per currency.</summary>
    public List<AdminReportCurrencyAmountDto> AfterPromotions { get; set; } = [];
}

/// <summary>
/// Usage figures from the latest daily snapshot.
/// </summary>
public class AdminReportUsageDto
{
    /// <summary>Coaches with a refresh token issued in the last 30 days.</summary>
    public int ActiveCoachesLast30Days { get; set; }

    /// <summary>Clients with a refresh token issued in the last 7 days. Previous comes from the baseline snapshot.</summary>
    public AdminReportCountDto ActiveClientsLast7Days { get; set; } = new();

    /// <summary>Plan weeks published in the last 7 days.</summary>
    public int PlanWeeksPublishedLast7Days { get; set; }

    /// <summary>Free coaches at the Free plan's client limit.</summary>
    public int FreeCoachesAtClientCap { get; set; }

    /// <summary>Median live clients per coach over all coach profiles, including those with none.</summary>
    public decimal MedianClientsPerCoach { get; set; }
}
