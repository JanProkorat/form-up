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
using FormUp.Tests.Endpoints;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace FormUp.Tests.Endpoints.TrainingPlans;

/// <summary>
/// Tests that verify the <c>sessioneditlockchanged</c> SignalR broadcast behaviour
/// for the trainer-side session-lock endpoints (issue #383).
/// Covers:
///   - Unlock (Editing acquire) emits state=Editing to BOTH clientId and trainerId
///   - Relock (Editing release, ReleaseAsync=true) emits state=Stable to BOTH parties
///   - Relock idempotent (ReleaseAsync=false) emits NOTHING
///   - UpdatePlan diff-gated auto-release emits state=Stable to BOTH parties
///   - UpdatePlan diff-gated auto-release skips emit when ReleaseAsync=false
///   - Unlock 409 conflict emits NOTHING
/// </summary>
public class SessionLockBroadcastTests
{
    private readonly Guid _trainerId = Guid.NewGuid();
    private readonly Guid _clientId = Guid.NewGuid();

    private IOptions<TrainingLockOptions> DefaultOptions() =>
        Options.Create(new TrainingLockOptions { EditingTtlHours = 2, LiveTtlHours = 6 });

    // ── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a plan with one published week containing a session with <paramref name="sessionId"/>.
    /// </summary>
    private TrainingPlan CreatePlanWithPublishedSession(Guid sessionId)
    {
        var exerciseId = Guid.NewGuid();
        return new TrainingPlan
        {
            ExternalId = Guid.NewGuid(),
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
                    Days = TrainingPlanTestHelpers.MaterializeDays((1, new TrainingSession
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
                                        ExerciseExternalId = exerciseId,
                                        ExerciseName = "Bench Press",
                                        Order = 1,
                                        MovementType = MovementType.Reps,
                                        Sets = [new ExerciseSet { SetNumber = 1, Reps = 10, WeightKg = 100 }]
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

    /// <summary>Creates a lock service that returns <see cref="AcquireResult.Acquired"/>.</summary>
    private ISessionLockService LockServiceAcquired(Guid sessionId, Guid planId)
    {
        var svc = Substitute.For<ISessionLockService>();
        var lockDoc = new SessionLock
        {
            SessionId = sessionId,
            PlanId = planId,
            ClientId = _clientId,
            TrainerId = _trainerId,
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

    /// <summary>Creates a lock service that returns <see cref="AcquireResult.LockConflict"/>.</summary>
    private static ISessionLockService LockServiceConflict()
    {
        var svc = Substitute.For<ISessionLockService>();
        svc.AcquireAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(),
                Arg.Any<LockHolder>(), Arg.Any<LockType>(), Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(new AcquireResult.LockConflict());
        svc.ReleaseAsync(Arg.Any<Guid>(), Arg.Any<LockHolder>(), Arg.Any<LockType>(), Arg.Any<CancellationToken>())
            .Returns(false);
        return svc;
    }

    /// <summary>
    /// Creates a lock service where GetStateAsync returns an active lock (so diff-gate allows the save),
    /// but ReleaseAsync returns false (lock was already gone — idempotent no-op).
    /// </summary>
    private ISessionLockService LockServiceNoLock(Guid sessionId, Guid planId)
    {
        var svc = Substitute.For<ISessionLockService>();
        var lockDoc = new SessionLock
        {
            SessionId = sessionId,
            PlanId = planId,
            ClientId = _clientId,
            TrainerId = _trainerId,
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
        // GetStateAsync returns a lock so the diff-gate sees an active Editing lock and allows the save
        svc.GetStateAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<SessionLock> { lockDoc });
        // ReleaseAsync returns false — lock was already gone (expired between the gate check and release)
        svc.ReleaseAsync(Arg.Any<Guid>(), Arg.Any<LockHolder>(), Arg.Any<LockType>(), Arg.Any<CancellationToken>())
            .Returns(false);
        return svc;
    }

    private static UpdateSessionRequest ChangedSessionRequest(int dayOfWeek, TrainingSession session, Guid sessionId)
    {
        var section = session.Workouts[0];
        var exercise = section.Exercises[0];
        var set = exercise.Sets[0];
        return new UpdateSessionRequest
        {
            SessionId = sessionId,
            DayOfWeek = dayOfWeek,
            Name = session.Name,
            Order = session.Order,
            Workouts =
            [
                new UpdateTrainingWorkoutRequest
                {
                    WorkoutId = section.WorkoutId,
                    Order = section.Order,
                    Name = section.Name,
                    Exercises =
                    [
                        new UpdateSessionExerciseRequest
                        {
                            ExerciseExternalId = exercise.ExerciseExternalId,
                            ExerciseName = exercise.ExerciseName,
                            Order = exercise.Order,
                            MovementType = exercise.MovementType,
                            Sets =
                            [
                                new UpdateExerciseSetRequest
                                {
                                    SetNumber = set.SetNumber,
                                    Reps = 99, // content change triggers diff gate
                                    WeightKg = set.WeightKg
                                }
                            ]
                        }
                    ]
                }
            ]
        };
    }

    // ── Unlock: acquire emits state=Editing to BOTH parties ─────────────────────

    [Fact]
    public async Task Unlock_Acquired_EmitsEditingToBothClientAndTrainer()
    {
        // Arrange
        var sessionId = Guid.NewGuid();
        var plan = CreatePlanWithPublishedSession(sessionId);
        var mongo = TrainingPlanTestHelpers.CreateMockMongo(plan);
        var lockService = LockServiceAcquired(sessionId, plan.ExternalId);
        var notifier = Substitute.For<IRealtimeNotifier>();

        var ep = Factory.Create<UnlockTrainingSessionEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(_trainerId, AppRoles.Trainer))),
            new MockDbBuilder().Build(), mongo, lockService, DefaultOptions(), notifier,
            EndpointTestHelpers.CreateGrantingLinkAuthorizationService());

        // Act
        await ep.HandleAsync(
            new UnlockTrainingSessionRequest { PlanId = plan.ExternalId, SessionId = sessionId },
            TestContext.Current.CancellationToken);

        // Assert — 204 success
        ep.HttpContext.Response.StatusCode.Should().Be(204);

        // Must emit to the CLIENT's user id
        await notifier.Received(1).NotifyAsync(
            _clientId,
            "sessioneditlockchanged",
            Arg.Is<SessionLockChangedPayload>(p =>
                p.PlanId == plan.ExternalId &&
                p.SessionId == sessionId &&
                p.State == "Editing" &&
                p.Holder == "Coach"),
            Arg.Any<CancellationToken>());

        // Must emit to the TRAINER's user id
        await notifier.Received(1).NotifyAsync(
            _trainerId,
            "sessioneditlockchanged",
            Arg.Is<SessionLockChangedPayload>(p =>
                p.PlanId == plan.ExternalId &&
                p.SessionId == sessionId &&
                p.State == "Editing" &&
                p.Holder == "Coach"),
            Arg.Any<CancellationToken>());

        // Exactly 2 total notifications (one per party)
        await notifier.Received(2).NotifyAsync(
            Arg.Any<Guid>(),
            "sessioneditlockchanged",
            Arg.Any<object>(),
            Arg.Any<CancellationToken>());
    }

    // ── Unlock: 409 conflict emits NOTHING ──────────────────────────────────────

    [Fact]
    public async Task Unlock_LockConflict_EmitsNothing()
    {
        // Arrange — session is already Live; acquire returns LockConflict
        var sessionId = Guid.NewGuid();
        var plan = CreatePlanWithPublishedSession(sessionId);
        var mongo = TrainingPlanTestHelpers.CreateMockMongo(plan);
        var lockService = LockServiceConflict();
        var notifier = Substitute.For<IRealtimeNotifier>();

        var ep = Factory.Create<UnlockTrainingSessionEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(_trainerId, AppRoles.Trainer))),
            new MockDbBuilder().Build(), mongo, lockService, DefaultOptions(), notifier,
            EndpointTestHelpers.CreateGrantingLinkAuthorizationService());

        // Act
        await ep.HandleAsync(
            new UnlockTrainingSessionRequest { PlanId = plan.ExternalId, SessionId = sessionId },
            TestContext.Current.CancellationToken);

        // Assert — 409 and zero notifications
        ep.HttpContext.Response.StatusCode.Should().Be(409);

        await notifier.DidNotReceive().NotifyAsync(
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<object>(),
            Arg.Any<CancellationToken>());
    }

    // ── Relock: ReleaseAsync=true emits state=Stable to BOTH parties ─────────────

    [Fact]
    public async Task Relock_Released_EmitsStableToBothClientAndTrainer()
    {
        // Arrange — lock exists and is successfully released
        var sessionId = Guid.NewGuid();
        var plan = CreatePlanWithPublishedSession(sessionId);
        var mongo = TrainingPlanTestHelpers.CreateMockMongo(plan);
        var lockService = LockServiceAcquired(sessionId, plan.ExternalId);
        var notifier = Substitute.For<IRealtimeNotifier>();

        var ep = Factory.Create<RelockTrainingSessionEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(_trainerId, AppRoles.Trainer))),
            new MockDbBuilder().Build(), mongo, lockService, notifier,
            EndpointTestHelpers.CreateGrantingLinkAuthorizationService());

        // Act
        await ep.HandleAsync(
            new RelockTrainingSessionRequest { PlanId = plan.ExternalId, SessionId = sessionId },
            TestContext.Current.CancellationToken);

        // Assert — 204 success
        ep.HttpContext.Response.StatusCode.Should().Be(204);

        // Must emit to the CLIENT's user id
        await notifier.Received(1).NotifyAsync(
            _clientId,
            "sessioneditlockchanged",
            Arg.Is<SessionLockChangedPayload>(p =>
                p.PlanId == plan.ExternalId &&
                p.SessionId == sessionId &&
                p.State == "Stable" &&
                p.Holder == "Coach"),
            Arg.Any<CancellationToken>());

        // Must emit to the TRAINER's user id
        await notifier.Received(1).NotifyAsync(
            _trainerId,
            "sessioneditlockchanged",
            Arg.Is<SessionLockChangedPayload>(p =>
                p.PlanId == plan.ExternalId &&
                p.SessionId == sessionId &&
                p.State == "Stable" &&
                p.Holder == "Coach"),
            Arg.Any<CancellationToken>());
    }

    // ── Relock: ReleaseAsync=false (already gone) emits NOTHING ─────────────────

    [Fact]
    public async Task Relock_AlreadyReleased_EmitsNothing()
    {
        // Arrange — lock already gone; ReleaseAsync returns false (idempotent no-op)
        var sessionId = Guid.NewGuid();
        var plan = CreatePlanWithPublishedSession(sessionId);
        var mongo = TrainingPlanTestHelpers.CreateMockMongo(plan);
        var lockService = LockServiceNoLock(sessionId, plan.ExternalId);
        var notifier = Substitute.For<IRealtimeNotifier>();

        var ep = Factory.Create<RelockTrainingSessionEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(_trainerId, AppRoles.Trainer))),
            new MockDbBuilder().Build(), mongo, lockService, notifier,
            EndpointTestHelpers.CreateGrantingLinkAuthorizationService());

        // Act
        await ep.HandleAsync(
            new RelockTrainingSessionRequest { PlanId = plan.ExternalId, SessionId = sessionId },
            TestContext.Current.CancellationToken);

        // Assert — 204 (idempotent success) and NO notifications
        ep.HttpContext.Response.StatusCode.Should().Be(204);

        // Only emit on a real state transition (ReleaseAsync returned true)
        await notifier.DidNotReceive().NotifyAsync(
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<object>(),
            Arg.Any<CancellationToken>());
    }

    // ── UpdatePlan diff-gate: auto-release emits state=Stable to BOTH parties ───

    [Fact]
    public async Task UpdatePlan_DiffGateAutoRelease_EmitsStableToBothParties()
    {
        // Arrange — published session in Editing by this trainer; content changed → auto-release after save.
        var sessionId = Guid.NewGuid();
        var plan = CreatePlanWithPublishedSession(sessionId);
        var mongo = TrainingPlanTestHelpers.CreateMockMongo(plan);
        var lockService = LockServiceAcquired(sessionId, plan.ExternalId);
        var notifier = Substitute.For<IRealtimeNotifier>();

        var ep = Factory.Create<UpdateTrainingPlanEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(_trainerId, AppRoles.Trainer))),
            mongo, lockService, notifier, new PlanConcurrencyGuard(), new MockDbBuilder().Build(),
            EndpointTestHelpers.CreateGrantingLinkAuthorizationService());

        var changedSession = ChangedSessionRequest(1, plan.Weeks[0].Days.First(d => d.DayOfWeek == 1).Sessions[0], sessionId);
        var req = new UpdateTrainingPlanRequest
        {
            PlanId = plan.ExternalId,
            Name = plan.Name,
            Version = plan.Version,
            StartDate = plan.StartDate,
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

        // Assert — save succeeded
        ep.HttpContext.Response.StatusCode.Should().Be(200);

        // Must emit to the CLIENT's user id
        await notifier.Received(1).NotifyAsync(
            _clientId,
            "sessioneditlockchanged",
            Arg.Is<SessionLockChangedPayload>(p =>
                p.PlanId == plan.ExternalId &&
                p.SessionId == sessionId &&
                p.State == "Stable" &&
                p.Holder == "Coach"),
            Arg.Any<CancellationToken>());

        // Must emit to the TRAINER's user id
        await notifier.Received(1).NotifyAsync(
            _trainerId,
            "sessioneditlockchanged",
            Arg.Is<SessionLockChangedPayload>(p =>
                p.PlanId == plan.ExternalId &&
                p.SessionId == sessionId &&
                p.State == "Stable" &&
                p.Holder == "Coach"),
            Arg.Any<CancellationToken>());
    }

    // ── UpdatePlan diff-gate: ReleaseAsync=false → no emit ──────────────────────

    [Fact]
    public async Task UpdatePlan_DiffGateAutoRelease_WhenReleaseReturnsFalse_EmitsNothing()
    {
        // Arrange — session in Editing by this trainer; but ReleaseAsync returns false (already expired).
        // This proves the gate: only emit on a real state transition.
        var sessionId = Guid.NewGuid();
        var plan = CreatePlanWithPublishedSession(sessionId);
        var mongo = TrainingPlanTestHelpers.CreateMockMongo(plan);
        var lockService = LockServiceNoLock(sessionId, plan.ExternalId);
        var notifier = Substitute.For<IRealtimeNotifier>();

        var ep = Factory.Create<UpdateTrainingPlanEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(_trainerId, AppRoles.Trainer))),
            mongo, lockService, notifier, new PlanConcurrencyGuard(), new MockDbBuilder().Build(),
            EndpointTestHelpers.CreateGrantingLinkAuthorizationService());

        var changedSession = ChangedSessionRequest(1, plan.Weeks[0].Days.First(d => d.DayOfWeek == 1).Sessions[0], sessionId);
        var req = new UpdateTrainingPlanRequest
        {
            PlanId = plan.ExternalId,
            Name = plan.Name,
            Version = plan.Version,
            StartDate = plan.StartDate,
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

        // Assert — save succeeded (200), but NO notifications because ReleaseAsync=false
        ep.HttpContext.Response.StatusCode.Should().Be(200);

        // Only emit when ReleaseAsync returns true — emitting Stable for a session
        // that had no lock would be spurious fan-out
        await notifier.DidNotReceive().NotifyAsync(
            Arg.Any<Guid>(),
            "sessioneditlockchanged",
            Arg.Any<object>(),
            Arg.Any<CancellationToken>());
    }
}
