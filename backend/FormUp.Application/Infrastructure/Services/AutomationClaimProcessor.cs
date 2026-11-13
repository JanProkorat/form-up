using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FormUp.Application.Infrastructure.Services;

/// <summary>
/// Claims one due <see cref="AutomationSchedule"/> row, sends what it stands for and moves it to its
/// next run, all in one transaction that is committed per row. Two workers never run the same row:
/// the claim skips rows another worker holds, and the unique (rule, client, occurrence) index
/// backs that up.
/// </summary>
/// <param name="db">Application database context.</param>
/// <param name="mongo">MongoDB context, read for plan windows.</param>
/// <param name="sender">Sends one occurrence.</param>
/// <param name="entitlementService">Resolves the coach's subscription entitlements.</param>
/// <param name="logger">Logger.</param>
public sealed class AutomationClaimProcessor(
    IApplicationDbContext db,
    IMongoContext mongo,
    AutomationOccurrenceSender sender,
    EntitlementService entitlementService,
    ILogger<AutomationClaimProcessor> logger)
{
    private enum RunMode
    {
        Send,
        RecordFailure,
        AdvanceOnly,
    }

    private sealed record DueOccurrence(string Key, string? PlanKeyPrefix);

    /// <summary>
    /// Claims and runs the earliest due row.
    /// </summary>
    /// <param name="nowUtc">The current instant.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<AutomationClaimResult> ProcessNextAsync(DateTime nowUtc, CancellationToken ct)
    {
        await using var transaction = await db.BeginTransactionAsync(ct);

        var schedule = await db.ClaimDueAutomationScheduleAsync(nowUtc, ct);

        if (schedule is null)
        {
            return new AutomationClaimResult(AutomationClaimState.Idle, 0);
        }

        var scheduleId = schedule.Id;

        try
        {
            if (!await RunAsync(schedule, nowUtc, RunMode.Send, ct))
            {
                return new AutomationClaimResult(AutomationClaimState.Duplicate, scheduleId);
            }

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            return new AutomationClaimResult(AutomationClaimState.Done, scheduleId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "AutomationScheduler: running schedule {ScheduleId} failed; rolling it back.", scheduleId);

            return new AutomationClaimResult(AutomationClaimState.Failed, scheduleId);
        }
    }

    /// <summary>
    /// Moves a row whose run rolled back on to its next run so it is not claimed again at once. Call
    /// it in a fresh scope.
    /// </summary>
    /// <param name="scheduleId">Internal id of the row.</param>
    /// <param name="recordFailure">Whether the occurrences are recorded as Failed (the run threw) or left as they are (a duplicate).</param>
    /// <param name="nowUtc">The current instant.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task RecoverAsync(long scheduleId, bool recordFailure, DateTime nowUtc, CancellationToken ct)
    {
        await using var transaction = await db.BeginTransactionAsync(ct);

        var schedule = await db.AutomationSchedules.FirstOrDefaultAsync(s => s.Id == scheduleId, ct);

        if (schedule?.NextRunAt is not { } due || due > nowUtc)
        {
            return;
        }

        await RunAsync(schedule, nowUtc, recordFailure ? RunMode.RecordFailure : RunMode.AdvanceOnly, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private async Task<bool> RunAsync(AutomationSchedule schedule, DateTime nowUtc, RunMode mode, CancellationToken ct)
    {
        var evaluation = await AutomationEvaluationLoader.LoadAsync(
            db, entitlementService, schedule.AutomationRuleId, schedule.ClientProfileId, nowUtc, ct);

        if (evaluation is null || !evaluation.Effective.Enabled)
        {
            schedule.NextRunAt = null;
            return true;
        }

        var due = schedule.NextRunAt!.Value;
        var windows = evaluation.Effective.IsPlanEvent
            ? await AutomationEvaluationLoader.LoadPlanWindowsAsync(mongo, evaluation, ct)
            : [];

        var occurrences = ResolveOccurrences(evaluation, windows, due);

        foreach (var occurrence in occurrences)
        {
            switch (mode)
            {
                case RunMode.Send:
                    var outcome = await sender.SendAsync(
                        evaluation.Rule.Id,
                        evaluation.Client.ClientProfileId,
                        occurrence.Key,
                        occurrence.PlanKeyPrefix,
                        nowUtc,
                        ct);

                    if (outcome == AutomationSendOutcome.Duplicate)
                    {
                        return false;
                    }

                    break;

                case RunMode.RecordFailure:
                    await sender.RecordFailureAsync(
                        evaluation.Rule.Id, evaluation.Client.ClientProfileId, occurrence.Key, ct);
                    break;
            }
        }

        await AdvanceAsync(schedule, evaluation, windows, nowUtc, ct);

        return true;
    }

    /// <summary>
    /// The occurrences the stored run stands for. Empty when the stored instant is no longer an
    /// occurrence of the rule (it was edited, or the plan changed since), in which case nothing is sent.
    /// </summary>
    private static List<DueOccurrence> ResolveOccurrences(
        AutomationEvaluation evaluation, IReadOnlyList<AutomationPlanWindow> windows, DateTime due)
    {
        var effective = evaluation.Effective;

        // A paused rule has no computable next send, so its stored occurrence is taken as it is.
        if (!effective.IsPaused)
        {
            var recomputed = AutomationNextSendCalculator.Compute(
                effective, evaluation.Zone, evaluation.StartLocalDate, windows, due.AddSeconds(-1), evaluation.FirstSendNotBeforeUtc);

            if (recomputed is not { } expected || Math.Abs((expected - due).TotalSeconds) >= 1)
            {
                return [];
            }
        }

        if (effective.TriggerKind == AutomationTriggerKind.Schedule)
        {
            return [new DueOccurrence($"sched:{due:yyyyMMddTHHmmss}", null)];
        }

        return AutomationPlanEventCalculator.ComputeDueSends(
                effective.EventType,
                effective.EventOffsetDays,
                effective.EventOffsetDirection,
                effective.LocalTime,
                evaluation.Zone,
                windows,
                due.AddSeconds(-1),
                due.AddSeconds(1))
            .Select(send => new DueOccurrence(
                $"{effective.EventType}:{send.PlanId:N}:{send.Anchor:yyyyMMdd}",
                $"{effective.EventType}:{send.PlanId:N}:"))
            .ToList();
    }

    private async Task AdvanceAsync(
        AutomationSchedule schedule,
        AutomationEvaluation evaluation,
        IReadOnlyList<AutomationPlanWindow> windows,
        DateTime nowUtc,
        CancellationToken ct)
    {
        var effective = evaluation.Effective;

        var next = AutomationNextSendCalculator.Compute(
            effective, evaluation.Zone, evaluation.StartLocalDate, windows, nowUtc, evaluation.FirstSendNotBeforeUtc);

        if (next is not null && effective.StopAfterSends is { } limit)
        {
            var ruleId = evaluation.Rule.Id;
            var clientProfileId = evaluation.Client.ClientProfileId;

            var sentCount = await db.AutomationRuns
                .AsNoTracking()
                .CountAsync(r => r.AutomationRuleId == ruleId
                                 && r.ClientProfileId == clientProfileId
                                 && r.Status == AutomationRunStatus.Sent, ct);

            if (sentCount >= limit)
            {
                next = null;
            }
        }

        schedule.NextRunAt = next;
        schedule.LocalTime = effective.LocalTime ?? AutomationPlanEventCalculator.DefaultLocalTime;
        schedule.TimeZoneId = evaluation.Zone.Id;
    }
}
