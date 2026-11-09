using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Features.ClientTraining;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using Microsoft.EntityFrameworkCore;
using MongoDB.Driver;

namespace FormUp.Application.Features.ClientTraining.GetFullPlan;

/// <summary>
/// Returns the full structure of a specific training plan for the authenticated client.
/// Enriches each exercise with muscle-group data (batch-fetched from the Exercise collection)
/// and per-set completion state (derived from <see cref="SessionExecution"/> documents: the
/// Performance side is written by the live-workout assistant, the completion flags by the
/// lightweight mark-complete toggles on the Today card).
/// Also enriches each session DTO with its current lock state (Stable/Editing/Live)
/// and holder (Coach/Client/null) via a single batch <c>GetStateAsync</c> call.
/// </summary>
/// <remarks>
/// This file grew rather than shrank under #938: <c>completedInstanceIdsBySession</c> now calls
/// <see cref="Domain.Extensions.SessionExecutionExtensions.ResolveCompletedInstanceIds"/>, which
/// needs the owning <see cref="Domain.Documents.TrainingSession"/> to resolve placements — the old
/// raw-id passthrough needed no session context at all. See #938 for the full accounting.
/// </remarks>
/// <param name="mongo">MongoDB context.</param>
/// <param name="db">Relational database context.</param>
/// <param name="lockService">Session lock service — used to batch-fetch lock state.</param>
/// <param name="timeProvider">Clock abstraction (#955) — lets tests pin the "now" instant deterministically.</param>
public class GetFullTrainingPlanEndpoint(IMongoContext mongo, IApplicationDbContext db, ISessionLockService lockService, TimeProvider timeProvider)
    : EndpointWithoutRequest<GetFullTrainingPlanResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/client/training/plans/{planId:guid}");
        Roles(AppRoles.Client);
        Summary(s =>
        {
            s.Summary = "Get full training plan";
            s.Description =
                "Returns all published weeks of the specified training plan enriched with " +
                "muscle-group data and per-set completion state derived from workout logs.";
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

        // ── 1. Resolve ClientProfile ─────────────────────────────────────────────
        var clientProfile = await db.ClientProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(cp => cp.UserId == Guid.Parse(userId), ct);

        if (clientProfile is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // Canonical client id on Mongo docs is ApplicationUser.Id (#840) — WorkoutLog,
        // TrainingPlan, and TrainingCompletion all key on the same value now, so a
        // single variable serves every collection queried below.
        var clientId = clientProfile.UserId;
        var planId = Route<Guid>("planId");

        // Resolve the client's local calendar day (#935) — anchors the current-week
        // resolution below on the client's local "today" rather than the server's UTC day.
        var todayLocalUtc = await db.ResolveClientLocalDateUtcAsync(clientId, timeProvider.GetUtcNow().UtcDateTime, ct);

        // ── 2. Fetch training plan (ownership check baked into filter) ────────────
        // Filtering on both ExternalId and ClientId means a plan belonging to
        // another client simply returns null — same response as "not found",
        // which avoids leaking existence to non-owners.
        var planFilter = Builders<TrainingPlan>.Filter.And(
            Builders<TrainingPlan>.Filter.Eq(p => p.ExternalId, planId),
            Builders<TrainingPlan>.Filter.Eq(p => p.ClientId, clientId));

        using var planCursor = await mongo.TrainingPlans.FindAsync(planFilter, cancellationToken: ct);
        var plan = await planCursor.FirstOrDefaultAsync(ct);

        if (plan is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // ── 3. Batch-fetch Exercise docs for muscle-group enrichment ──────────────
        var exerciseIds = plan.Weeks
            .SelectMany(w => w.Days)
            .SelectMany(d => d.Sessions)
            .SelectMany(s => s.AllExercises)
            .Select(e => e.ExerciseExternalId)
            .Distinct()
            .ToList();

        Dictionary<Guid, List<MuscleGroup>> muscleGroupMap = new();

        if (exerciseIds.Count > 0)
        {
            var exerciseFilter = Builders<Exercise>.Filter.In(e => e.ExternalId, exerciseIds);
            var exerciseDocs = await mongo.Exercises
                .Find(exerciseFilter)
                .Project(e => new { e.ExternalId, e.MuscleGroups })
                .ToListAsync(ct);

            foreach (var ex in exerciseDocs)
                muscleGroupMap[ex.ExternalId] = ex.MuscleGroups;
        }

        // ── 4. Fetch all SessionExecution docs for this client + plan's sessions (#841) ──
        // Walk them once to build a lookup keyed by (sessionId, exerciseExternalId, setNumber).
        // A set is "completed" when it has a CompletedAt value in WorkoutSet (Performance data),
        // OR when the checkbox completion flags mark the exercise/set done (folded in below).
        // We prefer the earliest non-null CompletedAt per set if multiple executions exist for
        // the same session.
        var planSessionIds = plan.Weeks
            .SelectMany(w => w.Days)
            .SelectMany(d => d.Sessions)
            .Select(s => s.SessionId)
            .ToList();

        // Session lookup by SessionId — feeds the canonical placement-exact completion rule
        // (SessionExecutionExtensions.ResolveCompletedInstanceIds) below (#938).
        var sessionLookup = plan.Weeks
            .SelectMany(w => w.Days)
            .SelectMany(d => d.Sessions)
            .ToDictionary(s => s.SessionId);

        var executionFilter = Builders<SessionExecution>.Filter.And(
            Builders<SessionExecution>.Filter.Eq(l => l.ClientId, clientId),
            Builders<SessionExecution>.Filter.In(l => l.SessionId, planSessionIds.Cast<Guid?>()));

        var executions = await mongo.SessionExecutions
            .Find(executionFilter)
            .ToListAsync(ct);

        var executionsWithPerformance = executions.Where(e => e.Performance is not null).ToList();

        // Every placement of every exercise across the plan's sessions, paired with the WorkoutId
        // of its containing TrainingWorkout (null for a standalone placement). The live-log write
        // path (UpdateWorkoutEndpoint / FinishSessionEndpoint) carries a WorkoutId per logged
        // workout but no per-instance exercise id, so WorkoutId is the only signal available to
        // disambiguate two placements of the same catalog exercise within one session — one
        // standalone and one nested, or nested in two different workouts (#885). Backs two
        // needs below: (a) recognizing whether a Performance-side LoggedWorkout.WorkoutId
        // corresponds to a real nested workout in a given session (vs. a fallback id
        // UpdateWorkoutEndpoint's legacy single-workout path assigns when the client sends no
        // WorkoutId — the shape a standalone exercise's log takes), and (b) resolving a specific
        // checkbox-completed exercise INSTANCE back to its containing workout.
        var placementsBySession = plan.Weeks
            .SelectMany(w => w.Days)
            .SelectMany(d => d.Sessions)
            .ToDictionary(
                s => s.SessionId,
                s => s.Workouts
                    .SelectMany(w => w.Exercises.Select(e => (Instance: e, WorkoutId: (Guid?)w.WorkoutId)))
                    .Concat(s.StandaloneExercises.Select(e => (Instance: e, WorkoutId: (Guid?)null)))
                    .ToList());

        var nestedWorkoutIdsBySession = placementsBySession.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Where(p => p.WorkoutId.HasValue).Select(p => p.WorkoutId!.Value).ToHashSet());

        // GroupBy+First (not a plain ToDictionary) defensively collapses a duplicate
        // ExerciseId — e.g. legacy/test data that leaves it at its Guid.Empty default on more
        // than one placement — to a first-occurrence-wins read rather than throwing.
        var instancesBySession = placementsBySession.ToDictionary(
            kv => kv.Key,
            kv => kv.Value
                .GroupBy(p => p.Instance.ExerciseId)
                .ToDictionary(g => g.Key, g => g.First()));

        // Resolves a Performance-side LoggedWorkout.WorkoutId to the key used below: the real
        // WorkoutId when it matches a nested TrainingWorkout in this session, else null (treated
        // as a standalone placement — see remarks above).
        Guid? ResolveWorkoutKey(Guid sessionId, Guid loggedWorkoutId) =>
            nestedWorkoutIdsBySession.TryGetValue(sessionId, out var nestedIds) && nestedIds.Contains(loggedWorkoutId)
                ? loggedWorkoutId
                : null;

        // Key: (sessionId, workoutId, exerciseExternalId, setNumber) → completedAt. workoutId
        // disambiguates two placements of the same catalog exercise within one session (#885) —
        // null means a standalone placement. If a session was logged more than once we take the
        // earliest non-null completedAt per set so accidental duplicate logs don't wipe
        // completion state.
        var completedSets = new Dictionary<(Guid sessionId, Guid? workoutId, Guid exerciseId, int setNumber), DateTime>();

        // Extended lookup: (sessionId, workoutId, exerciseId, setNumber) → WorkoutSet for
        // actual+planned values. We prefer the most-recently-updated execution per session
        // (mirrors the dedup logic used elsewhere). If two executions for the same session have
        // the set, the "best" one wins.
        var loggedSets = new Dictionary<(Guid sessionId, Guid? workoutId, Guid exerciseId, int setNumber), WorkoutSet>();

        // Deduplicate executions per sessionId: prefer most-recently-updated FINALISED, else most-recent.
        var bestLogBySession = executionsWithPerformance
            .Where(l => l.SessionId.HasValue)
            .GroupBy(l => l.SessionId!.Value)
            .Select(g =>
            {
                var finalised = g
                    .Where(l => l.Status == SessionExecutionStatus.Completed)
                    .OrderByDescending(l => l.DateUpdated ?? l.DateCreated)
                    .FirstOrDefault();
                return finalised ?? g.OrderByDescending(l => l.DateUpdated ?? l.DateCreated).First();
            })
            .ToList();

        foreach (var log in bestLogBySession)
        {
            var sessionId = log.SessionId!.Value;
            foreach (var workout in log.Performance!.Workouts)
            {
                var workoutKey = ResolveWorkoutKey(sessionId, workout.WorkoutId);
                foreach (var ex in workout.Exercises)
                {
                    foreach (var set in ex.Sets)
                    {
                        var key = (sessionId, workoutKey, ex.ExerciseExternalId, set.SetNumber);
                        loggedSets[key] = set;
                    }
                }
            }
        }

        foreach (var log in executionsWithPerformance)
        {
            if (log.SessionId is null) continue;
            var sessionId = log.SessionId.Value;

            foreach (var workout in log.Performance!.Workouts)
            {
                var workoutKey = ResolveWorkoutKey(sessionId, workout.WorkoutId);
                foreach (var ex in workout.Exercises)
                {
                    foreach (var set in ex.Sets)
                    {
                        if (set.CompletedAt is null) continue;

                        var key = (sessionId, workoutKey, ex.ExerciseExternalId, set.SetNumber);
                        if (!completedSets.ContainsKey(key) || set.CompletedAt < completedSets[key])
                            completedSets[key] = set.CompletedAt.Value;
                    }
                }
            }
        }

        // ── 4b. Fold in checkbox completion flags ─────────────────────────────────
        // The lightweight Today-card checkboxes (mark-exercise-complete / mark-session-complete)
        // write completion flags on the SAME SessionExecution document (#841). Merge those into
        // the same completedSets lookup so the plan-detail view reflects both surfaces.
        //
        // SessionId is globally unique within a plan, so we can match by sessionId
        // alone and skip the Date → WeekNumber mapping.

        var completedSectionIdsBySession = executions
            .Where(e => e.SessionId.HasValue)
            .GroupBy(e => e.SessionId!.Value)
            .ToDictionary(
                g => g.Key,
                g => g.SelectMany(e => e.CompletedWorkoutIds ?? new List<Guid>()).ToHashSet());

        // Per-instance completion lookup, via the canonical placement-exact rule (#938):
        // SessionExecutionExtensions.ResolveCompletedInstanceIds unions the raw checkbox path
        // (CompletedExerciseInstanceIds, already instance-precise) with Performance-derived
        // completion attributed placement-exact / tied-instance / session-wide-fallback — no
        // longer a separate, endpoint-local reimplementation of that rule. Drives
        // BuildExerciseDto's instance-resolved IsCompleted below.
        var completedInstanceIdsBySession = executions
            .Where(e => e.SessionId.HasValue && sessionLookup.ContainsKey(e.SessionId!.Value))
            .GroupBy(e => e.SessionId!.Value)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var session = sessionLookup[g.Key];
                    var completedInstanceIds = new HashSet<Guid>();

                    foreach (var execution in g)
                    {
                        completedInstanceIds.UnionWith(execution.ResolveCompletedInstanceIds(session));
                    }

                    return completedInstanceIds;
                });

        foreach (var execution in executions.Where(e => e.SessionId.HasValue))
        {
            var sessionId = execution.SessionId!.Value;

            if (!instancesBySession.TryGetValue(sessionId, out var instanceLookup))
                continue;

            var stampedAt = execution.DateUpdated ?? execution.DateCreated;

            // Fully-completed exercises: mark every planned set of the SPECIFIC completed
            // instance as complete. CompletedExerciseInstanceIds already identifies the exact
            // placement (#857 phase 3b) — resolve it directly via instanceLookup instead of
            // fanning out to every placement sharing the catalog id, which used to collapse a
            // standalone-vs-nested completion onto the same key (#885).
            foreach (var instanceId in execution.CompletedExerciseInstanceIds)
            {
                if (!instanceLookup.TryGetValue(instanceId, out var placement))
                    continue;

                foreach (var set in placement.Instance.Sets)
                {
                    var key = (sessionId, placement.WorkoutId, placement.Instance.ExerciseExternalId, set.SetNumber);
                    if (!completedSets.ContainsKey(key) || stampedAt < completedSets[key])
                        completedSets[key] = stampedAt;
                }
            }
        }

        // ── 4c. Batch-fetch lock state for all plan sessions ─────────────────────
        // Single Mongo round-trip — not one per session.
        Dictionary<Guid, SessionLock> lockLookup = new();
        if (planSessionIds.Count > 0)
        {
            var lockDocs = await lockService.GetStateAsync(planSessionIds, ct);
            lockLookup = lockDocs.ToDictionary(l => l.SessionId);
        }

        // ── 5. Resolve current week ───────────────────────────────────────────────
        var publishedWeeks = plan.Weeks
            .Where(w => w.Status == WeekStatus.Published)
            .OrderBy(w => w.WeekNumber)
            .ToList();

        int? currentWeek = null;
        if (publishedWeeks.Count > 0)
        {
            currentWeek = PlanWeekCalculator.ResolveCurrentWeekNumber(
                plan.StartDate,
                publishedWeeks.Select(w => w.WeekNumber).ToList(),
                plan.Weeks.Count,
                publishedWeeks.First().DatePublished,
                plan.DateCreated,
                todayLocalUtc);
        }

        // ── 6. Build response ─────────────────────────────────────────────────────

        // Shared by a workout's nested exercises AND a session's standalone exercises (#857
        // phase 3a) — both are SessionExercise instances with identical completion/enrichment
        // rules, so the mapping lives in one place rather than being duplicated per call site.
        ExerciseDto BuildExerciseDto(SessionExercise ex, Guid sessionId, Guid? workoutId)
        {
            var muscleGroups = muscleGroupMap.TryGetValue(ex.ExerciseExternalId, out var mg)
                ? mg
                : [];

            var setDtos = ex.Sets.Select(set =>
            {
                var key = (sessionId, workoutId, ex.ExerciseExternalId, set.SetNumber);
                completedSets.TryGetValue(key, out var completedAt);
                loggedSets.TryGetValue(key, out var loggedSet);

                return new SetDto
                {
                    SetNumber = set.SetNumber,
                    Type = set.Type.ToString(),
                    Reps = set.Reps,
                    WeightKg = set.WeightKg,
                    DurationSeconds = set.DurationSeconds,
                    DistanceMeters = set.DistanceMeters,
                    RestSeconds = set.RestSeconds,
                    CompletedAt = completedAt == default ? null : completedAt,
                    // Actual values from the workout log (null when not yet logged).
                    ActualReps = loggedSet?.Reps,
                    ActualWeightKg = loggedSet?.WeightKg,
                    ActualRpe = loggedSet?.Rpe,
                    ActualDurationSeconds = loggedSet?.DurationSeconds,
                    ActualDistanceMeters = loggedSet?.DistanceMeters,
                    // Snapshot-planned values (null on legacy logs → isModified stays false).
                    PlannedReps = loggedSet?.PlannedReps,
                    PlannedWeightKg = loggedSet?.PlannedWeightKg,
                    PlannedRpe = loggedSet?.PlannedRpe,
                    PlannedDurationSeconds = loggedSet?.PlannedDurationSeconds,
                    PlannedDistanceMeters = loggedSet?.PlannedDistanceMeters,
                    IsModified = loggedSet?.IsModified ?? false
                };
            }).ToList();

            // An exercise is complete when EITHER this specific instance was marked complete
            // directly (checkbox/instance-keyed completion — the only path a set-less exercise
            // can ever satisfy, since it has no sets for the all-sets-completed check below), OR
            // every planned set has a log entry (#877 — previously the set-based check alone,
            // which meant a set-less checkbox-completed exercise could never report complete).
            var instanceCompleted = completedInstanceIdsBySession.TryGetValue(sessionId, out var completedInstanceIds)
                && completedInstanceIds.Contains(ex.ExerciseId);
            var isCompleted = instanceCompleted || (setDtos.Count > 0 && setDtos.All(s => s.CompletedAt is not null));
            var hasModifications = setDtos.Any(s => s.IsModified);

            return new ExerciseDto
            {
                ExerciseId = ex.ExerciseId,
                ExerciseExternalId = ex.ExerciseExternalId,
                ExerciseName = ex.ExerciseName,
                Order = ex.Order,
                Notes = ex.Notes,
                RestSeconds = ex.RestSeconds,
                // Surface the movement type so the client can
                // pick the right summary template (reps /
                // duration / distance / reps-for-time).
                MovementType = ex.MovementType.ToString(),
                MuscleGroups = muscleGroups,
                IsCompleted = isCompleted,
                HasModifications = hasModifications,
                Sets = setDtos
            };
        }

        var weekDtos = publishedWeeks.Select(week =>
        {
            // Compute week start/end from StartDate when available.
            // Fall back to DatePublished for legacy plans; leave null when neither is set.
            DateTime? weekStart = null;
            DateTime? weekEnd = null;

            var anchor = plan.StartDate ?? week.DatePublished;
            if (anchor.HasValue)
            {
                weekStart = anchor.Value.Date.AddDays((week.WeekNumber - 1) * 7);
                weekEnd = weekStart.Value.AddDays(6);
            }

            var sessionDtos = week.Days
                .SelectMany(day => day.Sessions.Select(session => (day.DayOfWeek, Session: session)))
                .Select(x =>
            {
                var (dayOfWeek, session) = x;

                // Build per-workout DTOs, keeping each workout's own Order alongside it so the
                // flat merge below can interleave workouts and standalone exercises correctly.
                var workoutComponents = session.Workouts.OrderBy(workout => workout.Order).Select(workout =>
                {
                    var workoutExerciseDtos = workout.Exercises
                        .Select(ex => BuildExerciseDto(ex, session.SessionId, workout.WorkoutId))
                        .ToList();

                    var workoutIsCompleted = workoutExerciseDtos.Count > 0
                        ? workoutExerciseDtos.All(e => e.IsCompleted)
                        : completedSectionIdsBySession.TryGetValue(session.SessionId, out var completedSecs)
                            && completedSecs.Contains(workout.WorkoutId);

                    var dto = new WorkoutDto
                    {
                        WorkoutId = workout.WorkoutId,
                        Order = workout.Order,
                        Name = workout.Name,
                        Format = workout.Format?.ToString(),
                        FormatConfig = workout.FormatConfig,
                        Notes = workout.Notes,
                        IsCompleted = workoutIsCompleted,
                        Exercises = workoutExerciseDtos
                    };

                    return (workout.Order, Dto: dto);
                }).ToList();

                var workoutDtos = workoutComponents.Select(c => c.Dto).ToList();

                // Standalone exercises directly on the session (#857 phase 3a) — sit alongside
                // Workouts, sharing the same shared Order sequence (see UpdateTrainingPlanValidator's
                // cross-list duplicate-Order check).
                var standaloneComponents = session.StandaloneExercises
                    .Select(ex => (ex.Order, ExerciseDto: BuildExerciseDto(ex, session.SessionId, null)))
                    .ToList();

                var standaloneExerciseDtos = standaloneComponents.Select(c => c.ExerciseDto).ToList();

                // Flat exercise list — merges workout-nested and standalone exercises by the ONE
                // shared Order sequence they occupy within a session. Standalone exercises are
                // session content just like workouts, so they must appear here (and be counted in
                // TotalExerciseCount/CompletedExerciseCount below) — a standalone-only session was
                // previously invisible to this endpoint because this list only walked Workouts.
                var exerciseDtos = workoutComponents
                    .Select(c => (c.Order, Exercises: (IReadOnlyList<ExerciseDto>)c.Dto.Exercises))
                    .Concat(standaloneComponents.Select(c => (c.Order, Exercises: (IReadOnlyList<ExerciseDto>)[c.ExerciseDto])))
                    .OrderBy(c => c.Order)
                    .SelectMany(c => c.Exercises)
                    .ToList();

                var completedExerciseCount = exerciseDtos.Count(e => e.IsCompleted);
                var sessionHasModifications = exerciseDtos.Any(e => e.HasModifications);

                // Resolve lock state for this session (Stable if no active lock doc).
                var sessionLockState = "Stable";
                string? sessionLockHolder = null;
                if (lockLookup.TryGetValue(session.SessionId, out var sessionLock))
                {
                    sessionLockState = sessionLock.Type.ToString();
                    sessionLockHolder = sessionLock.Holder.ToString();
                }

                return new SessionDto
                {
                    SessionId = session.SessionId,
                    DayOfWeek = dayOfWeek,
                    Name = session.Name,
                    Order = session.Order,
                    Notes = session.Notes,
                    CompletedExerciseCount = completedExerciseCount,
                    TotalExerciseCount = exerciseDtos.Count,
                    EstimatedDurationMinutes = null, // deferred — requires product-defined set-duration heuristic
                    Workouts = workoutDtos,
                    AllExercises = exerciseDtos,
                    StandaloneExercises = standaloneExerciseDtos,
                    LockState = sessionLockState,
                    LockHolder = sessionLockHolder,
                    HasModifications = sessionHasModifications
                };
            }).ToList();

            return new WeekDto
            {
                WeekNumber = week.WeekNumber,
                Status = week.Status.ToString(),
                DatePublished = week.DatePublished,
                WeekStartDate = weekStart,
                WeekEndDate = weekEnd,
                DayNotes = week.Days
                    .Where(d => d.Note is not null)
                    .ToDictionary(d => d.DayOfWeek, d => d.Note!),
                Sessions = sessionDtos
            };
        }).ToList();

        await Send.OkAsync(new GetFullTrainingPlanResponse
        {
            PlanId = plan.ExternalId,
            PlanName = plan.Name,
            Status = plan.Status.ToString(),
            StartDate = plan.StartDate,
            CurrentWeek = currentWeek,
            TotalWeeks = plan.Weeks.Count,
            PublishedWeekCount = publishedWeeks.Count,
            QuestionnaireResponseId = plan.QuestionnaireResponseId,
            DateCompleted = plan.DateCompleted,
            RunsUntil = await this.ResolveRunsUntilForOverCapClientAsync(
                db, plan.TrainerId, clientId, plan.StartDate, publishedWeeks.Select(w => w.WeekNumber), ct),
            Weeks = weekDtos
        }, ct);
    }
}
