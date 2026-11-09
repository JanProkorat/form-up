using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.ClientTraining;
using FormUp.Application.Features.SessionExecutions.Shared;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using Microsoft.EntityFrameworkCore;
using MongoDB.Driver;

namespace FormUp.Application.Features.ClientTraining.GetTodaySession;

/// <summary>
/// Returns today's planned training session based on the client's active training plan.
/// Enriches each session in the response with its current lock state (Stable/Editing/Live)
/// and lock holder (Coach/Client/null) via a single batch <c>GetStateAsync</c> call.
/// </summary>
/// <param name="mongo">MongoDB context.</param>
/// <param name="db">Relational database context.</param>
/// <param name="lockService">Session lock service — used to batch-fetch lock state.</param>
/// <remarks>
/// <para>
/// Active-plan resolution is a two-phase read (ADR-0001 Tier 2a / #838):
/// <list type="number">
/// <item>A lightweight Mongo projection fetches every candidate Active plan with per-week
/// <b>metadata</b> only (<c>weekNumber</c>, <c>status</c>, <c>datePublished</c>) — excluding the
/// heavy <c>weeks[].sessions</c> sub-tree. This is enough for
/// <see cref="PlanWindowResolver.ResolveCurrentPlan{T}"/> and
/// <see cref="PlanWeekCalculator.ResolveCurrentWeekNumber"/>, both of which only need week
/// <em>counts</em> and metadata, never session content.</item>
/// <item>Once the current week is resolved, a second targeted Mongo query hydrates just that one
/// week's <c>sessions</c> via the positional <c>$</c> projection operator.</item>
/// </list>
/// The plan's <c>weeks</c> array itself must never be projected away entirely — doing so would
/// collapse <see cref="PlanWindowResolver"/>'s week-count selector to zero for every plan.
/// </para>
/// <para>
/// This file stays large by design: the two-phase <see cref="LightPlanProjection"/> read above,
/// with its phase-2 hydration in <see cref="FetchHydratedWeekAsync"/>, is a deliberate
/// per-endpoint perf optimisation (ADR-0001 Tier 2a / #838) and must not be flattened into the
/// generic B4 cross-store helper — that is the one a future refactorer would otherwise "fix" and
/// regress. See #938 for the full line-count accounting.
/// </para>
/// </remarks>
/// <param name="blobStorage">Blob storage service — converts each session photo's stored BlobUrl
/// into a short-lived pre-signed read URL before the response leaves the process (F9).</param>
/// <param name="timeProvider">Clock abstraction (#955) — lets tests pin the "now" instant deterministically.</param>
public class GetTodaySessionEndpoint(
    IMongoContext mongo,
    IApplicationDbContext db,
    ISessionLockService lockService,
    IBlobStorageService blobStorage,
    TimeProvider timeProvider) : EndpointWithoutRequest<GetTodaySessionResponse>
{
    /// <summary>
    /// Phase-1 projection: plan-level fields plus per-week metadata only (weekNumber, status,
    /// datePublished). Deliberately excludes <c>weeks[].sessions</c> and <c>weeks[].dayNotes</c> —
    /// the heavy content this endpoint doesn't need until the current week is resolved.
    /// </summary>
    /// <remarks>
    /// <c>internal</c> (not <c>private</c>) so Testcontainers integration tests
    /// (<c>GetTodaySessionProjectionIntegrationTests</c>) can execute this EXACT production
    /// projection against a real MongoDB instance and assert the metadata-retained /
    /// content-excluded shape directly — proving the projection itself, not a re-derived copy
    /// of it. See <c>InternalsVisibleTo("FormUp.Tests")</c> in
    /// <c>Domain/Services/ClientVerdictService.cs</c>.
    /// </remarks>
    internal static readonly ProjectionDefinition<TrainingPlan> LightPlanProjection = Builders<TrainingPlan>.Projection.Combine(
        Builders<TrainingPlan>.Projection.Include(p => p.ExternalId),
        Builders<TrainingPlan>.Projection.Include(p => p.ClientId),
        Builders<TrainingPlan>.Projection.Include(p => p.TrainerId),
        Builders<TrainingPlan>.Projection.Include(p => p.Name),
        Builders<TrainingPlan>.Projection.Include(p => p.Status),
        Builders<TrainingPlan>.Projection.Include(p => p.StartDate),
        Builders<TrainingPlan>.Projection.Include(p => p.DateCreated),
        Builders<TrainingPlan>.Projection.Include(p => p.DateCompleted),
        Builders<TrainingPlan>.Projection.Include(p => p.QuestionnaireResponseId),
        Builders<TrainingPlan>.Projection.Include("weeks.weekNumber"),
        Builders<TrainingPlan>.Projection.Include("weeks.status"),
        Builders<TrainingPlan>.Projection.Include("weeks.datePublished"));

    /// <inheritdoc />
    public override void Configure()
    {
        Get("/client/training/plan/today");
        Roles(AppRoles.Client);
        Summary(s =>
        {
            s.Summary = "Get today's training session";
            s.Description = "Returns the training session planned for today based on the active plan and week cycle.";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);

        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var clientProfile = await db.ClientProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(cp => cp.UserId == Guid.Parse(userId), ct);

        if (clientProfile is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // Canonical client id on Mongo docs is ApplicationUser.Id (#840) — TrainingPlan,
        // TrainingCompletion, and SessionLog now all key on the same value as WorkoutLog,
        // so a single variable serves every collection queried below.
        var clientId = clientProfile.UserId;

        // Resolve the client's local calendar day (#935) — every "today" comparison below (plan
        // window, current week, day-of-week, and the SessionExecution lookup date) anchors on
        // this rather than the server's UTC day.
        var todayLocalUtc = await db.ResolveClientLocalDateUtcAsync(clientId, timeProvider.GetUtcNow().UtcDateTime, ct);

        // Find the Active training plan whose date window contains today — a client may hold
        // several sequential, non-overlapping Active plans (#780).
        // Phase 1: lightweight projection — plan metadata + per-week metadata only, no session content.
        var filter = Builders<TrainingPlan>.Filter.Eq(p => p.ClientId, clientId)
                     & Builders<TrainingPlan>.Filter.Eq(p => p.Status, TrainingPlanStatus.Active);

        using var cursor = await mongo.TrainingPlans.FindAsync(
            filter,
            new FindOptions<TrainingPlan, TrainingPlan> { Projection = LightPlanProjection },
            ct);
        var activePlans = await cursor.ToListAsync(ct);
        var plan = PlanWindowResolver.ResolveCurrentPlan(activePlans, p => p.StartDate, p => p.Weeks.Count, todayLocalUtc);

        if (plan is null)
        {
            await Send.OkAsync(new GetTodaySessionResponse { HasSession = false }, ct);
            return;
        }

        // Base response: always expose plan metadata once an Active plan exists so the client
        // can preview it on the Plans screen even when there's no session for today (e.g. plan
        // not started yet, no published weeks, or today is a rest day).
        var response = new GetTodaySessionResponse
        {
            HasSession = false,
            PlanId = plan.ExternalId,
            PlanName = plan.Name,
            TotalWeeks = plan.Weeks.Count,
            Status = plan.Status.ToString(),
            QuestionnaireResponseId = plan.QuestionnaireResponseId,
            DateCompleted = plan.DateCompleted,
            RunsUntil = await this.ResolveRunsUntilForOverCapClientAsync(
                db,
                plan.TrainerId,
                clientId,
                plan.StartDate,
                plan.Weeks.Where(w => w.Status == WeekStatus.Published).Select(w => w.WeekNumber),
                ct),
        };

        if (plan.Weeks.Count == 0)
        {
            await Send.OkAsync(response, ct);
            return;
        }

        // Calculate current week based on plan publish date and cycling
        var publishedWeeks = plan.Weeks
            .Where(w => w.Status == WeekStatus.Published)
            .OrderBy(w => w.WeekNumber)
            .ToList();

        if (publishedWeeks.Count == 0)
        {
            await Send.OkAsync(response, ct);
            return;
        }

        // Calculate current week using StartDate if available, otherwise fall back to DatePublished
        var resolvedWeek = PlanWeekCalculator.ResolveCurrentWeekNumber(
            plan.StartDate,
            publishedWeeks.Select(w => w.WeekNumber).ToList(),
            plan.Weeks.Count,
            publishedWeeks.First().DatePublished,
            plan.DateCreated,
            todayLocalUtc);

        if (resolvedWeek is null)
        {
            // Plan hasn't started yet — still surface plan metadata so the client can preview it.
            await Send.OkAsync(response, ct);
            return;
        }

        int currentWeekNumber = resolvedWeek.Value;

        // Past the last published week → the trainer hasn't queued anything
        // for today. Don't clamp to `publishedWeeks.Last()`: clamping would
        // keep the last published week's day-of-week sessions visible
        // indefinitely after the published portion ends (e.g. yesterday was
        // the last day of the last published week → today must show nothing).
        if (currentWeekNumber > publishedWeeks[^1].WeekNumber)
        {
            await Send.OkAsync(response, ct);
            return;
        }

        var currentWeek = plan.Weeks.FirstOrDefault(w => w.WeekNumber == currentWeekNumber);
        if (currentWeek is null || currentWeek.Status != WeekStatus.Published)
        {
            // Gap-skip: the calculated week isn't published but earlier
            // weeks are (e.g. trainer published 1, 2, 4 and today resolves
            // to week 3). Fall back to the latest published week that's
            // not after the calculated one — this preserves the "ahead of
            // trainer is hidden, behind catches up" intent.
            currentWeek = publishedWeeks.LastOrDefault(w => w.WeekNumber <= currentWeekNumber);
            if (currentWeek is null)
            {
                await Send.OkAsync(response, ct);
                return;
            }
        }

        // Phase 2: hydrate just the resolved week's session content. currentWeek up to this
        // point only carries metadata (weekNumber/status/datePublished) from the phase-1 fetch.
        var hydratedWeek = await FetchHydratedWeekAsync(plan.ExternalId, currentWeek.WeekNumber, ct);
        if (hydratedWeek is null)
        {
            // Plan/week vanished between phase 1 and phase 2 (rare race) — surface as no session.
            await Send.OkAsync(response, ct);
            return;
        }

        currentWeek = hydratedWeek;

        // Find today's sessions (1 = Monday, 7 = Sunday) — todayLocalUtc already carries the
        // client's LOCAL calendar date (#935), so its DayOfWeek is the local day-of-week.
        var todayDow = (int)todayLocalUtc.DayOfWeek;
        todayDow = todayDow == 0 ? 7 : todayDow; // Convert Sunday from 0 to 7

        var todayDay = currentWeek.Days.FirstOrDefault(d => d.DayOfWeek == todayDow);
        var todaySessions = todayDay?.Sessions
            .OrderBy(s => s.Order)
            .ToList() ?? [];

#pragma warning disable CS0618 // Session is intentionally set for backwards compatibility
        response.Sessions = todaySessions;
        response.Session = todaySessions.Count > 0 ? todaySessions[0] : null;
        response.HasSession = todaySessions.Count > 0;
#pragma warning restore CS0618
        response.CurrentWeek = currentWeek.WeekNumber;

        // ── Batch-fetch Exercise docs for muscle-group enrichment ─────────────
        var exerciseIds = todaySessions
            .SelectMany(s => s.AllExercises)
            .Select(e => e.ExerciseExternalId)
            .Distinct()
            .ToList();

        if (exerciseIds.Count > 0)
        {
            var exerciseFilter = Builders<Exercise>.Filter.In(e => e.ExternalId, exerciseIds);
            using var exerciseCursor = await mongo.Exercises.FindAsync(
                exerciseFilter,
                cancellationToken: ct);
            var exerciseDocs = await exerciseCursor.ToListAsync(ct);

            foreach (var ex in exerciseDocs)
                response.ExerciseMuscleGroups[ex.ExternalId] = ex.MuscleGroups;
        }

        // ── Batch-fetch SessionExecution docs for today (#841) ────────────────
        // Unifies the former TrainingCompletion (lightweight Today-card checkbox toggles) and
        // WorkoutLog (live training assistant's per-set logs) sources of truth for "was exercise
        // X completed today" into one collection — a single document per (clientId, sessionId,
        // date) now carries both signals.
        if (todaySessions.Count > 0)
        {
            // targetDate anchors on the client's LOCAL calendar day (#935), matching the same key
            // every writer of SessionExecution.Date now uses (Mark* endpoints, StartWorkout,
            // WorkoutCompletionService, FinishSession) — the server's UTC day would otherwise
            // silently unmatch a completion recorded near local midnight.
            var targetDate = todayLocalUtc;
            var todaySessionIds = todaySessions.Select(s => s.SessionId).ToList();

            // Per-session accumulator so entries from both signals (checkbox flags, Performance) union cleanly.
            var completedBySession = new Dictionary<Guid, HashSet<Guid>>();

            // Per-session accumulator for the NEW per-instance completion field (#877) — keyed by
            // the raw SessionExercise.ExerciseId (instance id), NOT ExerciseExternalId. See the
            // union rule documented on GetTodaySessionResponse.CompletedExerciseInstanceIdsBySession.
            var completedInstancesBySession = new Dictionary<Guid, HashSet<Guid>>();

            // 1. Checkbox completion flags — one doc per (clientId, date, sessionId).
            var executionFilter =
                Builders<SessionExecution>.Filter.Eq(c => c.ClientId, clientId)
                & Builders<SessionExecution>.Filter.Eq(c => c.Date, targetDate)
                & Builders<SessionExecution>.Filter.In(c => c.SessionId, todaySessionIds.Cast<Guid?>());

            using var executionCursor = await mongo.SessionExecutions.FindAsync(
                executionFilter,
                cancellationToken: ct);
            var executionDocs = await executionCursor.ToListAsync(ct);

            // Build a lookup for sessions by sessionId so backfill can resolve workout membership.
            var sessionLookup = todaySessions.ToDictionary(s => s.SessionId);

            foreach (var doc in executionDocs)
            {
                var sessionId = doc.SessionId!.Value;

                if (!completedBySession.TryGetValue(sessionId, out var set))
                    completedBySession[sessionId] = set = [];

                response.VersionBySession[sessionId] = doc.Version;
                response.CompletedWorkoutIdsBySession[sessionId] =
                    (doc.CompletedWorkoutIds ?? new List<Guid>()).ToList();

                // Source 1 of the per-instance field (#877): CompletedExerciseInstanceIds already
                // holds instance ids verbatim — carry them straight through, no resolution needed.
                if (doc.CompletedExerciseInstanceIds.Count > 0)
                {
                    if (!completedInstancesBySession.TryGetValue(sessionId, out var instanceSet))
                    {
                        completedInstancesBySession[sessionId] = instanceSet = [];
                    }

                    foreach (var instanceId in doc.CompletedExerciseInstanceIds)
                    {
                        instanceSet.Add(instanceId);
                    }
                }

                // Populate the per-session completed-exercise set from the flat
                // CompletedExerciseInstanceIds list (#857 phase 3b) — reconstruct the
                // wire-compatible (ExerciseExternalId-keyed) by-workout shape by mapping each
                // completed instance back to its containing workout via the session definition.
                if (sessionLookup.TryGetValue(sessionId, out var completionSession))
                {
                    var byWorkout = new Dictionary<Guid, List<Guid>>();

                    foreach (var workout in completionSession.Workouts)
                    {
                        var completedInWorkout = workout.Exercises
                            .Where(e => doc.CompletedExerciseInstanceIds.Contains(e.ExerciseId))
                            .Select(e => e.ExerciseExternalId)
                            .ToList();

                        if (completedInWorkout.Count > 0)
                        {
                            byWorkout[workout.WorkoutId] = completedInWorkout;
                            foreach (var exId in completedInWorkout)
                                set.Add(exId);
                        }
                    }

                    response.CompletedExerciseIdsByWorkoutAndSession[sessionId] = byWorkout;

                    foreach (var exId in completionSession.StandaloneExercises
                        .Where(e => doc.CompletedExerciseInstanceIds.Contains(e.ExerciseId))
                        .Select(e => e.ExerciseExternalId))
                    {
                        set.Add(exId);
                    }
                }
            }

            // 2. Performance data — live-training logs for today. An exercise counts as
            // completed when every planned set has a CompletedAt timestamp.
            // #841: the unified partial-unique index guarantees at most ONE SessionExecution per
            // (clientId, sessionId, date) — executionDocs (already filtered to today's Date) has
            // at most one entry per session, so no further per-session dedup is needed here
            // (unlike the retired multi-WorkoutLog-per-day model this replaces).
            var executionsWithPerformanceToday = executionDocs
                .Where(e => e.Performance is not null)
                .ToList();

            foreach (var log in executionsWithPerformanceToday)
            {
                if (!completedBySession.TryGetValue(log.SessionId!.Value, out var set))
                    completedBySession[log.SessionId.Value] = set = [];

                var setsForExerciseInSession = new Dictionary<Guid, List<int>>();
                var loggedSetsForExercise = new Dictionary<Guid, List<LoggedSetDto>>();
                var sessionHasModifications = false;

                // Per-instance accumulators (#885/#938) — additive alongside the catalog-keyed ones
                // above; fanned out via the shared placement-exact resolution rule (placement-exact,
                // tied-instance, or session-wide-fallback — see
                // SessionExecutionExtensions.ResolveMatchedPlacements) rather than unconditionally
                // to every sibling sharing the catalog id.
                var completedSetsByInstance = new Dictionary<Guid, List<int>>();
                var loggedSetsByInstance = new Dictionary<Guid, List<LoggedSetDto>>();

                sessionLookup.TryGetValue(log.SessionId.Value, out var performanceSession);

                foreach (var workout in log.Performance!.Workouts)
                {
                    var workoutKey = performanceSession?.ResolveLoggedWorkoutKey(workout.WorkoutId);

                    foreach (var ex in workout.Exercises)
                    {
                        if (ex.Sets.Count == 0) continue;

                        IReadOnlyList<SessionExercise> matchedInstances = performanceSession is not null
                            ? performanceSession.ResolveMatchedPlacements(workoutKey, ex.ExerciseExternalId)
                            : [];

                        if (ex.Sets.All(s => s.CompletedAt is not null))
                        {
                            set.Add(ex.ExerciseExternalId);

                            // Source 2 of the per-instance field (#877, placement-exact since #938):
                            // Performance carries no instance id (see WorkoutExercise), so a
                            // fully-logged catalog exercise is attributed to the placement(s)
                            // resolved above — exactly one for an unambiguous placement, every tied
                            // instance when the same catalog exercise is placed twice under the same
                            // container, or a session-wide catalog fan-out only when attribution is
                            // genuinely impossible. See GetTodaySessionResponse's XML docs above.
                            if (matchedInstances.Count > 0)
                            {
                                if (!completedInstancesBySession.TryGetValue(log.SessionId.Value, out var instanceSet))
                                {
                                    completedInstancesBySession[log.SessionId.Value] = instanceSet = [];
                                }

                                foreach (var matchedInstance in matchedInstances)
                                {
                                    instanceSet.Add(matchedInstance.ExerciseId);
                                }
                            }
                        }

                        var completedSetNumbers = ex.Sets
                            .Where(s => s.CompletedAt is not null)
                            .Select(s => s.SetNumber)
                            .ToList();
                        if (completedSetNumbers.Count > 0)
                            setsForExerciseInSession[ex.ExerciseExternalId] = completedSetNumbers;

                        // Build value-bearing LoggedSetDto list for every set in this exercise.
                        var loggedSetDtos = ex.Sets.Select(s => new LoggedSetDto
                        {
                            SetNumber = s.SetNumber,
                            ActualReps = s.Reps,
                            ActualWeightKg = s.WeightKg,
                            ActualRpe = s.Rpe,
                            ActualDurationSeconds = s.DurationSeconds,
                            ActualDistanceMeters = s.DistanceMeters,
                            PlannedReps = s.PlannedReps,
                            PlannedWeightKg = s.PlannedWeightKg,
                            PlannedRpe = s.PlannedRpe,
                            PlannedDurationSeconds = s.PlannedDurationSeconds,
                            PlannedDistanceMeters = s.PlannedDistanceMeters,
                            IsModified = s.IsModified
                        }).ToList();

                        loggedSetsForExercise[ex.ExerciseExternalId] = loggedSetDtos;

                        if (loggedSetDtos.Any(s => s.IsModified))
                            sessionHasModifications = true;

                        // Per-instance attribution (#885/#938): fans out across every placement
                        // resolved above so a tied or session-wide-fallback placement never renders
                        // ticked with an empty set list — the same resolution feeds
                        // completedInstancesBySession above, so the two stay in agreement.
                        foreach (var matchedInstance in matchedInstances)
                        {
                            if (completedSetNumbers.Count > 0)
                                completedSetsByInstance[matchedInstance.ExerciseId] = completedSetNumbers;
                            loggedSetsByInstance[matchedInstance.ExerciseId] = loggedSetDtos;
                        }
                    }
                }

                if (setsForExerciseInSession.Count > 0)
                    response.CompletedSetsBySessionExercise[log.SessionId.Value] = setsForExerciseInSession;
                if (loggedSetsForExercise.Count > 0)
                    response.LoggedSetsBySessionExercise[log.SessionId.Value] = loggedSetsForExercise;
                if (sessionHasModifications)
                    response.HasModificationsBySession[log.SessionId.Value] = true;
                if (completedSetsByInstance.Count > 0)
                    response.CompletedSetsByExerciseInstanceBySession[log.SessionId.Value] = completedSetsByInstance;
                if (loggedSetsByInstance.Count > 0)
                    response.LoggedSetsByExerciseInstanceBySession[log.SessionId.Value] = loggedSetsByInstance;
            }

            foreach (var (sessionId, set) in completedBySession)
                response.CompletedExerciseIdsBySession[sessionId] = set.ToList();

            foreach (var (sessionId, instanceSet) in completedInstancesBySession)
                response.CompletedExerciseInstanceIdsBySession[sessionId] = instanceSet.ToList();

            // ── Batch-fetch lock state for today's sessions ───────────────────────
            // Single Mongo round-trip for all sessions (not one per session).
            var lockDocs = await lockService.GetStateAsync(todaySessionIds, ct);
            var lockLookup = lockDocs.ToDictionary(l => l.SessionId);

            foreach (var sessionId in todaySessionIds)
            {
                if (lockLookup.TryGetValue(sessionId, out var lockDoc))
                {
                    response.LockStateBySession[sessionId] = lockDoc.Type.ToString();
                    response.LockHolderBySession[sessionId] = lockDoc.Holder.ToString();
                }
                // Missing entry = Stable (no active lock). No entry added to the dicts.
            }

            // ── Derive per-set completion from exercise-level completion ──────────
            // When an exercise is marked complete via the Today-card checkbox
            // (TrainingCompletion) rather than the live training assistant
            // (WorkoutLog), the WorkoutLog-merge pass above never stamps individual
            // sets.  As a result CompletedSetsBySessionExercise stays empty for
            // those exercises even though the exercise/session checkbox is filled —
            // a visible inconsistency on the per-set ✓ column.
            //
            // Rule: for any exercise already present in CompletedExerciseIdsBySession
            // (sourced from either TrainingCompletion OR a fully-completed WorkoutLog),
            // ensure every planned set number for that exercise appears in the map.
            // We union with any existing list so partial-log progress is preserved
            // when the checkbox is also ticked.
            //
            // Run AFTER the WorkoutLog merge so latest-log-wins logic is undisturbed.
            foreach (var session in todaySessions)
            {
                if (!response.CompletedExerciseIdsBySession.TryGetValue(session.SessionId, out var completedExIds))
                    continue;

                if (!response.CompletedSetsBySessionExercise.TryGetValue(session.SessionId, out var sessionSetsMap))
                    response.CompletedSetsBySessionExercise[session.SessionId] = sessionSetsMap = new Dictionary<Guid, List<int>>();

                foreach (var plannedEx in session.AllExercises)
                {
                    if (!completedExIds.Contains(plannedEx.ExerciseExternalId))
                        continue;

                    var plannedSetNumbers = plannedEx.Sets.Select(s => s.SetNumber).ToList();
                    if (plannedSetNumbers.Count == 0)
                        continue;

                    if (sessionSetsMap.TryGetValue(plannedEx.ExerciseExternalId, out var existing))
                        sessionSetsMap[plannedEx.ExerciseExternalId] =
                            existing.Union(plannedSetNumbers).OrderBy(n => n).ToList();
                    else
                        sessionSetsMap[plannedEx.ExerciseExternalId] = plannedSetNumbers.OrderBy(n => n).ToList();
                }
            }

            // ── Batch-fetch SessionLog docs for today (photo gallery) ─────────────
            // One query for all of today's sessions; keyed by SessionId in the response.
            // SessionLog.ClientId = ApplicationUser.Id (#840), same as clientId here.
            var sessionLogFilter =
                Builders<SessionLog>.Filter.Eq(l => l.ClientId, clientId)
                & Builders<SessionLog>.Filter.In(l => l.SessionId, todaySessionIds)
                & Builders<SessionLog>.Filter.Eq(l => l.LogDate, targetDate);

            var sessionLogs = await mongo.SessionLogs
                .Find(sessionLogFilter)
                .ToListAsync(ct);

            foreach (var sessionLog in sessionLogs)
            {
                if (sessionLog.Photos.Count > 0)
                {
                    var sessionPhotos = sessionLog.Photos
                        .Select(p => new SessionPhotoDto
                        {
                            BlobUrl = p.BlobUrl,
                            UploadedAt = p.UploadedAt,
                            Note = p.Note
                        })
                        .ToList();

                    // A stored BlobUrl is no longer publicly fetchable — mint a short-lived
                    // DisplayUrl for each photo before it leaves the process (F9). BlobUrl itself
                    // stays the canonical, permanent identity value.
                    foreach (var sessionPhoto in sessionPhotos)
                    {
                        sessionPhoto.DisplayUrl = await blobStorage.GenerateReadUrlAsync(sessionPhoto.BlobUrl, ct)
                                                   ?? string.Empty;
                    }

                    response.PhotosBySession[sessionLog.SessionId] = sessionPhotos;
                }

                // Expose the session-level diary note so the mobile client can pre-load
                // it into its textarea. Without this, re-saving photos always sends null
                // for the note field, wiping whatever the user previously entered.
                if (!string.IsNullOrEmpty(sessionLog.Note))
                {
                    response.NotesBySession[sessionLog.SessionId] = sessionLog.Note;
                }
            }
        }

        await Send.OkAsync(response, ct);
    }

    /// <summary>
    /// Phase-2 fetch: hydrates the full session content for exactly one week of one plan, using
    /// the positional <c>$</c> projection operator so Mongo returns only the matched array
    /// element instead of the whole <c>weeks</c> tree.
    /// </summary>
    private async Task<TrainingWeek?> FetchHydratedWeekAsync(Guid planExternalId, int weekNumber, CancellationToken ct)
    {
        var weekFilter = Builders<TrainingPlan>.Filter.And(
            Builders<TrainingPlan>.Filter.Eq(p => p.ExternalId, planExternalId),
            Builders<TrainingPlan>.Filter.Eq("weeks.weekNumber", weekNumber));

        // CRITICAL: an inclusion-only projection like "weeks.$" returns ONLY `_id` and `weeks` —
        // every other field (including `externalId`) is excluded and deserializes to its C#
        // default (Guid.Empty). Without explicitly re-including ExternalId here, the defensive
        // ExternalId match below always fails against real MongoDB, silently making this method
        // return null on every call in production (#838 fresh-eyes catch — the mocked unit tests
        // never exercise real Mongo's field-inclusion semantics, so this was invisible there).
        var weekProjection = Builders<TrainingPlan>.Projection.Combine(
            Builders<TrainingPlan>.Projection.Include(p => p.ExternalId),
            Builders<TrainingPlan>.Projection.Include("weeks.$"));

        using var cursor = await mongo.TrainingPlans.FindAsync(
            weekFilter,
            new FindOptions<TrainingPlan, TrainingPlan> { Projection = weekProjection },
            ct);
        var hydratedPlans = await cursor.ToListAsync(ct);

        // Match on ExternalId explicitly rather than trusting the query to have filtered
        // server-side — this keeps the method correct even against a test double that ignores
        // the filter argument (see GetTodaySessionEndpointTests' NSubstitute-based mocks).
        return hydratedPlans
            .FirstOrDefault(p => p.ExternalId == planExternalId)?
            .Weeks
            .FirstOrDefault(w => w.WeekNumber == weekNumber);
    }
}
