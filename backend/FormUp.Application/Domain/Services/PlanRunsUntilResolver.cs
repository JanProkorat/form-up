namespace FormUp.Application.Domain.Services;

/// <summary>
/// Finds the last day a plan's published weeks cover.
/// </summary>
public static class PlanRunsUntilResolver
{
    /// <summary>
    /// The last day of the plan's last published week, or null when the plan has no start date or no
    /// published week. Week N covers the seven days from the start date plus (N - 1) weeks.
    /// </summary>
    /// <param name="startDate">The plan's start date.</param>
    /// <param name="publishedWeekNumbers">Week numbers whose own status is Published.</param>
    public static DateOnly? Resolve(DateTime? startDate, IEnumerable<int> publishedWeekNumbers)
    {
        var weekNumbers = publishedWeekNumbers.ToList();

        if (startDate is null || weekNumbers.Count == 0)
        {
            return null;
        }

        return DateOnly.FromDateTime(startDate.Value).AddDays(weekNumbers.Max() * 7 - 1);
    }
}
