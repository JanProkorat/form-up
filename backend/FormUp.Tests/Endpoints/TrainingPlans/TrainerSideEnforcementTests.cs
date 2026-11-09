using System.Security.Claims;
using FastEndpoints;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.TrainingPlans.RelockTrainingSession;
using FormUp.Application.Features.TrainingPlans.UnlockTrainingSession;
using FormUp.Application.Features.TrainingPlans.UpdateTrainingPlan;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Application.Infrastructure.Services;
using FormUp.Tests.Builders;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using NSubstitute;

namespace FormUp.Tests.Endpoints.TrainingPlans;

/// <summary>
/// Tests for the trainer-side enforcement: unlock/relock endpoints and diff-gated plan edit.
/// Issue #381 — covers:
///   - Unlock 409 when session is Live
///   - Unlock 404 when plan not owned by caller
///   - Diff-gate rejects edit to a Stable/Live published session (no Editing lock)
///   - Diff-gate allows edit to a published session that is in Editing by this trainer
///   - Auto-release of Editing locks after a successful save
///   - Draft-week edit is never gated (no lock required)
///   - Relock is idempotent (succeeds even when lock already gone)
/// </summary>
public class TrainerSideEnforcementTests
{
    private readonly Guid _trainerId = Guid.NewGuid();
    private readonly Guid _otherTrainerId = Guid.NewGuid();

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private IOptions<TrainingLockOptions> DefaultOptions() =>
        Options.Create(new TrainingLockOptions { EditingTtlHours = 2, LiveTtlHours = 6 });

    /// <summary>
    /// Creates a plan with one published week containing a session with <paramref name="sessionId"/>.
    /// The session has a single section with one exercise and one set.
    /// </summary>
    private TrainingPlan CreatePlanWithPublishedSession(
        Guid sessionId,
        Guid? trainerId = null,
        Guid? exerciseId = null)
    {
        var tid = trainerId ?? _trainerId;
        var exId = exerciseId ?? Guid.NewGuid();
        return new TrainingPlan
        {
            ExternalId = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            TrainerId = tid,
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
                            Name = "Push Day",
                            Order = 1,
                            Workouts =
                            [
                                new TrainingWorkout
                                {
                                    WorkoutId = Guid.NewGuid(),
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
                                                new ExerciseSet { SetNumber = 1, Reps = 10, WeightKg = 100 }
                                            ]
                                        }
                                    ]
                                }
                            ]
                        }))
                }
            ],
            Version = 1,
            DateCreated = DateTime.UtcNow.AddDays(-30)
        };
    }

    /// <summary>
    /// Creates a plan with one DRAFT week containing a session with <paramref name="sessionId"/>.
    /// </summary>
    private TrainingPlan CreatePlanWithDraftSession(Guid sessionId, Guid? exerciseId = null)
    {
        var exId = exerciseId ?? Guid.NewGuid();
        return new TrainingPlan
        {
            ExternalId = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            TrainerId = _trainerId,
            Name = "Draft Plan",
            Status = TrainingPlanStatus.Draft,
            Weeks =
            [
                new TrainingWeek
                {
                    WeekNumber = 1,
                    Status = WeekStatus.Draft,
                    Days = TrainingPlanTestHelpers.MaterializeDays(
(1, new TrainingSession
                        {
                            SessionId = sessionId,
                            Name = "Draft Session",
                            Order = 1,
                            Workouts =
                            [
                                new TrainingWorkout
                                {
                                    WorkoutId = Guid.NewGuid(),
                                    Order = 0,
                                    Name = "Hlavní",
                                    Exercises =
                                    [
                                        new SessionExercise
                                        {
                                            ExerciseExternalId = exId,
                                            ExerciseName = "Squat",
                                            Order = 1,
                                            MovementType = MovementType.Reps,
                                            Sets =
                                            [
                                                new ExerciseSet { SetNumber = 1, Reps = 5, WeightKg = 80 }
                                            ]
                                        }
                                    ]
                                }
                            ]
                        }))
                }
            ],
            Version = 1,
            DateCreated = DateTime.UtcNow.AddDays(-30)
        };
    }

    /// <summary>
    /// Builds the incoming session request with identical content to a published session
    /// (no content changes → diff should detect no diff).
    /// </summary>
    private static UpdateSessionRequest IdenticalSessionRequest(TrainingSession session, Guid sessionId)
    {
        var section = session.Workouts[0];
        var exercise = section.Exercises[0];
        var set = exercise.Sets[0];
        return new UpdateSessionRequest
        {
            SessionId = sessionId,
            DayOfWeek = 1, // both CreatePlanWithPublishedSession and CreatePlanWithDraftSession schedule day 1
            Name = session.Name,
            Order = session.Order,
            Notes = session.Notes,
            Format = session.Format,
            FormatConfig = session.FormatConfig,
            Workouts =
            [
                new UpdateTrainingWorkoutRequest
                {
                    WorkoutId = section.WorkoutId,
                    Order = section.Order,
                    Name = section.Name,
                    Format = section.Format,
                    FormatConfig = section.FormatConfig,
                    Notes = section.Notes,
                    Exercises =
                    [
                        new UpdateSessionExerciseRequest
                        {
                            ExerciseExternalId = exercise.ExerciseExternalId,
                            ExerciseName = exercise.ExerciseName,
                            Order = exercise.Order,
                            Notes = exercise.Notes,
                            RestSeconds = exercise.RestSeconds,
                            MovementType = exercise.MovementType,
                            Format = exercise.Format,
                            FormatConfig = exercise.FormatConfig,
                            Sets =
                            [
                                new UpdateExerciseSetRequest
                                {
                                    SetNumber = set.SetNumber,
                                    Type = set.Type,
                                    Reps = set.Reps,
                                    WeightKg = set.WeightKg,
                                    DurationSeconds = set.DurationSeconds,
                                    Rpe = set.Rpe,
                                    DistanceMeters = set.DistanceMeters,
                                    RestSeconds = set.RestSeconds
                                }
                            ]
                        }
                    ]
                }
            ]
        };
    }

    /// <summary>
    /// Builds the incoming session request with a content change (different reps).
    /// </summary>
    private static UpdateSessionRequest ChangedSessionRequest(TrainingSession session, Guid sessionId)
    {
        var req = IdenticalSessionRequest(session, sessionId);
        // Mutate reps to trigger the diff gate.
        req.Workouts[0].Exercises[0].Sets[0].Reps = 99;
        return req;
    }

    private ISessionLockService CreateLockServiceReturningConflict()
    {
        var svc = Substitute.For<ISessionLockService>();
        svc.AcquireAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(),
                Arg.Any<LockHolder>(), Arg.Any<LockType>(), Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new AcquireResult.LockConflict());
        svc.GetStateAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<SessionLock>());
        svc.ReleaseAsync(Arg.Any<Guid>(), Arg.Any<LockHolder>(), Arg.Any<LockType>(), Arg.Any<CancellationToken>())
            .Returns(false);
        return svc;
    }

    private ISessionLockService CreateLockServiceReturningAcquired(Guid sessionId, Guid planId, Guid clientId, Guid trainerId)
    {
        var svc = Substitute.For<ISessionLockService>();
        var lockDoc = new SessionLock
        {
            SessionId = sessionId,
            PlanId = planId,
            ClientId = clientId,
            TrainerId = trainerId,
            Holder = LockHolder.Coach,
            Type = LockType.Editing,
            AcquiredAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(2)
        };
        svc.AcquireAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(),
                Arg.Any<LockHolder>(), Arg.Any<LockType>(), Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new AcquireResult.Acquired(lockDoc));
        svc.GetStateAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<SessionLock> { lockDoc });
        svc.ReleaseAsync(Arg.Any<Guid>(), Arg.Any<LockHolder>(), Arg.Any<LockType>(), Arg.Any<CancellationToken>())
            .Returns(true);
        return svc;
    }

    private ISessionLockService CreateLockServiceWithNoLocks()
    {
        var svc = Substitute.For<ISessionLockService>();
        svc.AcquireAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(),
                Arg.Any<LockHolder>(), Arg.Any<LockType>(), Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new AcquireResult.Acquired(new SessionLock()));
        svc.GetStateAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<SessionLock>());
        svc.ReleaseAsync(Arg.Any<Guid>(), Arg.Any<LockHolder>(), Arg.Any<LockType>(), Arg.Any<CancellationToken>())
            .Returns(false);
        return svc;
    }

    // ── Unlock: 409 when session is Live ─────────────────────────────────────────

    [Fact]
    public async Task Unlock_Returns409_WhenSessionIsLive()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var plan = CreatePlanWithPublishedSession(sessionId);
        var mongo = TrainingPlanTestHelpers.CreateMockMongo(plan);
        var lockService = CreateLockServiceReturningConflict();

        var ep = Factory.Create<UnlockTrainingSessionEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(_trainerId, AppRoles.Trainer))),
            new MockDbBuilder().Build(), mongo, lockService, DefaultOptions(), Substitute.For<IRealtimeNotifier>(),
            EndpointTestHelpers.CreateGrantingLinkAuthorizationService());

        // Act
        await ep.HandleAsync(
            new UnlockTrainingSessionRequest { PlanId = plan.ExternalId, SessionId = sessionId },
            TestContext.Current.CancellationToken);

        // Assert — AcquireAsync returned LockConflict → 409
        ep.HttpContext.Response.StatusCode.Should().Be(409);
    }

    // ── Unlock: 404 when plan not owned by caller ─────────────────────────────────

    [Fact]
    public async Task Unlock_Returns404_WhenPlanNotOwnedByCaller()
    {
        // Arrange — plan owned by _otherTrainerId, caller is _trainerId
        var sessionId = Guid.NewGuid();
        var plan = CreatePlanWithPublishedSession(sessionId, trainerId: _otherTrainerId);
        // Return empty so the ownership filter finds nothing
        var mongo = TrainingPlanTestHelpers.CreateMockMongo();
        var lockService = CreateLockServiceWithNoLocks();

        var ep = Factory.Create<UnlockTrainingSessionEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(_trainerId, AppRoles.Trainer))),
            new MockDbBuilder().Build(), mongo, lockService, DefaultOptions(), Substitute.For<IRealtimeNotifier>(),
            EndpointTestHelpers.CreateGrantingLinkAuthorizationService());

        // Act
        await ep.HandleAsync(
            new UnlockTrainingSessionRequest { PlanId = plan.ExternalId, SessionId = sessionId },
            TestContext.Current.CancellationToken);

        // Assert
        ep.HttpContext.Response.StatusCode.Should().Be(404);

        // Lock service must not have been called
        await lockService.DidNotReceive().AcquireAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(),
            Arg.Any<LockHolder>(), Arg.Any<LockType>(), Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
    }

    // ── Unlock: deny-path for the link-authorization guard itself (not authorship) ────

    /// <summary>
    /// The plan is owned by the caller, but the caller's link to the plan's client no longer
    /// grants training access — this must still 404, distinct from
    /// <see cref="Unlock_Returns404_WhenPlanNotOwnedByCaller"/> which denies on authorship.
    /// </summary>
    [Fact]
    public async Task Unlock_Returns404_WhenNotLinkedToClient()
    {
        var sessionId = Guid.NewGuid();
        var plan = CreatePlanWithPublishedSession(sessionId);
        var mongo = TrainingPlanTestHelpers.CreateMockMongo(plan);
        var lockService = CreateLockServiceWithNoLocks();

        var ep = Factory.Create<UnlockTrainingSessionEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(_trainerId, AppRoles.Trainer))),
            new MockDbBuilder().Build(), mongo, lockService, DefaultOptions(), Substitute.For<IRealtimeNotifier>(),
            TrainingPlanTestHelpers.CreateDenyingLinkAuthorizationService());

        await ep.HandleAsync(
            new UnlockTrainingSessionRequest { PlanId = plan.ExternalId, SessionId = sessionId },
            TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(404);

        await lockService.DidNotReceive().AcquireAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(),
            Arg.Any<LockHolder>(), Arg.Any<LockType>(), Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Flag-inversion deny test: the link is active and exists, but grants only the nutrition
    /// domain. A "no link" deny test cannot detect a guard that checks the wrong flag, since
    /// both flags are absent either way — this pins the guard to
    /// <c>CanViewTrainingPlans</c> specifically.
    /// </summary>
    [Fact]
    public async Task Unlock_Returns404_WhenLinkGrantsOnlyNutrition()
    {
        var sessionId = Guid.NewGuid();
        var plan = CreatePlanWithPublishedSession(sessionId);
        var mongo = TrainingPlanTestHelpers.CreateMockMongo(plan);
        var lockService = CreateLockServiceWithNoLocks();

        var ep = Factory.Create<UnlockTrainingSessionEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(_trainerId, AppRoles.Trainer))),
            new MockDbBuilder().Build(), mongo, lockService, DefaultOptions(), Substitute.For<IRealtimeNotifier>(),
            EndpointTestHelpers.CreateGrantingLinkAuthorizationService(
                canViewNutritionPlans: true, canViewTrainingPlans: false));

        await ep.HandleAsync(
            new UnlockTrainingSessionRequest { PlanId = plan.ExternalId, SessionId = sessionId },
            TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(404);

        await lockService.DidNotReceive().AcquireAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(),
            Arg.Any<LockHolder>(), Arg.Any<LockType>(), Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
    }

    // ── Diff-gate: rejects edit to a Stable published session ─────────────────────

    [Fact]
    public async Task UpdatePlan_DiffGate_Returns409_WhenPublishedSessionChangedWithoutEditingLock()
    {
        // Arrange — session is Stable (no lock held), but request contains a content change.
        var sessionId = Guid.NewGuid();
        var plan = CreatePlanWithPublishedSession(sessionId);
        var mongo = TrainingPlanTestHelpers.CreateMockMongo(plan);
        var lockService = CreateLockServiceWithNoLocks(); // GetStateAsync returns empty = no lock

        var ep = Factory.Create<UpdateTrainingPlanEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(_trainerId, AppRoles.Trainer))),
            mongo, lockService, Substitute.For<IRealtimeNotifier>(), new PlanConcurrencyGuard(), new MockDbBuilder().Build(),
            EndpointTestHelpers.CreateGrantingLinkAuthorizationService());

        var changedSession = ChangedSessionRequest(plan.Weeks[0].Days.SelectMany(d => d.Sessions).First(), sessionId);
        var req = new UpdateTrainingPlanRequest
        {
            PlanId = plan.ExternalId,
            Name = plan.Name,
            Version = plan.Version,
            StartDate = plan.StartDate, // preserve so start-date lock check passes
            Weeks =
            [
                new UpdateTrainingWeekRequest
                {
                    WeekNumber = 1,
                    Sessions = [changedSession]
                }
            ]
        };

        // Act
        await ep.HandleAsync(req, TestContext.Current.CancellationToken);

        // Assert — diff detected a change and no Editing lock → 409
        ep.HttpContext.Response.StatusCode.Should().Be(409);

        // Plan must NOT have been saved
        await mongo.TrainingPlans.DidNotReceive().ReplaceOneAsync(
            Arg.Any<FilterDefinition<TrainingPlan>>(),
            Arg.Any<TrainingPlan>(),
            Arg.Any<ReplaceOptions>(),
            Arg.Any<CancellationToken>());
    }

    // ── Diff-gate: allows edit to a published session that is in Editing by this trainer ──

    [Fact]
    public async Task UpdatePlan_DiffGate_Allows_WhenPublishedSessionInEditingByThisTrainer()
    {
        // Arrange — session is Editing by this trainer.
        var sessionId = Guid.NewGuid();
        var plan = CreatePlanWithPublishedSession(sessionId);
        var mongo = TrainingPlanTestHelpers.CreateMockMongo(plan);
        var lockService = CreateLockServiceReturningAcquired(sessionId, plan.ExternalId, plan.ClientId, _trainerId);

        var ep = Factory.Create<UpdateTrainingPlanEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(_trainerId, AppRoles.Trainer))),
            mongo, lockService, Substitute.For<IRealtimeNotifier>(), new PlanConcurrencyGuard(), new MockDbBuilder().Build(),
            EndpointTestHelpers.CreateGrantingLinkAuthorizationService());

        var changedSession = ChangedSessionRequest(plan.Weeks[0].Days.SelectMany(d => d.Sessions).First(), sessionId);
        var req = new UpdateTrainingPlanRequest
        {
            PlanId = plan.ExternalId,
            Name = plan.Name,
            Version = plan.Version,
            StartDate = plan.StartDate, // preserve so start-date lock check passes
            Weeks =
            [
                new UpdateTrainingWeekRequest
                {
                    WeekNumber = 1,
                    Sessions = [changedSession]
                }
            ]
        };

        // Act
        await ep.HandleAsync(req, TestContext.Current.CancellationToken);

        // Assert — lock held by this trainer → save proceeds → 200
        ep.HttpContext.Response.StatusCode.Should().Be(200);

        await mongo.TrainingPlans.Received(1).ReplaceOneAsync(
            Arg.Any<FilterDefinition<TrainingPlan>>(),
            Arg.Any<TrainingPlan>(),
            Arg.Any<ReplaceOptions>(),
            Arg.Any<CancellationToken>());
    }

    // ── Auto-release: Editing locks released after successful save ────────────────

    [Fact]
    public async Task UpdatePlan_AutoReleasesEditingLock_AfterSuccessfulSave()
    {
        // Arrange — session in Editing by this trainer; save succeeds; lock must be released.
        var sessionId = Guid.NewGuid();
        var plan = CreatePlanWithPublishedSession(sessionId);
        var mongo = TrainingPlanTestHelpers.CreateMockMongo(plan);
        var lockService = CreateLockServiceReturningAcquired(sessionId, plan.ExternalId, plan.ClientId, _trainerId);

        var ep = Factory.Create<UpdateTrainingPlanEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(_trainerId, AppRoles.Trainer))),
            mongo, lockService, Substitute.For<IRealtimeNotifier>(), new PlanConcurrencyGuard(), new MockDbBuilder().Build(),
            EndpointTestHelpers.CreateGrantingLinkAuthorizationService());

        var changedSession = ChangedSessionRequest(plan.Weeks[0].Days.SelectMany(d => d.Sessions).First(), sessionId);
        var req = new UpdateTrainingPlanRequest
        {
            PlanId = plan.ExternalId,
            Name = plan.Name,
            Version = plan.Version,
            StartDate = plan.StartDate, // preserve so start-date lock check passes
            Weeks =
            [
                new UpdateTrainingWeekRequest
                {
                    WeekNumber = 1,
                    Sessions = [changedSession]
                }
            ]
        };

        // Act
        await ep.HandleAsync(req, TestContext.Current.CancellationToken);

        // Assert — save succeeded (200) and lock was released.
        ep.HttpContext.Response.StatusCode.Should().Be(200);
        await lockService.Received(1).ReleaseAsync(
            sessionId, LockHolder.Coach, LockType.Editing, Arg.Any<CancellationToken>());
    }

    // ── Draft-week edit is never gated ───────────────────────────────────────────

    [Fact]
    public async Task UpdatePlan_DraftWeekEdit_IsNotGated_Returns200()
    {
        // Arrange — plan has only a draft week; no lock needed even for content changes.
        var sessionId = Guid.NewGuid();
        var exerciseId = Guid.NewGuid();
        var plan = CreatePlanWithDraftSession(sessionId, exerciseId);
        var mongo = TrainingPlanTestHelpers.CreateMockMongo(plan);
        // Lock service should NOT be queried for draft-week sessions.
        var lockService = Substitute.For<ISessionLockService>();
        lockService.GetStateAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<SessionLock>());

        var ep = Factory.Create<UpdateTrainingPlanEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(_trainerId, AppRoles.Trainer))),
            mongo, lockService, Substitute.For<IRealtimeNotifier>(), new PlanConcurrencyGuard(), new MockDbBuilder().Build(),
            EndpointTestHelpers.CreateGrantingLinkAuthorizationService());

        // Request changes the reps — but this is a draft session, so no gate.
        var req = new UpdateTrainingPlanRequest
        {
            PlanId = plan.ExternalId,
            Name = plan.Name,
            Version = plan.Version,
            Weeks =
            [
                new UpdateTrainingWeekRequest
                {
                    WeekNumber = 1,
                    Sessions =
                    [
                        new UpdateSessionRequest
                        {
                            SessionId = sessionId,
                            DayOfWeek = 1,
                            Name = "Draft Session",
                            Order = 1,
                            Workouts =
                            [
                                new UpdateTrainingWorkoutRequest
                                {
                                    Order = 0,
                                    Name = "Hlavní",
                                    Exercises =
                                    [
                                        new UpdateSessionExerciseRequest
                                        {
                                            ExerciseExternalId = exerciseId,
                                            ExerciseName = "Squat",
                                            Order = 1,
                                            MovementType = MovementType.Reps,
                                            Sets =
                                            [
                                                new UpdateExerciseSetRequest
                                                {
                                                    SetNumber = 1,
                                                    Reps = 99, // changed — but draft week, so no gate
                                                    WeightKg = 80
                                                }
                                            ]
                                        }
                                    ]
                                }
                            ]
                        }
                    ]
                }
            ]
        };

        // Act
        await ep.HandleAsync(req, TestContext.Current.CancellationToken);

        // Assert — no gate for draft weeks → 200
        ep.HttpContext.Response.StatusCode.Should().Be(200);

        // GetStateAsync must NOT be called since no published sessions were changed.
        await lockService.DidNotReceive().GetStateAsync(
            Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>());
    }

    // ── Relock: idempotent success even when lock already gone ───────────────────

    [Fact]
    public async Task Relock_Returns204_WhenLockAlreadyReleased()
    {
        // Arrange — lock already expired/released; ReleaseAsync returns false (idempotent).
        var sessionId = Guid.NewGuid();
        var plan = CreatePlanWithPublishedSession(sessionId);
        var mongo = TrainingPlanTestHelpers.CreateMockMongo(plan);
        var lockService = CreateLockServiceWithNoLocks(); // ReleaseAsync returns false

        var ep = Factory.Create<RelockTrainingSessionEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(_trainerId, AppRoles.Trainer))),
            new MockDbBuilder().Build(), mongo, lockService, Substitute.For<IRealtimeNotifier>(),
            EndpointTestHelpers.CreateGrantingLinkAuthorizationService());

        // Act
        await ep.HandleAsync(
            new RelockTrainingSessionRequest { PlanId = plan.ExternalId, SessionId = sessionId },
            TestContext.Current.CancellationToken);

        // Assert — 204 regardless of whether a lock existed
        ep.HttpContext.Response.StatusCode.Should().Be(204);
    }

    // ── Relock: 404 when plan not owned by caller ─────────────────────────────────

    [Fact]
    public async Task Relock_Returns404_WhenPlanNotOwnedByCaller()
    {
        // Arrange — plan exists but belongs to _otherTrainerId; caller is _trainerId.
        var sessionId = Guid.NewGuid();
        // Return empty plans to simulate ownership filter finding nothing.
        var mongo = TrainingPlanTestHelpers.CreateMockMongo();
        var lockService = CreateLockServiceWithNoLocks();

        var ep = Factory.Create<RelockTrainingSessionEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(_trainerId, AppRoles.Trainer))),
            new MockDbBuilder().Build(), mongo, lockService, Substitute.For<IRealtimeNotifier>(),
            EndpointTestHelpers.CreateGrantingLinkAuthorizationService());

        // Act
        await ep.HandleAsync(
            new RelockTrainingSessionRequest { PlanId = Guid.NewGuid(), SessionId = sessionId },
            TestContext.Current.CancellationToken);

        // Assert
        ep.HttpContext.Response.StatusCode.Should().Be(404);

        // ReleaseAsync must not have been called
        await lockService.DidNotReceive().ReleaseAsync(
            Arg.Any<Guid>(), Arg.Any<LockHolder>(), Arg.Any<LockType>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The plan is owned by the caller, but the caller's link to the plan's client no longer
    /// grants training access — this must still 404, distinct from
    /// <see cref="Relock_Returns404_WhenPlanNotOwnedByCaller"/> which denies on authorship.
    /// </summary>
    [Fact]
    public async Task Relock_Returns404_WhenNotLinkedToClient()
    {
        var sessionId = Guid.NewGuid();
        var plan = CreatePlanWithPublishedSession(sessionId);
        var mongo = TrainingPlanTestHelpers.CreateMockMongo(plan);
        var lockService = CreateLockServiceWithNoLocks();

        var ep = Factory.Create<RelockTrainingSessionEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(_trainerId, AppRoles.Trainer))),
            new MockDbBuilder().Build(), mongo, lockService, Substitute.For<IRealtimeNotifier>(),
            TrainingPlanTestHelpers.CreateDenyingLinkAuthorizationService());

        await ep.HandleAsync(
            new RelockTrainingSessionRequest { PlanId = plan.ExternalId, SessionId = sessionId },
            TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(404);

        await lockService.DidNotReceive().ReleaseAsync(
            Arg.Any<Guid>(), Arg.Any<LockHolder>(), Arg.Any<LockType>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Flag-inversion deny test: the link is active and exists, but grants only the nutrition
    /// domain. A "no link" deny test cannot detect a guard that checks the wrong flag, since
    /// both flags are absent either way — this pins the guard to
    /// <c>CanViewTrainingPlans</c> specifically.
    /// </summary>
    [Fact]
    public async Task Relock_Returns404_WhenLinkGrantsOnlyNutrition()
    {
        var sessionId = Guid.NewGuid();
        var plan = CreatePlanWithPublishedSession(sessionId);
        var mongo = TrainingPlanTestHelpers.CreateMockMongo(plan);
        var lockService = CreateLockServiceWithNoLocks();

        var ep = Factory.Create<RelockTrainingSessionEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(_trainerId, AppRoles.Trainer))),
            new MockDbBuilder().Build(), mongo, lockService, Substitute.For<IRealtimeNotifier>(),
            EndpointTestHelpers.CreateGrantingLinkAuthorizationService(
                canViewNutritionPlans: true, canViewTrainingPlans: false));

        await ep.HandleAsync(
            new RelockTrainingSessionRequest { PlanId = plan.ExternalId, SessionId = sessionId },
            TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(404);

        await lockService.DidNotReceive().ReleaseAsync(
            Arg.Any<Guid>(), Arg.Any<LockHolder>(), Arg.Any<LockType>(), Arg.Any<CancellationToken>());
    }

    // ── Diff-gate: no content change → no lock required ─────────────────────────

    [Fact]
    public async Task UpdatePlan_NoContentChange_Returns200_WithoutRequiringLock()
    {
        // Arrange — published session with identical content (no diff detected).
        var sessionId = Guid.NewGuid();
        var plan = CreatePlanWithPublishedSession(sessionId);
        var mongo = TrainingPlanTestHelpers.CreateMockMongo(plan);
        var lockService = CreateLockServiceWithNoLocks(); // no locks held

        var ep = Factory.Create<UpdateTrainingPlanEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(_trainerId, AppRoles.Trainer))),
            mongo, lockService, Substitute.For<IRealtimeNotifier>(), new PlanConcurrencyGuard(), new MockDbBuilder().Build(),
            EndpointTestHelpers.CreateGrantingLinkAuthorizationService());

        var identicalSession = IdenticalSessionRequest(plan.Weeks[0].Days.SelectMany(d => d.Sessions).First(), sessionId);
        var req = new UpdateTrainingPlanRequest
        {
            PlanId = plan.ExternalId,
            Name = plan.Name,
            Version = plan.Version,
            StartDate = plan.StartDate, // preserve so start-date lock check passes
            Weeks =
            [
                new UpdateTrainingWeekRequest
                {
                    WeekNumber = 1,
                    Sessions = [identicalSession]
                }
            ]
        };

        // Act
        await ep.HandleAsync(req, TestContext.Current.CancellationToken);

        // Assert — no diff detected → no lock needed → 200
        ep.HttpContext.Response.StatusCode.Should().Be(200);

        // GetStateAsync must NOT be called since there are no changed sessions.
        await lockService.DidNotReceive().GetStateAsync(
            Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>());
    }
}
