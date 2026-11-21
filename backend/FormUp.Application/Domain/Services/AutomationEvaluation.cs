using FormUp.Application.Domain.Entities;

namespace FormUp.Application.Domain.Services;

/// <summary>
/// Everything the worker needs to decide and deliver one rule for one client, loaded fresh right
/// before the send so the live link, the coach role and the audience are re-checked every time.
/// </summary>
public sealed class AutomationEvaluation
{
    /// <summary>The rule, with its questionnaire and audience clients loaded.</summary>
    public required AutomationRule Rule { get; init; }

    /// <summary>The coach who owns the rule, with their user loaded.</summary>
    public required ProfessionalProfile Coach { get; init; }

    /// <summary>The client, as covered by a live link that grants access and whose coach role stands.</summary>
    public required AutomationCoveredClient Client { get; init; }

    /// <summary>The rule as this client experiences it: override and pause applied.</summary>
    public required AutomationEffectiveRule Effective { get; init; }

    /// <summary>
    /// The check-in form the client's applicable, customised override picks instead of the rule's own; null
    /// when the client follows the rule's form. It may be inactive, which makes the send fail.
    /// </summary>
    public Questionnaire? OverrideQuestionnaire { get; init; }

    /// <summary>The form this client is sent: the override's when it picks one, otherwise the rule's.</summary>
    public Questionnaire? EffectiveQuestionnaire => OverrideQuestionnaire ?? Rule.Questionnaire;

    /// <summary>The client's time zone.</summary>
    public required TimeZoneInfo Zone { get; init; }

    /// <summary>The client's schedule start date in their local calendar.</summary>
    public required DateOnly StartLocalDate { get; init; }

    /// <summary>The moment a weekly check-in may not start before; see <see cref="AutomationNextSendCalculator.ResolveFirstSendNotBefore"/>.</summary>
    public required DateTime FirstSendNotBeforeUtc { get; init; }

    /// <summary>The coach's display name, for the messages sent in their name.</summary>
    public string CoachName => $"{Coach.User.FirstName} {Coach.User.LastName}".Trim();
}
