using System.Text.Json;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.Questionnaires.Dtos;
using FormUp.Application.Features.Questionnaires.Shared;

namespace FormUp.Application.Features.Questionnaires.GetClientForms;

/// <summary>
/// The forms a coach sent a client, split by collaboration.
/// </summary>
public class GetClientFormsResponse
{
    /// <summary>Answered forms and forms still waiting, from the current collaboration, newest first.</summary>
    public List<ClientFormItem> Current { get; set; } = [];

    /// <summary>Answered forms from an earlier collaboration with the same client, newest first.</summary>
    public List<ClientFormItem> Previous { get; set; } = [];
}

/// <summary>
/// One form sent to the client, with its answers once submitted.
/// </summary>
public class ClientFormItem
{
    /// <summary>Public id of the response.</summary>
    public Guid ResponsePublicId { get; set; }

    /// <summary>Onboarding, questionnaire or check-in.</summary>
    public QuestionnaireResponseKind Kind { get; set; }

    /// <summary>Title of the form.</summary>
    public string QuestionnaireTitle { get; set; } = null!;

    /// <summary>Waiting, answered, or (check-ins only) missed.</summary>
    public FormResponseState State { get; set; }

    /// <summary>The plan type the invite or request behind the form granted, when known.</summary>
    public LinkCapabilityScope? Scope { get; set; }

    /// <summary>When the form was sent (UTC).</summary>
    public DateTime SentAt { get; set; }

    /// <summary>When the client submitted it (UTC); null while unanswered.</summary>
    public DateTime? SubmittedAt { get; set; }

    /// <summary>True when the form is submitted and the coach has not opened it yet.</summary>
    public bool IsNew { get; set; }

    /// <summary>When the coach first opened the submitted form (UTC).</summary>
    public DateTime? SeenAt { get; set; }

    /// <summary>For a check-in: the Monday of the week it covers.</summary>
    public DateOnly? CheckInWeekStart { get; set; }

    /// <summary>For a check-in: when it is due (UTC).</summary>
    public DateTime? CheckInDueAtUtc { get; set; }

    private static readonly JsonSerializerOptions ConfigOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The answers; empty until the form is submitted.</summary>
    public List<ClientFormAnswer> Answers { get; set; } = [];

    /// <summary>Maps a response to its list item; the state is worked out against <paramref name="nowUtc"/>.</summary>
    /// <param name="response">The response, with its answers and questionnaire loaded.</param>
    /// <param name="nowUtc">The current time, used to tell a missed check-in from a waiting one.</param>
    public static ClientFormItem FromEntity(QuestionnaireResponse response, DateTime nowUtc)
    {
        var submitted = response.Status == QuestionnaireResponseStatus.Submitted;
        var sections = QuestionSectionResolver.Resolve(response.Questionnaire.Questions);

        return new ClientFormItem
        {
            ResponsePublicId = response.PublicId,
            Kind = response.Kind,
            QuestionnaireTitle = response.Questionnaire.Title,
            State = ResolveState(response, submitted, nowUtc),
            Scope = response.Scope,
            SentAt = response.DateCreated,
            SubmittedAt = response.SubmittedAt,
            IsNew = submitted && response.ReviewedByCoachAtUtc is null,
            SeenAt = response.ReviewedByCoachAtUtc,
            CheckInWeekStart = response.CheckInWeekStart,
            CheckInDueAtUtc = response.CheckInDueAtUtc,
            Answers = submitted
                ? response.Answers.Select(answer =>
                {
                    var (sectionLabel, sectionOrder) = sections.GetValueOrDefault(answer.QuestionId);
                    return new ClientFormAnswer
                    {
                        QuestionPublicId = answer.Question.PublicId,
                        QuestionLabel = answer.Question.Label,
                        QuestionType = answer.Question.Type,
                        ValueText = answer.ValueText,
                        ValueNumber = answer.ValueNumber,
                        ValueJson = answer.ValueJson,
                        FileUrl = answer.FileUrl,
                        ScaleMax = ReadScaleMax(answer.Question),
                        SectionLabel = sectionLabel,
                        SectionOrder = sectionOrder,
                    };
                }).ToList()
                : [],
        };
    }

    private static decimal? ReadScaleMax(QuestionnaireQuestion question)
    {
        if (question.Type != QuestionTypes.Scale || string.IsNullOrWhiteSpace(question.Config))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ScaleQuestionConfig>(question.Config, ConfigOptions)?.Max;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static FormResponseState ResolveState(QuestionnaireResponse response, bool submitted, DateTime nowUtc)
    {
        if (submitted)
        {
            return FormResponseState.Answered;
        }

        return response.Kind == QuestionnaireResponseKind.CheckIn && response.CheckInDueAtUtc < nowUtc
            ? FormResponseState.Missed
            : FormResponseState.Waiting;
    }
}

/// <summary>
/// One answer: the question's text and type, and the value the client gave.
/// </summary>
public class ClientFormAnswer
{
    /// <summary>Public id of the question.</summary>
    public Guid QuestionPublicId { get; set; }

    /// <summary>The question text.</summary>
    public string QuestionLabel { get; set; } = null!;

    /// <summary>The question type, e.g. short_text or scale.</summary>
    public string QuestionType { get; set; } = null!;

    /// <summary>Text value, for text and choice questions.</summary>
    public string? ValueText { get; set; }

    /// <summary>Numeric value, for number and scale questions.</summary>
    public decimal? ValueNumber { get; set; }

    /// <summary>JSON value, for multi-select questions.</summary>
    public string? ValueJson { get; set; }

    /// <summary>File URL, for upload questions.</summary>
    public string? FileUrl { get; set; }

    /// <summary>Configured max of a scale question; null for other types or when the config has none.</summary>
    public decimal? ScaleMax { get; set; }

    /// <summary>Label of the section the question sits under, or null before the first section.</summary>
    public string? SectionLabel { get; set; }

    /// <summary>0-based order of the section, or null when <see cref="SectionLabel"/> is null.</summary>
    public int? SectionOrder { get; set; }
}
