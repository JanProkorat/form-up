namespace FormUp.Application.Features.AdminReports.GetAdminReports;

/// <summary>
/// Query for the admin Reports page.
/// </summary>
public class GetAdminReportsRequest
{
    /// <summary>Look-back window: Days30, Days90 or Months12. Defaults to Days30.</summary>
    public AdminReportPeriod Period { get; set; } = AdminReportPeriod.Days30;
}
