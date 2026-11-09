using System.Security.Claims;
using FastEndpoints;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Features.TrainingPlans.UnlockTrainingSession;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Application.Infrastructure.Services;
using FormUp.Tests.Builders;
using FormUp.Tests.Endpoints;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using NSubstitute;

namespace FormUp.Tests.Endpoints.TrainingPlans;

/// <summary>
/// Tests for the finished-guard added to <see cref="UnlockTrainingSessionEndpoint"/> (issue #429).
/// Verifies that unlocking a session whose WorkoutLog is already completed returns 409 SESSION_ALREADY_COMPLETED,
/// and that the endpoint still succeeds (204) when no completed log exists.
/// </summary>
public class UnlockSessionFinishedGuardTests
{
    private readonly Guid _trainerId = Guid.NewGuid();
    private readonly Guid _clientId = Guid.NewGuid();

    private IOptions<TrainingLockOptions> DefaultOptions() =>
        Options.Create(new TrainingLockOptions { EditingTtlHours = 2, LiveTtlHours = 6 });

    /// <summary>Creates a minimal plan with one session.</summary>
    private TrainingPlan CreatePlan(Guid planId, Guid sessionId) =>
        new()
        {
            ExternalId = planId,
            ClientId = _clientId,
            TrainerId = _trainerId,
            Name = "Test Plan",
            Status = TrainingPlanStatus.Active,
            StartDate = TrainingPlanTestHelpers.LastMonday(),
            Weeks =
            [
                new TrainingWeek
                {
                    WeekNumber = 1,
                    Status = WeekStatus.Published,
                    Days = TrainingPlanTestHelpers.MaterializeDays(
(1, new TrainingSession
                        {
                            SessionId = sessionId,
                            Name = "Test Session",
                            Order = 1,
                            Workouts = []
                        }))
                }
            ],
            Version = 1,
            DateCreated = DateTime.UtcNow.AddDays(-30)
        };

    /// <summary>
    /// Creates an IMongoContext whose SessionExecutions collection carries an optional
    /// completed-live-session document (<paramref name="completedLogCount"/> &gt; 0) plus any
    /// caller-supplied checkbox-path (Status=Partial) documents.
    /// #841: UnlockTrainingSessionEndpoint reads exclusively mongo.SessionExecutions and calls
    /// IsSessionComplete() on each returned document — session-level completeness is derived by
    /// the same IsSessionComplete()/IsWorkoutComplete() extension the endpoint calls.
    /// </summary>
    private IMongoContext CreateMockMongo(
        TrainingPlan plan,
        long completedLogCount,
        List<SessionExecution>? completionExecutions = null)
    {
        var mongo = Substitute.For<IMongoContext>();

        // Training plans — FindAsync returns the plan
        var planCollection = TrainingPlanTestHelpers.CreateMockCollection([plan]);
        mongo.TrainingPlans.Returns(planCollection);

        var sessionId = plan.Weeks
            .SelectMany(w => w.Days)
            .SelectMany(d => d.Sessions)
            .Select(s => s.SessionId)
            .FirstOrDefault();
        var executions = new List<SessionExecution>();

        if (completedLogCount > 0)
        {
            var now = DateTime.UtcNow;
            executions.Add(new SessionExecution
            {
                ExternalId = Guid.NewGuid(),
                ClientId = plan.ClientId,
                SessionId = sessionId,
                Date = SessionExecution.ToCompletionDateUtc(now),
                Status = SessionExecutionStatus.Completed,
                Performance = new SessionExecutionPerformance { StartedAt = now, CompletedAt = now, Workouts = [] },
                DateCreated = now,
                Version = 1
            });
        }

        executions.AddRange(completionExecutions ?? []);

        var executionCollection = TrainingPlanTestHelpers.CreateMockSessionExecutionCollection(executions);
        mongo.SessionExecutions.Returns(executionCollection);

        return mongo;
    }

    /// <summary>Creates a lock service that returns <see cref="AcquireResult.Acquired"/>.</summary>
    private ISessionLockService LockServiceAcquired(Guid sessionId, Guid planId)
    {
        var svc = Substitute.For<ISessionLockService>();
        svc.AcquireAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(),
                Arg.Any<LockHolder>(), Arg.Any<LockType>(), Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new AcquireResult.Acquired(new SessionLock
            {
                SessionId = sessionId,
                PlanId = planId,
                ClientId = _clientId,
                TrainerId = _trainerId,
                Holder = LockHolder.Coach,
                Type = LockType.Editing,
                AcquiredAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddHours(2)
            }));
        return svc;
    }

    // ── Gap B: finished-guard rejects unlock on completed sessions ───────────────

    [Fact]
    public async Task Unlock_SessionAlreadyCompleted_Returns409SessionAlreadyCompleted()
    {
        // Arrange: plan exists with the session, but CountDocumentsAsync returns 1 (completed log exists).
        var planId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var plan = CreatePlan(planId, sessionId);

        var mongo = CreateMockMongo(plan, completedLogCount: 1);
        var lockService = LockServiceAcquired(sessionId, planId);
        var notifier = Substitute.For<IRealtimeNotifier>();

        var ep = Factory.Create<UnlockTrainingSessionEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(_trainerId, AppRoles.Trainer))),
            new MockDbBuilder().Build(), mongo, lockService, DefaultOptions(), notifier,
            EndpointTestHelpers.CreateGrantingLinkAuthorizationService());

        // Act
        await ep.HandleAsync(
            new UnlockTrainingSessionRequest { PlanId = planId, SessionId = sessionId },
            TestContext.Current.CancellationToken);

        // Assert: 409 returned for finished session
        ep.HttpContext.Response.StatusCode.Should().Be(409,
            "unlocking a finished session must return 409 SESSION_ALREADY_COMPLETED");

        // Lock was NOT acquired (finished-guard fires before AcquireAsync)
        await lockService.DidNotReceive().AcquireAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(),
            Arg.Any<LockHolder>(), Arg.Any<LockType>(), Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());

        // No SignalR events emitted
        await notifier.DidNotReceive().NotifyAsync(
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<object>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unlock_SessionNotCompleted_Returns204AndAcquiresLock()
    {
        // Arrange: plan exists, session exists, CountDocumentsAsync returns 0 → unlock proceeds.
        var planId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var plan = CreatePlan(planId, sessionId);

        var mongo = CreateMockMongo(plan, completedLogCount: 0);
        var lockService = LockServiceAcquired(sessionId, planId);
        var notifier = Substitute.For<IRealtimeNotifier>();

        var ep = Factory.Create<UnlockTrainingSessionEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(_trainerId, AppRoles.Trainer))),
            new MockDbBuilder().Build(), mongo, lockService, DefaultOptions(), notifier,
            EndpointTestHelpers.CreateGrantingLinkAuthorizationService());

        // Act
        await ep.HandleAsync(
            new UnlockTrainingSessionRequest { PlanId = planId, SessionId = sessionId },
            TestContext.Current.CancellationToken);

        // Assert: 204 success, lock was acquired
        ep.HttpContext.Response.StatusCode.Should().Be(204,
            "unlock on a non-finished session must succeed with 204");

        await lockService.Received(1).AcquireAsync(
            sessionId, planId, _clientId, _trainerId,
            LockHolder.Coach, LockType.Editing, Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unlock_PlanNotFound_Returns404_BeforeFinishedGuard()
    {
        // Arrange: query returns no plan for this trainer (ownership guard fires first with 404).
        var planId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        // CreateMockMongo with a plan belonging to a DIFFERENT trainer — query with otherTrainerId returns nothing
        var otherTrainer = Guid.NewGuid();
        // Use a separate mongo where plan collection is empty for simplicity.
        var mongo = Substitute.For<IMongoContext>();
        var emptyPlanCollection = TrainingPlanTestHelpers.CreateMockCollection([]);
        mongo.TrainingPlans.Returns(emptyPlanCollection);

        // SessionExecutions not reached (404 fires first) but stub to avoid null ref.
        var emptyExecutionCollection = TrainingPlanTestHelpers.CreateMockSessionExecutionCollection([]);
        mongo.SessionExecutions.Returns(emptyExecutionCollection);

        var ep = Factory.Create<UnlockTrainingSessionEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(otherTrainer, AppRoles.Trainer))),
            new MockDbBuilder().Build(), mongo, Substitute.For<ISessionLockService>(), DefaultOptions(), Substitute.For<IRealtimeNotifier>(),
            EndpointTestHelpers.CreateGrantingLinkAuthorizationService());

        // Act
        await ep.HandleAsync(
            new UnlockTrainingSessionRequest { PlanId = planId, SessionId = sessionId },
            TestContext.Current.CancellationToken);

        // Assert: 404 — ownership guard fires before finished-guard
        ep.HttpContext.Response.StatusCode.Should().Be(404);

        // SessionExecutions.FindAsync must NOT be called (404 short-circuits before finished-guard)
        await emptyExecutionCollection.DidNotReceive().FindAsync(
            Arg.Any<FilterDefinition<SessionExecution>>(),
            Arg.Any<FindOptions<SessionExecution, SessionExecution>>(),
            Arg.Any<CancellationToken>());
    }

    // ── Gap: TrainingCompletion-based finished guard (home-checkbox path) ────────

    /// <summary>
    /// Session was completed via the mobile home-checkbox (writes TrainingCompletion, no WorkoutLog).
    /// Unlock must return 409 SESSION_ALREADY_COMPLETED.
    /// </summary>
    [Fact]
    public async Task Unlock_SessionCompletedViaTrainingCompletion_NoWorkoutLog_Returns409()
    {
        var planId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var plan = CreatePlan(planId, sessionId);
        // plan.Sections = [] — exercise-free session → IsSessionComplete requires SectionId in CompletedSectionIds.
        // Use a plan with one exercise in a section to make the test realistic.
        var sectionId = Guid.NewGuid();
        var exerciseId = Guid.NewGuid();
        plan.Weeks[0].Days.SelectMany(d => d.Sessions).First().Workouts =
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
                        ExerciseId = exerciseId,
                        ExerciseExternalId = exerciseId,
                        ExerciseName = "Squat",
                        Order = 0,
                        Sets = []
                    }
                ]
            }
        ];

        // Fully-complete SessionExecution (checkbox path) for that session (all exercises marked done).
        var completionExecution = new SessionExecution
        {
            ExternalId = Guid.NewGuid(),
            ClientId = _clientId,
            SessionId = sessionId,
            Date = DateTime.UtcNow.Date,
            Status = SessionExecutionStatus.Partial,
            CompletedExerciseInstanceIds = [exerciseId],
            Version = 1,
            DateCreated = DateTime.UtcNow
        };

        // No completed live session.
        var mongo = CreateMockMongo(plan, completedLogCount: 0, completionExecutions: [completionExecution]);
        var lockService = LockServiceAcquired(sessionId, planId);
        var notifier = Substitute.For<IRealtimeNotifier>();

        var ep = Factory.Create<UnlockTrainingSessionEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(_trainerId, AppRoles.Trainer))),
            new MockDbBuilder().Build(), mongo, lockService, DefaultOptions(), notifier,
            EndpointTestHelpers.CreateGrantingLinkAuthorizationService());

        // Act
        await ep.HandleAsync(
            new UnlockTrainingSessionRequest { PlanId = planId, SessionId = sessionId },
            TestContext.Current.CancellationToken);

        // Assert: 409 from TrainingCompletion finished-guard
        ep.HttpContext.Response.StatusCode.Should().Be(409,
            "home-checkbox completion must prevent unlock just like a completed WorkoutLog");

        // Lock must NOT be acquired
        await lockService.DidNotReceive().AcquireAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(),
            Arg.Any<LockHolder>(), Arg.Any<LockType>(), Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Only a PARTIAL TrainingCompletion exists (not all exercises done).
    /// The finished-guard must NOT fire — unlock should proceed normally.
    /// </summary>
    [Fact]
    public async Task Unlock_SessionPartiallyComplete_NoWorkoutLog_Returns204()
    {
        var planId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var plan = CreatePlan(planId, sessionId);

        var sectionId = Guid.NewGuid();
        var exerciseId1 = Guid.NewGuid();
        var exerciseId2 = Guid.NewGuid();
        plan.Weeks[0].Days.SelectMany(d => d.Sessions).First().Workouts =
        [
            new TrainingWorkout
            {
                WorkoutId = sectionId,
                Order = 0,
                Name = "Hlavní",
                Exercises =
                [
                    new SessionExercise { ExerciseId = exerciseId1, ExerciseExternalId = exerciseId1, ExerciseName = "Squat", Order = 0, Sets = [] },
                    new SessionExercise { ExerciseId = exerciseId2, ExerciseExternalId = exerciseId2, ExerciseName = "Press", Order = 1, Sets = [] }
                ]
            }
        ];

        // Only exerciseId1 done — partial completion.
        var partialCompletionExecution = new SessionExecution
        {
            ExternalId = Guid.NewGuid(),
            ClientId = _clientId,
            SessionId = sessionId,
            Date = DateTime.UtcNow.Date,
            Status = SessionExecutionStatus.Partial,
            CompletedExerciseInstanceIds = [exerciseId1],  // exerciseId2 missing → NOT complete
            Version = 1,
            DateCreated = DateTime.UtcNow
        };

        var mongo = CreateMockMongo(plan, completedLogCount: 0, completionExecutions: [partialCompletionExecution]);
        var lockService = LockServiceAcquired(sessionId, planId);
        var notifier = Substitute.For<IRealtimeNotifier>();

        var ep = Factory.Create<UnlockTrainingSessionEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(_trainerId, AppRoles.Trainer))),
            new MockDbBuilder().Build(), mongo, lockService, DefaultOptions(), notifier,
            EndpointTestHelpers.CreateGrantingLinkAuthorizationService());

        // Act
        await ep.HandleAsync(
            new UnlockTrainingSessionRequest { PlanId = planId, SessionId = sessionId },
            TestContext.Current.CancellationToken);

        // Assert: 204 — partial completion does not block unlock
        ep.HttpContext.Response.StatusCode.Should().Be(204,
            "a partial TrainingCompletion must not trigger the finished-guard");

        await lockService.Received(1).AcquireAsync(
            sessionId, planId, _clientId, _trainerId,
            LockHolder.Coach, LockType.Editing, Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
    }

    // ── Per-section path tests (Defect 1 regression) ────────────────────────────

    /// <summary>
    /// Regression test for Defect 1: the same catalog exercise (<c>ExerciseExternalId</c>) appears
    /// in two different workouts (e.g. "Bench Press" scheduled in both AMRAP block A and AMRAP
    /// block B), but each occurrence has its own distinct <c>ExerciseId</c> instance identifier
    /// (#857 phase 3b). The client completed only section A's instance.
    ///
    /// Before instance ids existed, a flat catalog-id check (CompletedExerciseIds.Contains) would
    /// see the shared exercise id once and treat BOTH sections as done → false-positive "session
    /// complete" → wrong 409 on unlock. Now that CompletedExerciseInstanceIds holds instance ids,
    /// a flat membership check is inherently per-occurrence-correct — no per-section dictionary
    /// is needed to disambiguate.
    /// </summary>
    [Fact]
    public async Task Unlock_DuplicateExerciseAcrossSections_CompletedInOnlyOneSection_Returns204()
    {
        var planId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var plan = CreatePlan(planId, sessionId);

        var sectionIdA = Guid.NewGuid();
        var sectionIdB = Guid.NewGuid();
        var sharedExerciseId = Guid.NewGuid(); // same catalog exercise in both sections
        var sectionAInstanceId = Guid.NewGuid();
        var sectionBInstanceId = Guid.NewGuid();

        plan.Weeks[0].Days.SelectMany(d => d.Sessions).First().Workouts =
        [
            new TrainingWorkout
            {
                WorkoutId = sectionIdA,
                Order = 0,
                Name = "AMRAP A",
                Exercises =
                [
                    new SessionExercise { ExerciseId = sectionAInstanceId, ExerciseExternalId = sharedExerciseId, ExerciseName = "Bench Press", Order = 0, Sets = [] }
                ]
            },
            new TrainingWorkout
            {
                WorkoutId = sectionIdB,
                Order = 1,
                Name = "AMRAP B",
                Exercises =
                [
                    new SessionExercise { ExerciseId = sectionBInstanceId, ExerciseExternalId = sharedExerciseId, ExerciseName = "Bench Press", Order = 0, Sets = [] }
                ]
            }
        ];

        // Client completed section A's instance but NOT section B's — CompletedExerciseInstanceIds
        // only contains sectionAInstanceId.
        var completionExecution = new SessionExecution
        {
            ExternalId = Guid.NewGuid(),
            ClientId = _clientId,
            SessionId = sessionId,
            Date = DateTime.UtcNow.Date,
            Status = SessionExecutionStatus.Partial,
            CompletedExerciseInstanceIds = [sectionAInstanceId],
            Version = 1,
            DateCreated = DateTime.UtcNow
        };

        // No completed live session.
        var mongo = CreateMockMongo(plan, completedLogCount: 0, completionExecutions: [completionExecution]);
        var lockService = LockServiceAcquired(sessionId, planId);
        var notifier = Substitute.For<IRealtimeNotifier>();

        var ep = Factory.Create<UnlockTrainingSessionEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(_trainerId, AppRoles.Trainer))),
            new MockDbBuilder().Build(), mongo, lockService, DefaultOptions(), notifier,
            EndpointTestHelpers.CreateGrantingLinkAuthorizationService());

        // Act
        await ep.HandleAsync(
            new UnlockTrainingSessionRequest { PlanId = planId, SessionId = sessionId },
            TestContext.Current.CancellationToken);

        // Assert: session B still incomplete — unlock must proceed, no 409.
        ep.HttpContext.Response.StatusCode.Should().Be(204,
            "session with duplicate exercise id completed in only one section must NOT be treated as finished");

        await lockService.Received(1).AcquireAsync(
            sessionId, planId, _clientId, _trainerId,
            LockHolder.Coach, LockType.Editing, Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// A completion whose flat <c>CompletedExerciseInstanceIds</c> covers every workout's exercise
    /// instance across both workouts should be treated as complete and must block the unlock
    /// with 409.
    /// </summary>
    [Fact]
    public async Task Unlock_AllSectionsCompleteViaBySection_Returns409()
    {
        var planId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var plan = CreatePlan(planId, sessionId);

        var sectionIdA = Guid.NewGuid();
        var sectionIdB = Guid.NewGuid();
        var exerciseId1 = Guid.NewGuid();
        var exerciseId2 = Guid.NewGuid();

        plan.Weeks[0].Days.SelectMany(d => d.Sessions).First().Workouts =
        [
            new TrainingWorkout
            {
                WorkoutId = sectionIdA,
                Order = 0,
                Name = "Section A",
                Exercises =
                [
                    new SessionExercise { ExerciseId = exerciseId1, ExerciseExternalId = exerciseId1, ExerciseName = "Squat", Order = 0, Sets = [] }
                ]
            },
            new TrainingWorkout
            {
                WorkoutId = sectionIdB,
                Order = 1,
                Name = "Section B",
                Exercises =
                [
                    new SessionExercise { ExerciseId = exerciseId2, ExerciseExternalId = exerciseId2, ExerciseName = "Deadlift", Order = 0, Sets = [] }
                ]
            }
        ];

        // Both workouts fully done — every exercise instance across both workouts is present in
        // the flat CompletedExerciseInstanceIds list.
        var completionExecution = new SessionExecution
        {
            ExternalId = Guid.NewGuid(),
            ClientId = _clientId,
            SessionId = sessionId,
            Date = DateTime.UtcNow.Date,
            Status = SessionExecutionStatus.Partial,
            CompletedExerciseInstanceIds = [exerciseId1, exerciseId2],
            Version = 1,
            DateCreated = DateTime.UtcNow
        };

        // No completed live session.
        var mongo = CreateMockMongo(plan, completedLogCount: 0, completionExecutions: [completionExecution]);
        var lockService = LockServiceAcquired(sessionId, planId);
        var notifier = Substitute.For<IRealtimeNotifier>();

        var ep = Factory.Create<UnlockTrainingSessionEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(_trainerId, AppRoles.Trainer))),
            new MockDbBuilder().Build(), mongo, lockService, DefaultOptions(), notifier,
            EndpointTestHelpers.CreateGrantingLinkAuthorizationService());

        // Act
        await ep.HandleAsync(
            new UnlockTrainingSessionRequest { PlanId = planId, SessionId = sessionId },
            TestContext.Current.CancellationToken);

        // Assert: both workouts done via the flat instance list → 409
        ep.HttpContext.Response.StatusCode.Should().Be(409,
            "a fully-complete CompletedExerciseInstanceIds completion must block the unlock");

        // Lock must NOT be acquired
        await lockService.DidNotReceive().AcquireAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(),
            Arg.Any<LockHolder>(), Arg.Any<LockType>(), Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
    }
}
