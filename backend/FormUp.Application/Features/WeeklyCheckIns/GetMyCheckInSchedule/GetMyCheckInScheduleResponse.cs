namespace FormUp.Application.Features.WeeklyCheckIns.GetMyCheckInSchedule;

/// <summary>
/// The client's check-in schedule: one entry per coach whose check-in is currently set to arrive.
/// </summary>
public class GetMyCheckInScheduleResponse
{
    /// <summary>One entry per coach with an active check-in rule; empty when none applies.</summary>
    public List<MyCheckInScheduleItem> Items { get; set; } = [];
}

/// <summary>
/// When one coach's weekly check-in arrives and how long the client can answer, in the client's time zone.
/// </summary>
public class MyCheckInScheduleItem
{
    /// <summary>Public id of the coach.</summary>
    public Guid ProfessionalPublicId { get; set; }

    /// <summary>Full name of the coach.</summary>
    public string ProfessionalName { get; set; } = string.Empty;

    /// <summary>Profession of the link: <c>Training</c> or <c>Nutrition</c>.</summary>
    public string Profession { get; set; } = string.Empty;

    /// <summary>Day of the week the check-in is sent, in the client's time zone.</summary>
    public DayOfWeek SendWeekday { get; set; }

    /// <summary>Local time of day the check-in is sent (HH:mm), in the client's time zone.</summary>
    public string SendTime { get; set; } = string.Empty;

    /// <summary>Day of the week the answer window ends, in the client's time zone.</summary>
    public DayOfWeek AnswerUntilWeekday { get; set; }
}
