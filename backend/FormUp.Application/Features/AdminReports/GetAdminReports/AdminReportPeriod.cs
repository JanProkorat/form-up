namespace FormUp.Application.Features.AdminReports.GetAdminReports;

/// <summary>
/// The look-back window of the admin Reports page.
/// </summary>
public enum AdminReportPeriod
{
    /// <summary>The last 30 days (query value <c>Days30</c>).</summary>
    Days30 = 0,

    /// <summary>The last 90 days (query value <c>Days90</c>).</summary>
    Days90 = 1,

    /// <summary>The last 12 calendar months (query value <c>Months12</c>).</summary>
    Months12 = 2,
}
