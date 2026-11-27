namespace FormUp.Application.Domain.Entities;

/// <summary>
/// Coaches on one plan, stored inside a <see cref="DailyReportSnapshot"/> as a JSON element.
/// </summary>
public class ReportPlanCount
{
    /// <summary>
    /// Plan code.
    /// </summary>
    public string PlanCode { get; set; } = string.Empty;

    /// <summary>
    /// Number of coaches.
    /// </summary>
    public int Count { get; set; }
}
