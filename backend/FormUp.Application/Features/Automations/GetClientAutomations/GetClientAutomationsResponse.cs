using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Features.Automations.GetClientAutomations;

/// <summary>
/// A client's effective automation rules with a 30-day summary of what was sent.
/// </summary>
public class GetClientAutomationsResponse
{
    /// <summary>The rules by creation order.</summary>
    public List<ClientAutomationItem> Items { get; set; } = [];

    /// <summary>Totals over the last 30 days across all rules.</summary>
    public ClientAutomationSummary Last30Days { get; set; } = new();
}

/// <summary>
/// One row of a client's effective rule list.
/// </summary>
public class ClientAutomationItem
{
    /// <summary>Public id of the rule; null for a linked check-in row.</summary>
    public Guid? RuleId { get; set; }

    /// <summary>Where the row comes from.</summary>
    public AutomationRuleSource Source { get; set; }

    /// <summary>Rule name; null for a linked check-in row.</summary>
    public string? Name { get; set; }

    /// <summary>Training or Nutrition for a linked check-in row; null for rules.</summary>
    public string? Profession { get; set; }

    /// <summary>What the rule asks of the client; null for a linked check-in row.</summary>
    public AutomationActionType? ActionType { get; set; }

    /// <summary>Public id of the questionnaire a SendForm rule sends.</summary>
    public Guid? QuestionnaireId { get; set; }

    /// <summary>Public id of the check-in form this client gets instead of the rule's own; null when they follow the rule's form.</summary>
    public Guid? CheckInQuestionnaireId { get; set; }

    /// <summary>Title of the client's own check-in form; null when they follow the rule's form.</summary>
    public string? CheckInQuestionnaireName { get; set; }

    /// <summary>Effective message.</summary>
    public string? Message { get; set; }

    /// <summary>Effective trigger kind; null for a linked check-in row.</summary>
    public AutomationTriggerKind? TriggerKind { get; set; }

    /// <summary>Effective schedule interval.</summary>
    public int? ScheduleInterval { get; set; }

    /// <summary>Effective schedule unit.</summary>
    public AutomationScheduleUnit? ScheduleUnit { get; set; }

    /// <summary>Effective client-local time of day; null on an event rule means 09:00.</summary>
    public TimeSpan? LocalTime { get; set; }

    /// <summary>Effective event.</summary>
    public AutomationEventType? EventType { get; set; }

    /// <summary>Effective event offset in days.</summary>
    public int? EventOffsetDays { get; set; }

    /// <summary>Effective event offset direction.</summary>
    public AutomationOffsetDirection? EventOffsetDirection { get; set; }

    /// <summary>Effective once-per-plan flag.</summary>
    public bool OncePerPlan { get; set; }

    /// <summary>Effective skip-while-unanswered flag.</summary>
    public bool SkipWhilePreviousUnanswered { get; set; }

    /// <summary>Effective send limit.</summary>
    public int? StopAfterSends { get; set; }

    /// <summary>Effective enabled flag.</summary>
    public bool Enabled { get; set; }

    /// <summary>Whether a pause is in effect right now.</summary>
    public bool IsPaused { get; set; }

    /// <summary>Last local day of the pause, while it is in effect.</summary>
    public DateOnly? PausedUntil { get; set; }

    /// <summary>Note of the pause, while it is in effect.</summary>
    public string? PauseNote { get; set; }

    /// <summary>Effective check-in weekday; null for a rule that is not a check-in.</summary>
    public DayOfWeek? ScheduleWeekday { get; set; }

    /// <summary>Effective check-in answer window in hours.</summary>
    public int? AnswerWindowHours { get; set; }

    /// <summary>Effective check-in reminder delay in hours; null means no reminder.</summary>
    public int? ReminderAfterHours { get; set; }

    /// <summary>Effective notify-the-coach-on-missed-week flag of a check-in.</summary>
    public bool? NotifyCoachOnMissedWeek { get; set; }

    /// <summary>Next send (UTC); null when paused, disabled, or an event other than plan started or ended.</summary>
    public DateTime? NextSendAt { get; set; }

    /// <summary>The most recent run; null until a run exists.</summary>
    public ClientAutomationLastResult? LastResult { get; set; }

    /// <summary>Requests sent in the last 30 days.</summary>
    public int Sent30Days { get; set; }

    /// <summary>Requests answered in the last 30 days.</summary>
    public int Answered30Days { get; set; }

    /// <summary>Occurrences skipped in the last 30 days.</summary>
    public int Skipped30Days { get; set; }
}

/// <summary>
/// The most recent run of a rule for the client.
/// </summary>
public class ClientAutomationLastResult
{
    /// <summary>Outcome.</summary>
    public AutomationRunStatus Status { get; set; }

    /// <summary>Why it was skipped.</summary>
    public AutomationSkipReason? SkipReason { get; set; }

    /// <summary>When it was sent (UTC).</summary>
    public DateTime? SentAt { get; set; }

    /// <summary>When the client answered (UTC).</summary>
    public DateTime? AnsweredAt { get; set; }
}

/// <summary>
/// Totals over the last 30 days.
/// </summary>
public class ClientAutomationSummary
{
    /// <summary>Requests sent.</summary>
    public int Sent { get; set; }

    /// <summary>Requests answered.</summary>
    public int Answered { get; set; }

    /// <summary>Requests sent and still waiting for an answer.</summary>
    public int Waiting { get; set; }

    /// <summary>Occurrences skipped.</summary>
    public int Skipped { get; set; }
}
