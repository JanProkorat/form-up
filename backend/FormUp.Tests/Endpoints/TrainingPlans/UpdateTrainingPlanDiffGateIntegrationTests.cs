using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace FormUp.Tests.Endpoints.TrainingPlans;

/// <summary>
/// Testcontainers integration tests (real MongoDB) for the diff-gate in
/// <c>PUT /training/plans/{planId}</c>. Covers gap #5b found in the fresh-eyes review
/// of issue #381: dropping a published session from the request (i.e. the stored
/// published SessionId does not appear in the incoming map) must be rejected with 409
/// <c>session_locked</c> unless the trainer holds an Editing lock for that session.
///
/// Gap #5a (legacy-doc no false-positive) was retired by #837: a stored session
/// reaching this endpoint is always sections/workouts-populated, so there is no
/// longer a legacy-doc-vs-section-request comparison for the diff-gate to
/// false-positive on. See the comment above <c>SeedTwoSectionPlanWithCompletedLogAsync</c>
/// below for the current state of that retirement.
/// </summary>
[Collection(TestCollection.Shard2)]
public class UpdateTrainingPlanDiffGateIntegrationTests(FitnessApiFactory factory)
{
    private static string UniqueEmail() => $"{Guid.NewGuid():N}@diff-gate-test.com";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    // ── shared plan-building helpers ─────────────────────────────────────────────

    /// <summary>
    /// Seeds a published plan in Mongo whose single session uses the sections-based layout.
    /// </summary>
    private async Task<(TrainingPlan Plan, Guid SessionId, Guid SectionId, Guid ExerciseId)>
        SeedSectionPublishedPlanAsync(Guid trainerUserId, Guid clientUserId)
    {
        var planId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var exId = Guid.NewGuid();

        var session = new TrainingSession
        {
            SessionId = sessionId,
            Name = "Modern Day",
            Order = 1,
            Workouts =
            [
                new TrainingWorkout
                {
                    WorkoutId = sectionId,
                    Order = 0,
                    Name = "Hlavní",
                    Exercises =
                    [
                        new SessionExercise
                        {
                            ExerciseExternalId = exId,
                            ExerciseName = "Bench Press",
                            Order = 1,
                            MovementType = MovementType.Reps,
                            Sets =
                            [
                                new ExerciseSet { SetNumber = 1, Type = SetType.Normal, Reps = 10, WeightKg = 80 }
                            ]
                        }
                    ]
                }
            ]
        };

        var plan = new TrainingPlan
        {
            ExternalId = planId,
            ClientId = clientUserId,
            TrainerId = trainerUserId,
            Name = "Section Diff-Gate Plan",
            Status = TrainingPlanStatus.Active,
            StartDate = TrainingPlanTestHelpers.LastMonday(),
            Version = 1,
            DateCreated = DateTime.UtcNow.AddDays(-14),
            Weeks =
            [
                new TrainingWeek
                {
                    WeekNumber = 1,
                    Status = WeekStatus.Published,
                    DatePublished = DateTime.UtcNow.AddDays(-7),
                    Days = TrainingPlanTestHelpers.MaterializeDays((1, session))
                }
            ]
        };

        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        await mongo.TrainingPlans.InsertOneAsync(plan, cancellationToken: TestContext.Current.CancellationToken);

        return (plan, sessionId, sectionId, exId);
    }

    // ── gap #5a: legacy-doc backfill no false-positive — RETIRED (#837) ──────────
    //
    // The legacy flat-exercise diff-gate scenario previously covered here is retired:
    // a stored session reaching this endpoint is always sections/workouts-populated,
    // so there is no longer a legacy-doc-vs-section-request comparison for the diff-gate
    // to false-positive on. (#857 subsequently deleted the boot-time backfill that used
    // to synthesize the modern shape from legacy flat `exercises` docs — see
    // MongoIndexInitializer and its TrainingTreeRestructureMigrationTests absence-test
    // coverage — legacy documents are simply left untouched now, not migrated on read.)

    // ── Section-finished guard helpers ──────────────────────────────────────────

    /// <summary>
    /// Seeds a two-section plan and a completed WorkoutLog for its session.
    /// Returns (plan, sessionId, sectionAId, sectionBId, exerciseAId, exerciseBId).
    /// </summary>
    private async Task<(TrainingPlan Plan, Guid SessionId, Guid SectionAId, Guid SectionBId, Guid ExerciseAId, Guid ExerciseBId)>
        SeedTwoSectionPlanWithCompletedLogAsync(Guid trainerUserId, Guid clientUserId)
    {
        var planId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var sectionAId = Guid.NewGuid();
        var sectionBId = Guid.NewGuid();
        var exerciseAId = Guid.NewGuid();
        var exerciseBId = Guid.NewGuid();

        var session = new TrainingSession
        {
            SessionId = sessionId,
            Name = "Two-Section Day",
            Order = 1,
            Workouts =
            [
                new TrainingWorkout
                {
                    WorkoutId = sectionAId,
                    Order = 0,
                    Name = "Section A",
                    Exercises =
                    [
                        new SessionExercise
                        {
                            ExerciseExternalId = exerciseAId,
                            ExerciseName = "Squat",
                            Order = 1,
                            MovementType = MovementType.Reps,
                            Sets = [new ExerciseSet { SetNumber = 1, Type = SetType.Normal, Reps = 5, WeightKg = 100 }]
                        }
                    ]
                },
                new TrainingWorkout
                {
                    WorkoutId = sectionBId,
                    Order = 1,
                    Name = "Section B",
                    Exercises =
                    [
                        new SessionExercise
                        {
                            ExerciseExternalId = exerciseBId,
                            ExerciseName = "Press",
                            Order = 1,
                            MovementType = MovementType.Reps,
                            Sets = [new ExerciseSet { SetNumber = 1, Type = SetType.Normal, Reps = 8, WeightKg = 80 }]
                        }
                    ]
                }
            ]
        };

        var clientId = clientUserId;

        var plan = new TrainingPlan
        {
            ExternalId = planId,
            ClientId = clientId,
            TrainerId = trainerUserId,
            Name = "Section Finished Guard Plan",
            Status = TrainingPlanStatus.Active,
            StartDate = TrainingPlanTestHelpers.LastMonday(),
            Version = 1,
            DateCreated = DateTime.UtcNow.AddDays(-14),
            Weeks =
            [
                new TrainingWeek
                {
                    WeekNumber = 1,
                    Status = WeekStatus.Published,
                    DatePublished = DateTime.UtcNow.AddDays(-7),
                    Days = TrainingPlanTestHelpers.MaterializeDays((1, session))
                }
            ]
        };

        // #841: UpdateTrainingPlanEndpoint reads mongo.SessionExecutions exclusively — seed a
        // completed SessionExecution (Performance mirrors the retired WorkoutLog shape) instead
        // of a WorkoutLog document.
        var startedAt = DateTime.UtcNow.AddHours(-1);
        var execution = new SessionExecution
        {
            ExternalId = Guid.NewGuid(),
            ClientId = clientId,
            PlanId = planId,
            SessionId = sessionId,
            Date = SessionExecution.ToCompletionDateUtc(startedAt),
            Status = SessionExecutionStatus.Completed,
            Performance = new SessionExecutionPerformance
            {
                StartedAt = startedAt,
                CompletedAt = DateTime.UtcNow.AddMinutes(-30),
                Workouts =
                [
                    new LoggedWorkout
                    {
                        WorkoutId = sectionAId, Order = 0, Name = "Section A",
                        Exercises = [new WorkoutExercise
                        {
                            ExerciseExternalId = exerciseAId, ExerciseName = "Squat",
                            Sets = [new WorkoutSet { SetNumber = 1, Reps = 5, CompletedAt = DateTime.UtcNow.AddMinutes(-50) }]
                        }]
                    },
                    new LoggedWorkout
                    {
                        WorkoutId = sectionBId, Order = 1, Name = "Section B",
                        Exercises = [new WorkoutExercise
                        {
                            ExerciseExternalId = exerciseBId, ExerciseName = "Press",
                            Sets = [new WorkoutSet { SetNumber = 1, Reps = 8, CompletedAt = DateTime.UtcNow.AddMinutes(-40) }]
                        }]
                    }
                ]
            },
            DateCreated = startedAt,
            Version = 1
        };

        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        await mongo.TrainingPlans.InsertOneAsync(plan, cancellationToken: TestContext.Current.CancellationToken);
        await mongo.SessionExecutions.InsertOneAsync(execution, cancellationToken: TestContext.Current.CancellationToken);

        return (plan, sessionId, sectionAId, sectionBId, exerciseAId, exerciseBId);
    }

    /// <summary>
    /// Seeds a two-section plan and a partial TrainingCompletion that marks only section A
    /// as finished (section B is unfinished). Returns the plan + IDs.
    /// </summary>
    private async Task<(TrainingPlan Plan, Guid SessionId, Guid SectionAId, Guid SectionBId, Guid ExerciseAId, Guid ExerciseBId)>
        SeedTwoSectionPlanWithPartialCompletionAsync(Guid trainerUserId, Guid clientUserId)
    {
        var planId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var sectionAId = Guid.NewGuid();
        var sectionBId = Guid.NewGuid();
        var exerciseAId = Guid.NewGuid();
        var exerciseBId = Guid.NewGuid();

        var session = new TrainingSession
        {
            SessionId = sessionId,
            Name = "Two-Section Day",   // must match BuildTwoSectionUpdateBody
            Order = 1,
            Workouts =
            [
                new TrainingWorkout
                {
                    WorkoutId = sectionAId, Order = 0, Name = "Section A",
                    Exercises = [new SessionExercise
                    {
                        ExerciseId = exerciseAId,
                        ExerciseExternalId = exerciseAId, ExerciseName = "Squat", Order = 1,   // must match BuildTwoSectionUpdateBody
                        MovementType = MovementType.Reps,
                        Sets = [new ExerciseSet { SetNumber = 1, Type = SetType.Normal, Reps = 5, WeightKg = 100 }]
                    }]
                },
                new TrainingWorkout
                {
                    WorkoutId = sectionBId, Order = 1, Name = "Section B",
                    Exercises = [new SessionExercise
                    {
                        ExerciseId = exerciseBId,
                        ExerciseExternalId = exerciseBId, ExerciseName = "Press", Order = 1,   // must match BuildTwoSectionUpdateBody
                        MovementType = MovementType.Reps,
                        Sets = [new ExerciseSet { SetNumber = 1, Type = SetType.Normal, Reps = 8, WeightKg = 80 }]
                    }]
                }
            ]
        };

        var clientId = clientUserId;

        var plan = new TrainingPlan
        {
            ExternalId = planId,
            ClientId = clientId,
            TrainerId = trainerUserId,
            Name = "Mixed-State Guard Plan",
            Status = TrainingPlanStatus.Active,
            StartDate = TrainingPlanTestHelpers.LastMonday(),
            Version = 1,
            DateCreated = DateTime.UtcNow.AddDays(-14),
            Weeks =
            [
                new TrainingWeek
                {
                    WeekNumber = 1,
                    Status = WeekStatus.Published,
                    DatePublished = DateTime.UtcNow.AddDays(-7),
                    Days = TrainingPlanTestHelpers.MaterializeDays((1, session))
                }
            ]
        };

        // Partial completion: only section A's exercise done.
        // #841: UpdateTrainingPlanEndpoint reads mongo.SessionExecutions exclusively — seed a
        // Partial SessionExecution carrying the completion flags instead of a TrainingCompletion.
        var execution = new SessionExecution
        {
            ExternalId = Guid.NewGuid(),
            ClientId = clientId,
            SessionId = sessionId,
            Date = DateTime.UtcNow.Date,
            Status = SessionExecutionStatus.Partial,
            // The per-workout attribution dictionary is gone; completion is a flat list of
            // SessionExercise instance ids. This fixture builds its sessions inline (not via
            // TrainingPlanTestHelpers), so it sets ExerciseId == ExerciseExternalId on the
            // seeded exercises above — that is what makes exerciseAId address the same exercise.
            CompletedExerciseInstanceIds = [exerciseAId],
            Version = 1,
            DateCreated = DateTime.UtcNow
        };

        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        await mongo.TrainingPlans.InsertOneAsync(plan, cancellationToken: TestContext.Current.CancellationToken);
        await mongo.SessionExecutions.InsertOneAsync(execution, cancellationToken: TestContext.Current.CancellationToken);

        return (plan, sessionId, sectionAId, sectionBId, exerciseAId, exerciseBId);
    }

    /// <summary>
    /// Builds the request body to update the two-section plan, editing the content of one
    /// specific section (changes its set reps) while keeping the other section unchanged.
    /// </summary>
    private static object BuildTwoSectionUpdateBody(
        TrainingPlan plan,
        Guid sessionId,
        Guid sectionAId, Guid sectionBId,
        Guid exerciseAId, Guid exerciseBId,
        bool changeA, bool changeB)
    {
        return new
        {
            Name = plan.Name,
            Version = plan.Version,
            StartDate = plan.StartDate,
            Weeks = new[]
            {
                new
                {
                    WeekNumber = 1,
                    Sessions = new[]
                    {
                        new
                        {
                            SessionId = sessionId.ToString(),
                            DayOfWeek = 1,
                            Name = "Two-Section Day",
                            Order = 1,
                            // Must match UpdateSessionRequest's property name: this payload is
                            // hand-built, so the section->workout rename does not reach it
                            // automatically. Posting the old "Sections" key binds an empty
                            // Workouts list, which the phase-3a "a session must have at least one
                            // workout or standalone exercise" rule then rejects with 400.
                            Workouts = new[]
                            {
                                new
                                {
                                    WorkoutId = sectionAId.ToString(),
                                    Order = 0,
                                    Name = "Section A",
                                    Exercises = new[]
                                    {
                                        new
                                        {
                                            ExerciseExternalId = exerciseAId.ToString(),
                                            ExerciseName = "Squat",
                                            Order = 1,
                                            MovementType = "Reps",
                                            Sets = new[]
                                            {
                                                new { SetNumber = 1, Type = "Normal",
                                                    Reps = changeA ? 10 : 5, WeightKg = 100.0 }
                                            }
                                        }
                                    }
                                },
                                new
                                {
                                    WorkoutId = sectionBId.ToString(),
                                    Order = 1,
                                    Name = "Section B",
                                    Exercises = new[]
                                    {
                                        new
                                        {
                                            ExerciseExternalId = exerciseBId.ToString(),
                                            ExerciseName = "Press",
                                            Order = 1,
                                            MovementType = "Reps",
                                            Sets = new[]
                                            {
                                                new { SetNumber = 1, Type = "Normal",
                                                    Reps = changeB ? 12 : 8, WeightKg = 80.0 }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };
    }

    /// <summary>
    /// Acquires an Editing lock for the given session via POST /training/plans/{planId}/sessions/{sessionId}/unlock.
    /// Returns the updated plan version from the response (if available), or falls back to seeding the lock
    /// directly in MongoDB when the endpoint returns 409 (session already finished in the DB state).
    /// </summary>
    private async Task AcquireEditingLockAsync(
        HttpClient httpClient, TrainingPlan plan, Guid sessionId, string accessToken,
        Guid trainerUserId)
    {
        TestHelpers.SetBearerToken(httpClient, accessToken);
        var unlockResponse = await httpClient.PostAsJsonAsync(
            $"/training/plans/{plan.ExternalId}/sessions/{sessionId}/unlock",
            new { PlanId = plan.ExternalId, SessionId = sessionId },
            TestContext.Current.CancellationToken);

        // If the session already has completion data, the unlock endpoint may return 409.
        // In that case, seed the lock directly in MongoDB (the test exercises the PUT guard,
        // not the unlock guard).
        if (unlockResponse.StatusCode == HttpStatusCode.Conflict)
        {
            using var scope = factory.Services.CreateScope();
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
            var lockDoc = new SessionLock
            {
                SessionId = sessionId,
                PlanId = plan.ExternalId,
                ClientId = plan.ClientId,
                TrainerId = trainerUserId,
                Type = LockType.Editing,
                Holder = LockHolder.Coach,
                AcquiredAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddHours(2)
            };
            await mongo.SessionLocks.InsertOneAsync(lockDoc, cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    // ── gap #5b: removed/replaced published session without lock → 409 ───────────

    /// <summary>
    /// Dropping a stored published session from the incoming request (its SessionId
    /// is absent from the request) must be rejected with HTTP 409. The plan is stored
    /// with sections-layout; no Editing lock is held by the trainer for that session.
    /// </summary>
    [Fact]
    public async Task UpdatePlan_RemovedPublishedSession_WithoutEditingLock_Returns409()
    {
        // ── 1. Register + login trainer ───────────────────────────────────────────
        var httpClient = factory.CreateClient();
        var email = UniqueEmail();
        await TestHelpers.RegisterAsync(httpClient, email, "TestPass1!", "Remove", "DiffGate", "Trainer");
        var (accessToken, _) = await TestHelpers.LoginAsync(httpClient, email, "TestPass1!");

        Guid trainerUserId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await db.Users.FirstAsync(
                u => u.Email == email,
                TestContext.Current.CancellationToken);
            trainerUserId = user.Id;
        }

        // ── 2. Seed a published plan with a session ───────────────────────────────
        // Plan routes authorize on the live link, so the plan's ClientId must be a
        // client this trainer is actually linked to.
        var linkedClientId = await TestHelpers.RegisterLinkedClientAsync(
            factory, trainerUserId, TestContext.Current.CancellationToken);
        var (plan, sessionId, _, exerciseId) = await SeedSectionPublishedPlanAsync(trainerUserId, linkedClientId);

        // ── 3. Build an UPDATE request that OMITS the published session ────────────
        // Sending week 1 with an EMPTY sessions list effectively removes the published
        // session. No SessionId mismatch is needed — pure absence is enough. No lock.
        var body = new
        {
            Name = plan.Name,
            Version = plan.Version,
            StartDate = plan.StartDate,
            Weeks = new[]
            {
                new
                {
                    WeekNumber = 1,
                    Sessions = Array.Empty<object>() // session removed
                }
            }
        };

        // ── 4. PUT /training/plans/{planId} ───────────────────────────────────────
        TestHelpers.SetBearerToken(httpClient, accessToken);
        var response = await httpClient.PutAsJsonAsync(
            $"/training/plans/{plan.ExternalId}",
            body,
            TestContext.Current.CancellationToken);

        // ── 5. Assert 409 with session_locked error code ───────────────────────────
        var responseBody = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(
            HttpStatusCode.Conflict,
            $"removing a published session without an Editing lock must be rejected 409. Body: {responseBody}");
        responseBody.Should().Contain(
            "session_locked",
            "the RFC 7807 error_code must be 'session_locked'");
    }

    // ── #465: section-finished guard ────────────────────────────────────────────

    /// <summary>
    /// When the session has a completed WorkoutLog (Signal 1) and the trainer holds an Editing
    /// lock, attempting to change any section's content must be rejected with 409 WORKOUT_ALREADY_COMPLETED.
    /// </summary>
    [Fact]
    public async Task UpdatePlan_FinishedSectionContent_WorkoutLogSignal_Returns409SectionAlreadyCompleted()
    {
        // ── 1. Register + login trainer ───────────────────────────────────────────
        var httpClient = factory.CreateClient();
        var email = UniqueEmail();
        await TestHelpers.RegisterAsync(httpClient, email, "TestPass1!", "Guard", "WorkoutLog", "Trainer");
        var (accessToken, _) = await TestHelpers.LoginAsync(httpClient, email, "TestPass1!");

        Guid trainerUserId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await db.Users.FirstAsync(
                u => u.Email == email,
                TestContext.Current.CancellationToken);
            trainerUserId = user.Id;
        }

        // ── 2. Seed a two-section plan with a completed WorkoutLog ────────────────
        var (plan, sessionId, sectionAId, sectionBId, exerciseAId, exerciseBId) =
            await SeedTwoSectionPlanWithCompletedLogAsync(
                trainerUserId,
                await TestHelpers.RegisterLinkedClientAsync(
                    factory, trainerUserId, TestContext.Current.CancellationToken));

        // ── 3. Acquire editing lock (seed directly — unlock guard blocks finished sessions) ──
        await AcquireEditingLockAsync(httpClient, plan, sessionId, accessToken, trainerUserId);

        // ── 4. Build an update that changes section A's content ───────────────────
        var body = BuildTwoSectionUpdateBody(
            plan, sessionId,
            sectionAId, sectionBId,
            exerciseAId, exerciseBId,
            changeA: true, changeB: false);

        // ── 5. PUT /training/plans/{planId} ───────────────────────────────────────
        TestHelpers.SetBearerToken(httpClient, accessToken);
        var response = await httpClient.PutAsJsonAsync(
            $"/training/plans/{plan.ExternalId}",
            body,
            TestContext.Current.CancellationToken);

        // ── 6. Assert 409 WORKOUT_ALREADY_COMPLETED ───────────────────────────────
        var responseBody = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(
            HttpStatusCode.Conflict,
            $"editing a finished section (WorkoutLog signal) must be rejected 409. Body: {responseBody}");
        responseBody.Should().Contain(
            "WORKOUT_ALREADY_COMPLETED",
            "the RFC 7807 errorCode must be WORKOUT_ALREADY_COMPLETED");
    }

    /// <summary>
    /// MIXED-STATE: a session where section A is finished (TrainingCompletion Signal 2) and
    /// section B is NOT finished. Editing section B must return 200; editing section A must
    /// return 409 WORKOUT_ALREADY_COMPLETED.
    /// </summary>
    [Fact]
    public async Task UpdatePlan_MixedState_FinishedAndUnfinishedSections_TrainingCompletionSignal()
    {
        // ── 1. Register + login trainer ───────────────────────────────────────────
        var httpClient = factory.CreateClient();
        var email = UniqueEmail();
        await TestHelpers.RegisterAsync(httpClient, email, "TestPass1!", "Guard", "MixedState", "Trainer");
        var (accessToken, _) = await TestHelpers.LoginAsync(httpClient, email, "TestPass1!");

        Guid trainerUserId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await db.Users.FirstAsync(
                u => u.Email == email,
                TestContext.Current.CancellationToken);
            trainerUserId = user.Id;
        }

        // ── 2. Seed plan with partial completion (section A done, section B not) ──
        // Plan routes authorize on the live link, so both this plan and the re-seeded
        // one below must carry a client this trainer is actually linked to.
        var linkedClientId = await TestHelpers.RegisterLinkedClientAsync(
            factory, trainerUserId, TestContext.Current.CancellationToken);
        var (plan, sessionId, sectionAId, sectionBId, exerciseAId, exerciseBId) =
            await SeedTwoSectionPlanWithPartialCompletionAsync(trainerUserId, linkedClientId);

        // ── 3a. Acquire editing lock and edit section B (unfinished) → expect 200 ──
        await AcquireEditingLockAsync(httpClient, plan, sessionId, accessToken, trainerUserId);

        var bodyChangeB = BuildTwoSectionUpdateBody(
            plan, sessionId,
            sectionAId, sectionBId,
            exerciseAId, exerciseBId,
            changeA: false, changeB: true);

        TestHelpers.SetBearerToken(httpClient, accessToken);
        var responseB = await httpClient.PutAsJsonAsync(
            $"/training/plans/{plan.ExternalId}",
            bodyChangeB,
            TestContext.Current.CancellationToken);

        var responseBodyB = await responseB.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        responseB.StatusCode.Should().Be(
            HttpStatusCode.OK,
            $"editing the unfinished section B must return 200. Body: {responseBodyB}");

        // ── 3b. Re-seed the plan (version was bumped) and acquire a fresh editing lock ──
        // After the 200, the plan version is bumped; we need to re-seed the original plan
        // to test editing section A at version 1.
        var (plan2, sessionId2, sectionAId2, sectionBId2, exerciseAId2, exerciseBId2) =
            await SeedTwoSectionPlanWithPartialCompletionAsync(trainerUserId, linkedClientId);

        await AcquireEditingLockAsync(httpClient, plan2, sessionId2, accessToken, trainerUserId);

        // ── 4. Edit section A (finished) → expect 409 ────────────────────────────
        var bodyChangeA = BuildTwoSectionUpdateBody(
            plan2, sessionId2,
            sectionAId2, sectionBId2,
            exerciseAId2, exerciseBId2,
            changeA: true, changeB: false);

        TestHelpers.SetBearerToken(httpClient, accessToken);
        var responseA = await httpClient.PutAsJsonAsync(
            $"/training/plans/{plan2.ExternalId}",
            bodyChangeA,
            TestContext.Current.CancellationToken);

        var responseBodyA = await responseA.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        responseA.StatusCode.Should().Be(
            HttpStatusCode.Conflict,
            $"editing the finished section A must return 409. Body: {responseBodyA}");
        responseBodyA.Should().Contain(
            "WORKOUT_ALREADY_COMPLETED",
            "the RFC 7807 errorCode must be WORKOUT_ALREADY_COMPLETED");
    }
}
