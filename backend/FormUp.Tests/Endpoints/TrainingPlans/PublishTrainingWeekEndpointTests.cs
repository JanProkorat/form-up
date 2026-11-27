using System.Security.Claims;
using System.Text.Json;
using FastEndpoints;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.TrainingPlans.PublishTrainingWeek;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Builders;
using FormUp.Tests.Endpoints;
using MongoDB.Driver;
using NSubstitute;

namespace FormUp.Tests.Endpoints.TrainingPlans;

/// <summary>
/// Tests for <see cref="PublishTrainingWeekEndpoint"/>.
///
/// #839: publish now persists via a targeted <c>FindOneAndUpdateAsync</c> + arrayFilters $set
/// rather than a full-document version-gated <c>ReplaceOneAsync</c>. These NSubstitute-based
/// unit tests exercise the ENDPOINT'S logic (validation ordering, sibling-archive gating,
/// outcome-to-status-code mapping) with an explicitly stubbed write result — they do NOT and
/// cannot prove real MongoDB arrayFilters/$set semantics. That proof lives in
/// <see cref="PublishTrainingWeekConcurrencyIntegrationTests"/> (Testcontainers, real Mongo).
/// </summary>
public class PublishTrainingWeekEndpointTests
{
    private readonly Guid _trainerId = Guid.NewGuid();

    private static ISessionLockService StubLockService()
    {
        var svc = Substitute.For<ISessionLockService>();
        svc.ReleaseAsync(Arg.Any<Guid>(), Arg.Any<LockHolder>(), Arg.Any<LockType>(),
            Arg.Any<CancellationToken>()).Returns(false);
        return svc;
    }

    /// <summary>
    /// Builds the plan the mocked <c>FindOneAndUpdateAsync</c> should return for a successful
    /// publish of <paramref name="weekNumber"/> — a shallow clone of <paramref name="plan"/> with
    /// the target week Published and the plan-level fields the real targeted $set would touch.
    /// </summary>
    private static TrainingPlan AsPublished(TrainingPlan plan, int weekNumber) => new()
    {
        ExternalId = plan.ExternalId,
        ClientId = plan.ClientId,
        TrainerId = plan.TrainerId,
        Name = plan.Name,
        Description = plan.Description,
        Status = TrainingPlanStatus.Active,
        Weeks = plan.Weeks.Select(w => new TrainingWeek
        {
            WeekNumber = w.WeekNumber,
            Status = w.WeekNumber == weekNumber ? WeekStatus.Published : w.Status,
            DatePublished = w.WeekNumber == weekNumber ? DateTime.UtcNow : w.DatePublished,
            Days = w.Days
        }).ToList(),
        Version = plan.Version + 1,
        DateCreated = plan.DateCreated,
        DateUpdated = DateTime.UtcNow,
        StartDate = plan.StartDate
    };

    private static void StubSuccessfulPublish(IMongoContext mongo, TrainingPlan published) =>
        mongo.TrainingPlans.FindOneAndUpdateAsync(
                Arg.Any<FilterDefinition<TrainingPlan>>(),
                Arg.Any<UpdateDefinition<TrainingPlan>>(),
                Arg.Any<FindOneAndUpdateOptions<TrainingPlan, TrainingPlan>>(),
                Arg.Any<CancellationToken>())
            .Returns((TrainingPlan?)published);

    private static void StubConflictingPublish(IMongoContext mongo) =>
        mongo.TrainingPlans.FindOneAndUpdateAsync(
                Arg.Any<FilterDefinition<TrainingPlan>>(),
                Arg.Any<UpdateDefinition<TrainingPlan>>(),
                Arg.Any<FindOneAndUpdateOptions<TrainingPlan, TrainingPlan>>(),
                Arg.Any<CancellationToken>())
            .Returns((TrainingPlan?)null);

    [Fact]
    public async Task HandleAsync_ValidPublish_Returns200()
    {
        var planId = Guid.NewGuid();
        var plan = TrainingPlanTestHelpers.CreatePlan(
            externalId: planId, trainerId: _trainerId, weekCount: 2);
        plan.StartDate = DateTime.UtcNow.Date;
        var mongo = TrainingPlanTestHelpers.CreateMockMongo(plan);
        StubSuccessfulPublish(mongo, AsPublished(plan, weekNumber: 1));

        var ep = Factory.Create<PublishTrainingWeekEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_trainerId, AppRoles.Trainer))),
            mongo,
            new MockDbBuilder().Build(),
            Substitute.For<INotificationService>(),
            Substitute.For<IRealtimeNotifier>(),
            StubLockService(),
            new PlanConcurrencyGuard(),
            EndpointTestHelpers.CreateGrantingLinkAuthorizationService());

        await ep.HandleAsync(new PublishTrainingWeekRequest
        {
            PlanId = planId,
            WeekNumber = 1
        }, TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(200);
    }

    /// <summary>
    /// Deny-path test for the link-authorization guard itself. The plan is owned by the caller,
    /// but the caller's link to the plan's client no longer grants training access — this must
    /// 404 before the write is even attempted. If <see cref="IClientLinkAuthorizationService"/>
    /// were removed from this guard, this test would regress to 200.
    /// </summary>
    [Fact]
    public async Task HandleAsync_NotLinkedToClient_Returns404()
    {
        var planId = Guid.NewGuid();
        var plan = TrainingPlanTestHelpers.CreatePlan(
            externalId: planId, trainerId: _trainerId, weekCount: 2);
        plan.StartDate = DateTime.UtcNow.Date;
        var mongo = TrainingPlanTestHelpers.CreateMockMongo(plan);
        StubSuccessfulPublish(mongo, AsPublished(plan, weekNumber: 1));

        var ep = Factory.Create<PublishTrainingWeekEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_trainerId, AppRoles.Trainer))),
            mongo,
            new MockDbBuilder().Build(),
            Substitute.For<INotificationService>(),
            Substitute.For<IRealtimeNotifier>(),
            StubLockService(),
            new PlanConcurrencyGuard(),
            TrainingPlanTestHelpers.CreateDenyingLinkAuthorizationService());

        await ep.HandleAsync(new PublishTrainingWeekRequest
        {
            PlanId = planId,
            WeekNumber = 1
        }, TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(404);

        await mongo.TrainingPlans.DidNotReceive().FindOneAndUpdateAsync(
            Arg.Any<FilterDefinition<TrainingPlan>>(),
            Arg.Any<UpdateDefinition<TrainingPlan>>(),
            Arg.Any<FindOneAndUpdateOptions<TrainingPlan, TrainingPlan>>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Flag-inversion deny test: the link is active and exists, but grants only the nutrition
    /// domain. A "no link" deny test cannot detect a guard that checks the wrong flag, since
    /// both flags are absent either way — this pins the guard to
    /// <c>CanViewTrainingPlans</c> specifically.
    /// </summary>
    [Fact]
    public async Task HandleAsync_LinkGrantsOnlyNutrition_Returns404()
    {
        var planId = Guid.NewGuid();
        var plan = TrainingPlanTestHelpers.CreatePlan(
            externalId: planId, trainerId: _trainerId, weekCount: 2);
        plan.StartDate = DateTime.UtcNow.Date;
        var mongo = TrainingPlanTestHelpers.CreateMockMongo(plan);
        StubSuccessfulPublish(mongo, AsPublished(plan, weekNumber: 1));

        var ep = Factory.Create<PublishTrainingWeekEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_trainerId, AppRoles.Trainer))),
            mongo,
            new MockDbBuilder().Build(),
            Substitute.For<INotificationService>(),
            Substitute.For<IRealtimeNotifier>(),
            StubLockService(),
            new PlanConcurrencyGuard(),
            EndpointTestHelpers.CreateGrantingLinkAuthorizationService(
                canViewNutritionPlans: true, canViewTrainingPlans: false));

        await ep.HandleAsync(new PublishTrainingWeekRequest
        {
            PlanId = planId,
            WeekNumber = 1
        }, TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(404);

        await mongo.TrainingPlans.DidNotReceive().FindOneAndUpdateAsync(
            Arg.Any<FilterDefinition<TrainingPlan>>(),
            Arg.Any<UpdateDefinition<TrainingPlan>>(),
            Arg.Any<FindOneAndUpdateOptions<TrainingPlan, TrainingPlan>>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Regression test for #839 AC#4: publishing must no longer 409 just because the plan's
    /// Version has moved since the caller last read it. The request carries no version since
    /// #851 — concurrency for the write is gated on the TARGET WEEK's own state via the write
    /// filter's ElemMatch, not the document-level Version.
    /// </summary>
    [Fact]
    public async Task HandleAsync_ConcurrentVersionBump_StillPublishes_Returns200()
    {
        var planId = Guid.NewGuid();
        var plan = TrainingPlanTestHelpers.CreatePlan(
            externalId: planId, trainerId: _trainerId, version: 3);
        plan.StartDate = DateTime.UtcNow.Date;
        var mongo = TrainingPlanTestHelpers.CreateMockMongo(plan);
        StubSuccessfulPublish(mongo, AsPublished(plan, weekNumber: 1));

        var ep = Factory.Create<PublishTrainingWeekEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_trainerId, AppRoles.Trainer))),
            mongo,
            new MockDbBuilder().Build(),
            Substitute.For<INotificationService>(),
            Substitute.For<IRealtimeNotifier>(),
            StubLockService(),
            new PlanConcurrencyGuard(),
            EndpointTestHelpers.CreateGrantingLinkAuthorizationService());

        await ep.HandleAsync(new PublishTrainingWeekRequest
        {
            PlanId = planId,
            WeekNumber = 1
        }, TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task HandleAsync_ConcurrentSameWeekPublish_Returns409WithProblemDetailsShape()
    {
        // Verifies the race-conflict path uses SendProblemAsync (RFC 7807 Problem Details) with
        // the correct errorCode and content type. The genuine same-week race: another request
        // published the SAME week between our fetch and our write, so the targeted write's
        // ElemMatch/arrayFilter matches zero documents (FindOneAndUpdateAsync returns null).
        var planId = Guid.NewGuid();
        var plan = TrainingPlanTestHelpers.CreatePlan(
            externalId: planId, trainerId: _trainerId, version: 3);
        plan.StartDate = DateTime.UtcNow.Date;
        var mongo = TrainingPlanTestHelpers.CreateMockMongo(plan);
        StubConflictingPublish(mongo);

        using var responseBody = new MemoryStream();
        var ep = Factory.Create<PublishTrainingWeekEndpoint>(
            ctx =>
            {
                ctx.Request.HttpContext.User = new ClaimsPrincipal(
                    new ClaimsIdentity(
                        EndpointTestHelpers.FakeUserClaims(_trainerId, AppRoles.Trainer)));
                ctx.Request.HttpContext.Response.Body = responseBody;
            },
            mongo,
            new MockDbBuilder().Build(),
            Substitute.For<INotificationService>(),
            Substitute.For<IRealtimeNotifier>(),
            StubLockService(),
            new PlanConcurrencyGuard(),
            EndpointTestHelpers.CreateGrantingLinkAuthorizationService());

        await ep.HandleAsync(new PublishTrainingWeekRequest
        {
            PlanId = planId,
            WeekNumber = 1
        }, TestContext.Current.CancellationToken);

        // 1. HTTP status
        ep.HttpContext.Response.StatusCode.Should().Be(409);

        // 2. errorCode extension in the RFC 7807 body — the raw SendAsync pattern would write
        //    { "Error": "..." } with no "errorCode" field, so this assertion locks the contract.
        responseBody.Seek(0, SeekOrigin.Begin);
        using var doc = await JsonDocument.ParseAsync(responseBody);
        doc.RootElement.GetProperty("errorCode").GetString()
            .Should().Be(ErrorCodes.PlanVersionConflict);
    }

    /// <summary>
    /// Regression guard for #780: publishing plan B's first week must NOT archive a
    /// non-overlapping Active plan A of the same client — e.g. a January plan stays Active
    /// when a March plan is published. Only overlapping/same-window Active plans may be
    /// superseded.
    /// </summary>
    [Fact]
    public async Task HandleAsync_FirstPublish_NonOverlappingActivePlan_DoesNotArchiveIt()
    {
        var clientId = Guid.NewGuid();
        var planBId = Guid.NewGuid();

        // Plan A: Active, already published, window fully in the past (started 60 days ago,
        // 2-week duration — long elapsed by the time Plan B's window begins).
        var planA = TrainingPlanTestHelpers.CreatePlan(
            clientId: clientId, trainerId: _trainerId,
            status: TrainingPlanStatus.Active, weekCount: 2, version: 1);
        planA.StartDate = DateTime.UtcNow.Date.AddDays(-60);
        planA.Weeks[0].Status = WeekStatus.Published;
        planA.Weeks[0].DatePublished = planA.StartDate;

        // Plan B: Draft, about to publish its first week, window starts today —
        // does not overlap Plan A's window at all.
        var planB = TrainingPlanTestHelpers.CreatePlan(
            externalId: planBId, clientId: clientId, trainerId: _trainerId,
            status: TrainingPlanStatus.Draft, weekCount: 2, version: 1);
        planB.StartDate = DateTime.UtcNow.Date;

        var mongo = TrainingPlanTestHelpers.CreateMockMongo(planB, planA);
        StubSuccessfulPublish(mongo, AsPublished(planB, weekNumber: 1));

        var ep = Factory.Create<PublishTrainingWeekEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_trainerId, AppRoles.Trainer))),
            mongo,
            new MockDbBuilder().Build(),
            Substitute.For<INotificationService>(),
            Substitute.For<IRealtimeNotifier>(),
            StubLockService(),
            new PlanConcurrencyGuard(),
            EndpointTestHelpers.CreateGrantingLinkAuthorizationService());

        await ep.HandleAsync(new PublishTrainingWeekRequest
        {
            PlanId = planBId,
            WeekNumber = 1
        }, TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(200);

        // Plan A's window does not overlap Plan B's — it must NOT be archived.
        await mongo.TrainingPlans.DidNotReceive().UpdateManyAsync(
            Arg.Any<FilterDefinition<TrainingPlan>>(),
            Arg.Any<UpdateDefinition<TrainingPlan>>(),
            Arg.Any<UpdateOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ConcurrentSameWeekPublish_ArrayFilterMatchesZero_Returns409AndDoesNotArchiveSiblings()
    {
        // Regression test for the genuine same-week race (#839 error path 7): another request
        // published the SAME week between our fetch and our write. The targeted write's
        // ElemMatch/arrayFilter then matches zero documents (FindOneAndUpdateAsync returns null),
        // and the endpoint must return 409 without archiving any sibling plans.
        var planId = Guid.NewGuid();
        var plan = TrainingPlanTestHelpers.CreatePlan(
            externalId: planId, trainerId: _trainerId, weekCount: 1, version: 1);
        plan.StartDate = DateTime.UtcNow.Date;
        var mongo = TrainingPlanTestHelpers.CreateMockMongo(plan);
        StubConflictingPublish(mongo);

        var ep = Factory.Create<PublishTrainingWeekEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_trainerId, AppRoles.Trainer))),
            mongo,
            new MockDbBuilder().Build(),
            Substitute.For<INotificationService>(),
            Substitute.For<IRealtimeNotifier>(),
            StubLockService(),
            new PlanConcurrencyGuard(),
            EndpointTestHelpers.CreateGrantingLinkAuthorizationService());

        await ep.HandleAsync(new PublishTrainingWeekRequest
        {
            PlanId = planId,
            WeekNumber = 1
        }, TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(409);

        // Siblings must NOT be archived when the targeted write loses the race.
        await mongo.TrainingPlans.DidNotReceive().UpdateManyAsync(
            Arg.Any<FilterDefinition<TrainingPlan>>(),
            Arg.Any<UpdateDefinition<TrainingPlan>>(),
            Arg.Any<UpdateOptions>(),
            Arg.Any<CancellationToken>());
    }
}
