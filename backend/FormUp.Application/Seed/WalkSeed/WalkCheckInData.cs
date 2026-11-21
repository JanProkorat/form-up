using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Seed.WalkSeed;

/// <summary>
/// Gives a check-in walk coach its forms, its weekly check-in rule and the check-in history of its clients, built the way
/// <c>AutomationOccurrenceSender.DeliverFormAsync</c> builds a delivery. Everything the coach owned before is removed
/// first, so a re-run puts the data back as it was.
/// </summary>
internal static class WalkCheckInData
{
    /// <summary>Title of the check-in form the weekly rule sends.</summary>
    public const string WeeklyFormTitle = "Weekly check-in";

    /// <summary>Title of the second check-in form.</summary>
    public const string KneeFormTitle = "Knee rehab check-in";

    /// <summary>Title of the onboarding form.</summary>
    public const string OnboardingFormTitle = "Onboarding form";

    /// <summary>Time zone of the walk clients; the history is computed in it.</summary>
    public const string ZoneId = "Europe/Prague";

    /// <summary>How long the walk clients have been linked, in weeks; the history stays inside it.</summary>
    public const int LinkedWeeks = 8;

    private const string SmileyRating = "smiley_rating";
    private const string ShortText = "short_text";
    private const int AnswerWindowHours = 48;

    private enum CheckInState
    {
        Waiting,
        AnsweredNew,
        AnsweredReviewed,
        Missed,
    }

    private static readonly IReadOnlyDictionary<string, (int WeeksBack, CheckInState State)[]> History =
        new Dictionary<string, (int, CheckInState)[]>
        {
            ["checkins-a"] = [(0, CheckInState.Waiting), (1, CheckInState.AnsweredNew), (2, CheckInState.AnsweredReviewed)],
            ["checkins-b"] = [(1, CheckInState.AnsweredReviewed), (2, CheckInState.AnsweredReviewed)],
            ["checkins-c"] = [(1, CheckInState.Missed)],
            ["checkins-d"] = [(1, CheckInState.AnsweredNew)],
            ["checkinsonly-a"] = [(1, CheckInState.AnsweredNew)],
        };

    /// <summary>
    /// Replaces the coach's forms, rules and responses with the walk data.
    /// </summary>
    /// <param name="db">Application database context.</param>
    /// <param name="coachUserId">The coach's user id.</param>
    /// <param name="profile">The coach's profile.</param>
    /// <param name="clients">The coach's walk clients, already linked.</param>
    /// <param name="nowUtc">The current instant.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task SeedAsync(
        ApplicationDbContext db,
        Guid coachUserId,
        ProfessionalProfile profile,
        IReadOnlyList<WalkCheckInClient> clients,
        DateTime nowUtc,
        CancellationToken ct)
    {
        await RemovePreviousDataAsync(db, coachUserId, profile.Id, ct);

        var weeklyForm = NewCheckInForm(coachUserId, WeeklyFormTitle,
        [
            new QuestionnaireQuestion
            {
                OrderIndex = 0,
                Type = SmileyRating,
                Label = "How has your energy level been this week?",
                Config = "{\"lowLabel\":\"Very low\",\"highLabel\":\"Great\"}",
            },
            new QuestionnaireQuestion { OrderIndex = 1, Type = ShortText, Label = "What got in the way this week?" },
            new QuestionnaireQuestion { OrderIndex = 2, Type = ShortText, Label = "Anything you want to tell your coach?" },
        ]);

        var kneeForm = NewCheckInForm(coachUserId, KneeFormTitle,
        [
            new QuestionnaireQuestion
            {
                OrderIndex = 0,
                Type = SmileyRating,
                Label = "How did your knee feel this week?",
                Config = "{\"lowLabel\":\"Painful\",\"highLabel\":\"No pain\"}",
            },
            new QuestionnaireQuestion { OrderIndex = 1, Type = ShortText, Label = "Which exercises caused pain?" },
        ]);

        var onboardingForm = new Questionnaire
        {
            ProfessionalId = coachUserId,
            Title = OnboardingFormTitle,
            Type = QuestionnaireType.Onboarding,
            IsDefault = true,
            Questions =
            [
                new QuestionnaireQuestion { OrderIndex = 0, Type = ShortText, Label = "What is your main goal?" },
                new QuestionnaireQuestion { OrderIndex = 1, Type = ShortText, Label = "Do you have any injuries?" },
            ],
        };

        db.Questionnaires.AddRange(weeklyForm, kneeForm, onboardingForm);
        await db.SaveChangesAsync(ct);

        var rule = new AutomationRule
        {
            OwnerProfessionalProfileId = profile.Id,
            Name = WeeklyFormTitle,
            ActionType = AutomationActionType.SendForm,
            QuestionnaireId = weeklyForm.Id,
            TriggerKind = AutomationTriggerKind.Schedule,
            ScheduleInterval = 1,
            ScheduleUnit = AutomationScheduleUnit.Weeks,
            LocalTime = new TimeOnly(18, 0),
            ScheduleWeekday = DayOfWeek.Thursday,
            AnswerWindowHours = AnswerWindowHours,
            NotifyCoachOnMissedWeek = false,
            SkipWhilePreviousUnanswered = false,
            Enabled = true,
            EnabledAtUtc = nowUtc.AddDays(-7 * LinkedWeeks),
            Audience = AutomationAudience.All,
        };
        db.AutomationRules.Add(rule);
        await db.SaveChangesAsync(ct);

        await AddHistoryAsync(db, coachUserId, rule, weeklyForm, onboardingForm, clients, nowUtc, ct);
    }

    private static async Task RemovePreviousDataAsync(
        ApplicationDbContext db, Guid coachUserId, long profileId, CancellationToken ct)
    {
        // Rules first: their form and override references are restrict, and runs and overrides go with them.
        await db.AutomationRules.Where(rule => rule.OwnerProfessionalProfileId == profileId).ExecuteDeleteAsync(ct);
        await db.QuestionnaireResponses.Where(response => response.ProfessionalId == coachUserId).ExecuteDeleteAsync(ct);

        var formIds = db.Questionnaires.Where(form => form.ProfessionalId == coachUserId).Select(form => form.Id);

        await db.ClientProfessionalLinks
            .Where(link => link.QuestionnaireId != null && formIds.Contains(link.QuestionnaireId.Value))
            .ExecuteUpdateAsync(setters => setters.SetProperty(link => link.QuestionnaireId, (long?)null), ct);
        await db.PendingInvites
            .Where(invite => invite.QuestionnaireId != null && formIds.Contains(invite.QuestionnaireId.Value))
            .ExecuteUpdateAsync(setters => setters.SetProperty(invite => invite.QuestionnaireId, (long?)null), ct);
        await db.ClientRequests
            .Where(request => request.QuestionnaireId != null && formIds.Contains(request.QuestionnaireId.Value))
            .ExecuteUpdateAsync(setters => setters.SetProperty(request => request.QuestionnaireId, (long?)null), ct);

        await db.Questionnaires.Where(form => form.ProfessionalId == coachUserId).ExecuteDeleteAsync(ct);
    }

    private static Questionnaire NewCheckInForm(Guid coachUserId, string title, List<QuestionnaireQuestion> questions) =>
        new()
        {
            ProfessionalId = coachUserId,
            Title = title,
            Type = QuestionnaireType.CheckIn,
            Questions = questions,
        };

    private static async Task AddHistoryAsync(
        ApplicationDbContext db,
        Guid coachUserId,
        AutomationRule rule,
        Questionnaire weeklyForm,
        Questionnaire onboardingForm,
        IReadOnlyList<WalkCheckInClient> clients,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var zone = ClientLocalDateResolver.ResolveTimeZone(ZoneId);
        var thisMonday = AutomationScheduleCalculator.MondayOf(ClientLocalDateResolver.ResolveLocalDate(nowUtc, zone));
        var backdated = new List<(QuestionnaireResponse Response, AutomationRun? Run, DateTime At)>();

        foreach (var client in clients)
        {
            foreach (var (weeksBack, state) in History[client.Key])
            {
                var weekStart = thisMonday.AddDays(-7 * weeksBack);
                var (sentAt, dueAt) = ResolveSendTimes(weekStart, weeksBack, zone, nowUtc);

                var response = NewResponse(coachUserId, client, weeklyForm, QuestionnaireResponseKind.CheckIn);
                response.CheckInWeekStart = weekStart;
                response.CheckInDueAtUtc = dueAt;

                var run = new AutomationRun
                {
                    AutomationRuleId = rule.Id,
                    ClientProfileId = client.Profile.Id,
                    Occurrence = $"sched:{sentAt:yyyyMMddTHHmmss}",
                    Status = AutomationRunStatus.Sent,
                    SentAt = sentAt,
                };

                if (state is CheckInState.AnsweredNew or CheckInState.AnsweredReviewed)
                {
                    var submittedAt = sentAt.AddHours(20);
                    response.Status = QuestionnaireResponseStatus.Submitted;
                    response.SubmittedAt = submittedAt;
                    run.AnsweredAt = submittedAt;
                    AddCheckInAnswers(response, weeklyForm);

                    if (state == CheckInState.AnsweredReviewed)
                    {
                        response.ReviewedByCoachAtUtc = submittedAt.AddHours(4);
                    }
                }

                db.QuestionnaireResponses.Add(response);
                db.AutomationRuns.Add(run);
                backdated.Add((response, run, sentAt));
            }

            if (client.Key.EndsWith("-a", StringComparison.Ordinal))
            {
                var submittedAt = nowUtc.AddHours(-3);
                var response = NewResponse(coachUserId, client, onboardingForm, QuestionnaireResponseKind.Questionnaire);
                response.Status = QuestionnaireResponseStatus.Submitted;
                response.SubmittedAt = submittedAt;
                AddAnswers(response, onboardingForm, ["Build strength before the summer", "Left knee, healed"]);

                db.QuestionnaireResponses.Add(response);
                backdated.Add((response, null, submittedAt.AddHours(-1)));
            }
        }

        await db.SaveChangesAsync(ct);

        // The context stamps DateCreated with the current time on insert; put the sent time back, as a real send had it.
        foreach (var (response, run, at) in backdated)
        {
            response.DateCreated = at;

            foreach (var answer in response.Answers)
            {
                answer.DateCreated = response.SubmittedAt ?? at;
            }

            if (run is not null)
            {
                run.DateCreated = at;
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private static (DateTime SentAt, DateTime DueAt) ResolveSendTimes(
        DateOnly weekStart, int weeksBack, TimeZoneInfo zone, DateTime nowUtc)
    {
        var scheduled = AutomationScheduleCalculator.ToUtc(weekStart.AddDays(3), new TimeOnly(18, 0), zone);
        var windowOpen = scheduled <= nowUtc && scheduled.AddHours(AnswerWindowHours) > nowUtc;

        if (weeksBack == 0 && !windowOpen)
        {
            // Before Thursday 18:00 (or after the window): keep a waiting check-in by sending it an hour ago.
            return (TruncateToSecond(nowUtc.AddHours(-1)), nowUtc.AddHours(AnswerWindowHours));
        }

        return (scheduled, scheduled.AddHours(AnswerWindowHours));
    }

    private static QuestionnaireResponse NewResponse(
        Guid coachUserId, WalkCheckInClient client, Questionnaire form, QuestionnaireResponseKind kind) =>
        new()
        {
            Questionnaire = form,
            ClientId = client.User.Id,
            ProfessionalId = coachUserId,
            LinkId = client.Link.Id,
            Status = QuestionnaireResponseStatus.Pending,
            Kind = kind,
        };

    private static void AddCheckInAnswers(QuestionnaireResponse response, Questionnaire form) =>
        AddAnswers(response, form, ["4", "Two late shifts at work", "Feeling stronger on squats"]);

    private static void AddAnswers(QuestionnaireResponse response, Questionnaire form, string[] values)
    {
        foreach (var question in form.Questions.OrderBy(q => q.OrderIndex).Take(values.Length))
        {
            var value = values[question.OrderIndex];
            var isNumber = question.Type == SmileyRating;

            response.Answers.Add(new QuestionnaireAnswer
            {
                Question = question,
                ValueNumber = isNumber ? decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture) : null,
                ValueText = isNumber ? null : value,
            });
        }
    }

    private static DateTime TruncateToSecond(DateTime instant) =>
        new(instant.Ticks - (instant.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
}
