using System.Globalization;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.PhotoDiaryRequests;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FormUp.Application.Infrastructure.Services;

/// <summary>
/// Sends one occurrence of an automation rule to one client: re-checks that the client is still
/// reachable, applies the rule's options, records the run and delivers the request. The caller owns
/// the transaction, so the run and the delivery commit or roll back together. Pushes and realtime
/// events wait in <paramref name="afterCommit"/> until the caller has committed.
/// </summary>
/// <param name="db">Application database context.</param>
/// <param name="notifications">Writes the in-app notification row.</param>
/// <param name="afterCommit">Holds the pushes and realtime events until the transaction commits.</param>
/// <param name="entitlementService">Resolves the coach's subscription entitlements.</param>
/// <param name="logger">Logger.</param>
public sealed class AutomationOccurrenceSender(
    IApplicationDbContext db,
    INotificationService notifications,
    AutomationAfterCommitQueue afterCommit,
    EntitlementService entitlementService,
    ILogger<AutomationOccurrenceSender> logger)
{
    /// <summary>How long a photo request an automation sends stays open.</summary>
    public const int PhotoRequestDurationDays = 7;

    private const string ScheduledKeyPrefix = "sched:";

    /// <summary>
    /// Sends the occurrence unless the client is unreachable or an option says to hold back.
    /// </summary>
    /// <param name="ruleId">Internal id of the rule.</param>
    /// <param name="clientProfileId">Internal id of the client profile.</param>
    /// <param name="occurrence">Key of the occurrence; with the rule and client it is unique.</param>
    /// <param name="planKeyPrefix">Prefix shared by every occurrence of one plan, for the once-per-plan option; null off plan events.</param>
    /// <param name="nowUtc">The current instant.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<AutomationSendOutcome> SendAsync(
        long ruleId,
        long clientProfileId,
        string occurrence,
        string? planKeyPrefix,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var evaluation = await AutomationEvaluationLoader.LoadAsync(
            db, entitlementService, ruleId, clientProfileId, nowUtc, ct);

        if (evaluation is null || !evaluation.Effective.Enabled)
        {
            return AutomationSendOutcome.NotEligible;
        }

        var alreadyRecorded = await db.AutomationRuns
            .AsNoTracking()
            .AnyAsync(r => r.AutomationRuleId == ruleId
                           && r.ClientProfileId == clientProfileId
                           && r.Occurrence == occurrence, ct);

        if (alreadyRecorded)
        {
            return AutomationSendOutcome.Duplicate;
        }

        var skipReason = await FindSkipReasonAsync(evaluation, planKeyPrefix, nowUtc, ct);

        if (skipReason is not null)
        {
            return await RecordWithoutSendingAsync(
                evaluation, occurrence, AutomationRunStatus.Skipped, skipReason, nowUtc, ct);
        }

        if (!IsActionUsable(evaluation))
        {
            logger.LogWarning(
                "AutomationScheduler: rule {RuleId} cannot send its form to client {ClientProfileId}: the form is missing, inactive, not the coach's, or a check-in form without an answer window.",
                ruleId, clientProfileId);

            return await RecordWithoutSendingAsync(
                evaluation, occurrence, AutomationRunStatus.Failed, null, nowUtc, ct);
        }

        var run = new AutomationRun
        {
            AutomationRuleId = ruleId,
            ClientProfileId = clientProfileId,
            Occurrence = occurrence,
            Status = AutomationRunStatus.Sent,
            SentAt = nowUtc,
        };
        db.AutomationRuns.Add(run);

        if (!await TrySaveAsync(ct))
        {
            return AutomationSendOutcome.Duplicate;
        }

        run.DeliveredEntityId = await DeliverAsync(evaluation, nowUtc, nowUtc, ct);
        await db.SaveChangesAsync(ct);

        return AutomationSendOutcome.Sent;
    }

    /// <summary>
    /// Records a Failed run for an occurrence whose send threw, unless it already has a run. The run
    /// waits for its first retry. Call it in a fresh scope after the send's own transaction rolled back.
    /// </summary>
    /// <param name="ruleId">Internal id of the rule.</param>
    /// <param name="clientProfileId">Internal id of the client profile.</param>
    /// <param name="occurrence">Key of the occurrence.</param>
    /// <param name="nowUtc">The current instant.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task RecordFailureAsync(
        long ruleId, long clientProfileId, string occurrence, DateTime nowUtc, CancellationToken ct)
    {
        var alreadyRecorded = await db.AutomationRuns
            .AsNoTracking()
            .AnyAsync(r => r.AutomationRuleId == ruleId
                           && r.ClientProfileId == clientProfileId
                           && r.Occurrence == occurrence, ct);

        if (alreadyRecorded)
        {
            return;
        }

        db.AutomationRuns.Add(new AutomationRun
        {
            AutomationRuleId = ruleId,
            ClientProfileId = clientProfileId,
            Occurrence = occurrence,
            Status = AutomationRunStatus.Failed,
            AttemptCount = 1,
            NextRetryAt = AutomationRetryPolicy.NextRetryAt(1, nowUtc),
        });

        await TrySaveAsync(ct);
    }

    /// <summary>
    /// Retries a Failed run in place: it becomes Sent, Skipped or a final Failed and never gets a
    /// second row. The caller claimed and locked the row and owns the transaction.
    /// </summary>
    /// <param name="run">The tracked, claimed Failed run.</param>
    /// <param name="nowUtc">The current instant.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<AutomationSendOutcome> RetryAsync(AutomationRun run, DateTime nowUtc, CancellationToken ct)
    {
        var evaluation = await AutomationEvaluationLoader.LoadAsync(
            db, entitlementService, run.AutomationRuleId, run.ClientProfileId, nowUtc, ct);

        // The rule is gone, off or not usable any more: retrying cannot help.
        if (evaluation is null || !evaluation.Effective.Enabled || !IsActionUsable(evaluation))
        {
            run.NextRetryAt = null;
            await db.SaveChangesAsync(ct);

            return AutomationSendOutcome.Failed;
        }

        var skipReason = await FindSkipReasonAsync(evaluation, PlanKeyPrefixOf(evaluation, run.Occurrence), nowUtc, ct);

        if (skipReason is not null)
        {
            run.Status = AutomationRunStatus.Skipped;
            run.SkipReason = skipReason;
            run.NextRetryAt = null;
            await db.SaveChangesAsync(ct);

            return AutomationSendOutcome.Skipped;
        }

        run.DeliveredEntityId = await DeliverAsync(evaluation, nowUtc, WeekAnchorOf(run.Occurrence, nowUtc), ct);
        run.Status = AutomationRunStatus.Sent;
        run.SentAt = nowUtc;
        run.NextRetryAt = null;
        await db.SaveChangesAsync(ct);

        return AutomationSendOutcome.Sent;
    }

    /// <summary>
    /// The once-per-plan key prefix of a plan-event occurrence key
    /// (<c>{EventType}:{PlanId:N}:{yyyyMMdd}</c>, up to and including the last colon); null for every
    /// other kind of occurrence.
    /// </summary>
    private static string? PlanKeyPrefixOf(AutomationEvaluation evaluation, string occurrence)
    {
        if (!evaluation.Effective.IsPlanEvent)
        {
            return null;
        }

        var lastColon = occurrence.LastIndexOf(':');

        return lastColon < 0 ? null : occurrence[..(lastColon + 1)];
    }

    /// <summary>
    /// The instant a retried check-in takes its week from: the scheduled instant in a
    /// <c>sched:yyyyMMddTHHmmss</c> key, so a late retry stays in the week it was due for.
    /// </summary>
    private static DateTime WeekAnchorOf(string occurrence, DateTime nowUtc) =>
        occurrence.StartsWith(ScheduledKeyPrefix, StringComparison.Ordinal)
        && DateTime.TryParseExact(
            occurrence[ScheduledKeyPrefix.Length..],
            "yyyyMMdd'T'HHmmss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var scheduledAt)
            ? DateTime.SpecifyKind(scheduledAt, DateTimeKind.Utc)
            : nowUtc;

    private async Task<AutomationSkipReason?> FindSkipReasonAsync(
        AutomationEvaluation evaluation, string? planKeyPrefix, DateTime nowUtc, CancellationToken ct)
    {
        var effective = evaluation.Effective;
        var ruleId = evaluation.Rule.Id;
        var clientProfileId = evaluation.Client.ClientProfileId;

        if (effective.IsPaused)
        {
            return AutomationSkipReason.Paused;
        }

        if (effective.StopAfterSends is { } limit)
        {
            var sentCount = await db.AutomationRuns
                .AsNoTracking()
                .CountAsync(r => r.AutomationRuleId == ruleId
                                 && r.ClientProfileId == clientProfileId
                                 && r.Status == AutomationRunStatus.Sent, ct);

            if (sentCount >= limit)
            {
                return AutomationSkipReason.StopAfterReached;
            }
        }

        if (effective.OncePerPlan && planKeyPrefix is not null)
        {
            var sentForPlan = await db.AutomationRuns
                .AsNoTracking()
                .AnyAsync(r => r.AutomationRuleId == ruleId
                               && r.ClientProfileId == clientProfileId
                               && r.Status == AutomationRunStatus.Sent
                               && r.Occurrence.StartsWith(planKeyPrefix), ct);

            if (sentForPlan)
            {
                return AutomationSkipReason.AlreadySentForPlan;
            }
        }

        // A check-in rule never skips: each week stands on its own and an unanswered one counts as missed.
        if (effective.SkipWhilePreviousUnanswered
            && !IsCheckInRule(evaluation)
            && await IsPreviousUnansweredAsync(evaluation, nowUtc, ct))
        {
            return AutomationSkipReason.PreviousUnanswered;
        }

        if (evaluation.Rule.ActionType == AutomationActionType.RequestProgressPhotos
            && await HasOpenPhotoRequestAsync(evaluation, nowUtc, ct))
        {
            return AutomationSkipReason.PreviousUnanswered;
        }

        return null;
    }

    private async Task<bool> IsPreviousUnansweredAsync(AutomationEvaluation evaluation, DateTime nowUtc, CancellationToken ct)
    {
        var ruleId = evaluation.Rule.Id;
        var clientProfileId = evaluation.Client.ClientProfileId;

        var previous = await db.AutomationRuns
            .AsNoTracking()
            .Where(r => r.AutomationRuleId == ruleId
                        && r.ClientProfileId == clientProfileId
                        && r.Status == AutomationRunStatus.Sent
                        && r.AnsweredAt == null)
            .OrderByDescending(r => r.SentAt)
            .Select(r => new { r.DeliveredEntityId, r.SentAt })
            .FirstOrDefaultAsync(ct);

        return previous is not null && await AutomationDeliveryTracker.IsOpenAsync(
            db, evaluation.Rule.ActionType, previous.DeliveredEntityId, previous.SentAt, clientProfileId, nowUtc, ct);
    }

    private async Task<bool> HasOpenPhotoRequestAsync(AutomationEvaluation evaluation, DateTime nowUtc, CancellationToken ct)
    {
        var coachUserId = evaluation.Coach.UserId;
        var linkId = evaluation.Client.PrimaryLinkId;

        // Same lock as CreateRequestEndpoint, so a manual and an automated request cannot both
        // pass the open-request check.
        await db.LockProfessionalProfileAsync(evaluation.Rule.OwnerProfessionalProfileId, ct);

        var candidates = await db.PhotoDiaryRequests
            .AsNoTracking()
            .Where(r => r.ProfessionalId == coachUserId
                        && r.LinkId == linkId
                        && (r.Status == PhotoDiaryStatus.Pending
                            || r.Status == PhotoDiaryStatus.Accepted
                            || r.Status == PhotoDiaryStatus.InProgress))
            .ToListAsync(ct);

        var now = new DateTimeOffset(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc));

        return candidates.Any(r => r.IsOpenAt(now));
    }

    private static bool IsCheckInRule(AutomationEvaluation evaluation) =>
        evaluation.Rule.ActionType == AutomationActionType.SendForm
        && evaluation.EffectiveQuestionnaire is { } questionnaire
        && AutomationQuestionnaireUsability.IsCheckInForm(questionnaire.Type);

    private static bool IsActionUsable(AutomationEvaluation evaluation)
    {
        if (evaluation.Rule.ActionType != AutomationActionType.SendForm)
        {
            return true;
        }

        var questionnaire = evaluation.EffectiveQuestionnaire;

        if (questionnaire is null
            || !AutomationQuestionnaireUsability.IsUsable(questionnaire.IsActive)
            || questionnaire.ProfessionalId != evaluation.Coach.UserId)
        {
            return false;
        }

        // A check-in form needs the answer window, or its week could never be judged missed.
        return !IsCheckInRule(evaluation) || evaluation.Effective.AnswerWindowHours is not null;
    }

    private async Task<AutomationSendOutcome> RecordWithoutSendingAsync(
        AutomationEvaluation evaluation,
        string occurrence,
        AutomationRunStatus status,
        AutomationSkipReason? skipReason,
        DateTime nowUtc,
        CancellationToken ct)
    {
        db.AutomationRuns.Add(new AutomationRun
        {
            AutomationRuleId = evaluation.Rule.Id,
            ClientProfileId = evaluation.Client.ClientProfileId,
            Occurrence = occurrence,
            Status = status,
            SkipReason = skipReason,
        });

        if (!await TrySaveAsync(ct))
        {
            return AutomationSendOutcome.Duplicate;
        }

        logger.LogDebug(
            "AutomationScheduler: rule {RuleId} client {ClientProfileId} occurrence {Occurrence} recorded as {Status} ({SkipReason}) at {Now:u}.",
            evaluation.Rule.Id, evaluation.Client.ClientProfileId, occurrence, status, skipReason, nowUtc);

        return status == AutomationRunStatus.Skipped ? AutomationSendOutcome.Skipped : AutomationSendOutcome.Failed;
    }

    private async Task<bool> TrySaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (AutomationDbErrors.IsUniqueViolation(ex))
        {
            logger.LogWarning("AutomationScheduler: another worker already recorded this occurrence; nothing sent.");
            return false;
        }
    }

    private async Task<Guid> DeliverAsync(
        AutomationEvaluation evaluation, DateTime nowUtc, DateTime weekAnchorUtc, CancellationToken ct)
    {
        return evaluation.Rule.ActionType switch
        {
            AutomationActionType.SendForm => await DeliverFormAsync(evaluation, nowUtc, weekAnchorUtc, ct),
            AutomationActionType.RequestMeasurements => await DeliverMeasurementRequestAsync(evaluation, ct),
            AutomationActionType.RequestProgressPhotos => await DeliverPhotoRequestAsync(evaluation, nowUtc, ct),
            _ => throw new InvalidOperationException($"Unsupported automation action {evaluation.Rule.ActionType}."),
        };
    }

    private async Task<Guid> DeliverFormAsync(
        AutomationEvaluation evaluation, DateTime nowUtc, DateTime weekAnchorUtc, CancellationToken ct)
    {
        var questionnaire = evaluation.EffectiveQuestionnaire!;
        var clientUserId = evaluation.Client.ClientUserId;
        var isCheckIn = IsCheckInRule(evaluation);

        // The pending response is the whole delivery: the link's own questionnaire stays as it is.
        var response = new QuestionnaireResponse
        {
            QuestionnaireId = questionnaire.Id,
            ClientId = clientUserId,
            ProfessionalId = evaluation.Coach.UserId,
            LinkId = evaluation.Client.PrimaryLinkId,
            Status = QuestionnaireResponseStatus.Pending,
            Kind = isCheckIn ? QuestionnaireResponseKind.CheckIn : QuestionnaireResponseKind.Questionnaire,
        };

        if (isCheckIn)
        {
            var localSendDate = ClientLocalDateResolver.ResolveLocalDate(weekAnchorUtc, evaluation.Zone);
            response.CheckInWeekStart = AutomationScheduleCalculator.MondayOf(localSendDate);
            response.CheckInDueAtUtc = nowUtc.AddHours(evaluation.Effective.AnswerWindowHours!.Value);
        }

        db.QuestionnaireResponses.Add(response);
        await db.SaveChangesAsync(ct);

        var parameters = new Dictionary<string, string> { ["questionnaireTitle"] = questionnaire.Title };

        await notifications.CreateWithoutPushAsync(clientUserId, NotificationType.QuestionnaireAssigned, parameters, ct: ct);

        afterCommit.EnqueuePush(clientUserId, NotificationType.QuestionnaireAssigned, parameters, null);
        afterCommit.EnqueueBroadcast(
            clientUserId,
            "questionnaireassigned",
            new { QuestionnairePublicId = questionnaire.PublicId, questionnaire.Title });

        return response.PublicId;
    }

    private async Task<Guid> DeliverMeasurementRequestAsync(AutomationEvaluation evaluation, CancellationToken ct)
    {
        var clientUserId = evaluation.Client.ClientUserId;
        var message = evaluation.Effective.Message;
        var parameters = new Dictionary<string, string> { ["professionalName"] = evaluation.CoachName };

        if (!string.IsNullOrWhiteSpace(message))
        {
            parameters["message"] = message;
        }

        var variant = parameters.ContainsKey("message") ? NotificationTemplates.MeasurementRequestedWithMessage : null;

        var notification = await notifications.CreateWithoutPushAsync(
            clientUserId, NotificationType.MeasurementRequested, parameters, variant, ct);

        afterCommit.EnqueuePush(clientUserId, NotificationType.MeasurementRequested, parameters, variant);
        afterCommit.EnqueueBroadcast(
            clientUserId,
            "newnotification",
            new
            {
                id = notification.Id,
                type = NotificationType.MeasurementRequested.ToString(),
                data = notification.Data,
            });

        return notification.PublicId;
    }

    private async Task<Guid> DeliverPhotoRequestAsync(AutomationEvaluation evaluation, DateTime nowUtc, CancellationToken ct)
    {
        var now = new DateTimeOffset(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc));
        var message = string.IsNullOrWhiteSpace(evaluation.Effective.Message) ? null : evaluation.Effective.Message;

        var request = new PhotoDiaryRequest
        {
            Id = Guid.NewGuid(),
            ProfessionalId = evaluation.Coach.UserId,
            LinkId = evaluation.Client.PrimaryLinkId,
            Mode = PhotoDiaryMode.Bulk,
            DurationDays = PhotoRequestDurationDays,
            Message = message,
            Status = PhotoDiaryStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.PhotoDiaryRequests.Add(request);
        await db.SaveChangesAsync(ct);

        afterCommit.EnqueueBroadcast(
            evaluation.Client.ClientUserId,
            "photodiaryrequested",
            new PhotoDiaryRequestedEvent
            {
                RequestId = request.Id,
                ProfessionalName = evaluation.CoachName,
                ProfessionalRole = evaluation.Client.PrimaryLinkRole.ToString(),
                DurationDays = request.DurationDays,
                Mode = request.Mode?.ToString(),
                Message = request.Message,
                PlanId = request.PlanId,
                CreatedAt = request.CreatedAt,
            });

        return request.Id;
    }
}
