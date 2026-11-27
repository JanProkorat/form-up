using System.Security.Claims;
using System.Text.Json;
using FastEndpoints;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.NutritionPlans.PublishWeek;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Builders;
using FormUp.Tests.Endpoints;
using MongoDB.Driver;
using NSubstitute;

namespace FormUp.Tests.Endpoints.NutritionPlans;

/// <summary>
/// Tests for <see cref="PublishWeekEndpoint"/>.
///
/// #839: publish now persists via a targeted <c>FindOneAndUpdateAsync</c> + arrayFilters $set
/// rather than a full-document version-gated <c>ReplaceOneAsync</c>. These NSubstitute-based
/// unit tests exercise the ENDPOINT'S logic (validation ordering, sibling-archive gating,
/// outcome-to-status-code mapping) with an explicitly stubbed write result — they do NOT and
/// cannot prove real MongoDB arrayFilters/$set semantics. That proof lives in
/// <see cref="PublishWeekConcurrencyIntegrationTests"/> (Testcontainers, real Mongo).
/// </summary>
public class PublishWeekEndpointTests
{
    private readonly Guid _nutritionistId = Guid.NewGuid();

    private PublishWeekEndpoint CreateEndpoint(
        IMongoContext mongo,
        MemoryStream? responseBody = null,
        IClientLinkAuthorizationService? linkAuthorizationService = null) =>
        Factory.Create<PublishWeekEndpoint>(
            ctx =>
            {
                ctx.Request.HttpContext.User = new ClaimsPrincipal(
                    new ClaimsIdentity(
                        EndpointTestHelpers.FakeUserClaims(_nutritionistId, AppRoles.Nutritionist)));
                if (responseBody is not null)
                    ctx.Request.HttpContext.Response.Body = responseBody;
            },
            mongo,
            new MockDbBuilder().Build(),
            Substitute.For<INotificationService>(),
            Substitute.For<IRealtimeNotifier>(),
            new PlanConcurrencyGuard(),
            linkAuthorizationService ?? EndpointTestHelpers.CreateGrantingLinkAuthorizationService());

    /// <summary>
    /// Builds the plan the mocked <c>FindOneAndUpdateAsync</c> should return for a successful
    /// publish of <paramref name="weekNumber"/> — a shallow clone of <paramref name="plan"/> with
    /// the target week Published and the plan-level fields the real targeted $set would touch.
    /// </summary>
    private static NutritionPlan AsPublished(NutritionPlan plan, int weekNumber) => new()
    {
        ExternalId = plan.ExternalId,
        ClientId = plan.ClientId,
        NutritionistId = plan.NutritionistId,
        Name = plan.Name,
        Status = NutritionPlanStatus.Active,
        GlobalSettings = plan.GlobalSettings,
        Weeks = plan.Weeks.Select(w => new PlanWeek
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

    private static void StubSuccessfulPublish(IMongoContext mongo, NutritionPlan published) =>
        mongo.NutritionPlans.FindOneAndUpdateAsync(
                Arg.Any<FilterDefinition<NutritionPlan>>(),
                Arg.Any<UpdateDefinition<NutritionPlan>>(),
                Arg.Any<FindOneAndUpdateOptions<NutritionPlan, NutritionPlan>>(),
                Arg.Any<CancellationToken>())
            .Returns((NutritionPlan?)published);

    private static void StubConflictingPublish(IMongoContext mongo) =>
        mongo.NutritionPlans.FindOneAndUpdateAsync(
                Arg.Any<FilterDefinition<NutritionPlan>>(),
                Arg.Any<UpdateDefinition<NutritionPlan>>(),
                Arg.Any<FindOneAndUpdateOptions<NutritionPlan, NutritionPlan>>(),
                Arg.Any<CancellationToken>())
            .Returns((NutritionPlan?)null);

    [Fact]
    public async Task HandleAsync_DraftWeek_PublishesSuccessfully()
    {
        var planId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var plan = PlanTestHelpers.CreatePlan(
            externalId: planId,
            clientId: clientId,
            nutritionistId: _nutritionistId,
            status: NutritionPlanStatus.Draft,
            weekCount: 2,
            version: 1);
        plan.StartDate = DateTime.UtcNow.Date;

        // Sibling Active plan for the SAME client whose window overlaps the plan being
        // published (same StartDate) — must be superseded (#780: only overlapping siblings
        // are archived, see the dedicated non-overlap regression test below).
        var overlappingSibling = PlanTestHelpers.CreatePlan(
            clientId: clientId,
            nutritionistId: _nutritionistId,
            status: NutritionPlanStatus.Active,
            weekCount: 2,
            version: 1);
        overlappingSibling.StartDate = plan.StartDate;

        // Both weeks are Draft (default from CreatePlan)
        var mongo = PlanTestHelpers.CreateMockMongo(plans: [plan, overlappingSibling]);
        StubSuccessfulPublish(mongo, AsPublished(plan, weekNumber: 1));
        var ep = CreateEndpoint(mongo);

        var req = new PublishWeekRequest
        {
            PlanId = planId,
            WeekNumber = 1
        };

        await ep.HandleAsync(req, TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(200);

        // Should archive the overlapping sibling Active plan for the same client
        await mongo.NutritionPlans.Received().UpdateManyAsync(
            Arg.Any<FilterDefinition<NutritionPlan>>(),
            Arg.Any<UpdateDefinition<NutritionPlan>>(),
            Arg.Any<UpdateOptions>(),
            Arg.Any<CancellationToken>());

        // The targeted write must gate on the target week only — not the document Version.
        await mongo.NutritionPlans.Received(1).FindOneAndUpdateAsync(
            Arg.Any<FilterDefinition<NutritionPlan>>(),
            Arg.Any<UpdateDefinition<NutritionPlan>>(),
            Arg.Any<FindOneAndUpdateOptions<NutritionPlan, NutritionPlan>>(),
            Arg.Any<CancellationToken>());
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
        var planA = PlanTestHelpers.CreatePlan(
            clientId: clientId,
            nutritionistId: _nutritionistId,
            status: NutritionPlanStatus.Active,
            weekCount: 2,
            version: 1);
        planA.StartDate = DateTime.UtcNow.Date.AddDays(-60);
        planA.Weeks[0].Status = WeekStatus.Published;
        planA.Weeks[0].DatePublished = planA.StartDate;

        // Plan B: Draft, about to publish its first week, window starts today —
        // does not overlap Plan A's window at all.
        var planB = PlanTestHelpers.CreatePlan(
            externalId: planBId,
            clientId: clientId,
            nutritionistId: _nutritionistId,
            status: NutritionPlanStatus.Draft,
            weekCount: 2,
            version: 1);
        planB.StartDate = DateTime.UtcNow.Date;

        var mongo = PlanTestHelpers.CreateMockMongo(plans: [planB, planA]);
        StubSuccessfulPublish(mongo, AsPublished(planB, weekNumber: 1));
        var ep = CreateEndpoint(mongo);

        var req = new PublishWeekRequest
        {
            PlanId = planBId,
            WeekNumber = 1
        };

        await ep.HandleAsync(req, TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(200);

        // Plan A's window does not overlap Plan B's — it must NOT be archived.
        await mongo.NutritionPlans.DidNotReceive().UpdateManyAsync(
            Arg.Any<FilterDefinition<NutritionPlan>>(),
            Arg.Any<UpdateDefinition<NutritionPlan>>(),
            Arg.Any<UpdateOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_AlreadyPublished_ThrowsError()
    {
        var planId = Guid.NewGuid();
        var plan = PlanTestHelpers.CreatePlan(
            externalId: planId,
            nutritionistId: _nutritionistId,
            status: NutritionPlanStatus.Active,
            weekCount: 2,
            version: 1);

        // Set week 1 to already Published
        plan.Weeks[0].Status = WeekStatus.Published;
        plan.Weeks[0].DatePublished = DateTime.UtcNow.AddDays(-1);

        var mongo = PlanTestHelpers.CreateMockMongo(plans: [plan]);
        var ep = CreateEndpoint(mongo);

        var req = new PublishWeekRequest
        {
            PlanId = planId,
            WeekNumber = 1
        };

        var act = () => ep.HandleAsync(req, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ValidationFailureException>();

        // The already-published guard fires during validation, before any write is attempted.
        await mongo.NutritionPlans.DidNotReceive().FindOneAndUpdateAsync(
            Arg.Any<FilterDefinition<NutritionPlan>>(),
            Arg.Any<UpdateDefinition<NutritionPlan>>(),
            Arg.Any<FindOneAndUpdateOptions<NutritionPlan, NutritionPlan>>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Regression test for #839 AC#4: publishing must no longer 409 just because the plan's
    /// Version has moved since the caller last read it (e.g. a concurrent edit to an unrelated
    /// week bumped Version). The request carries no version since #851 — concurrency for the
    /// write is gated on the TARGET WEEK's own state via the write filter's ElemMatch, not the
    /// document-level Version.
    /// </summary>
    [Fact]
    public async Task HandleAsync_ConcurrentVersionBump_StillPublishes_Returns200()
    {
        var planId = Guid.NewGuid();
        var plan = PlanTestHelpers.CreatePlan(
            externalId: planId,
            nutritionistId: _nutritionistId,
            status: NutritionPlanStatus.Draft,
            weekCount: 1,
            version: 5); // any document Version publishes — it is never compared
        plan.StartDate = DateTime.UtcNow.Date;

        var mongo = PlanTestHelpers.CreateMockMongo(plans: [plan]);
        StubSuccessfulPublish(mongo, AsPublished(plan, weekNumber: 1));
        var ep = CreateEndpoint(mongo);

        var req = new PublishWeekRequest
        {
            PlanId = planId,
            WeekNumber = 1
        };

        await ep.HandleAsync(req, TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task HandleAsync_NotFound_Returns404()
    {
        var mongo = PlanTestHelpers.CreateMockMongo();
        var ep = CreateEndpoint(mongo);

        var req = new PublishWeekRequest
        {
            PlanId = Guid.NewGuid(),
            WeekNumber = 1
        };

        await ep.HandleAsync(req, TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(404);
    }

    /// <summary>
    /// Deny-path test for the link-authorization guard itself. The plan is owned by the caller,
    /// but the caller's link to the plan's client no longer grants nutrition access — this must
    /// 404 before the write is even attempted, distinct from
    /// <see cref="HandleAsync_NotFound_Returns404"/> which denies on a missing plan.
    /// </summary>
    [Fact]
    public async Task HandleAsync_NotLinkedToClient_Returns404()
    {
        var planId = Guid.NewGuid();
        var plan = PlanTestHelpers.CreatePlan(
            externalId: planId, nutritionistId: _nutritionistId, weekCount: 2);
        plan.StartDate = DateTime.UtcNow.Date;
        var mongo = PlanTestHelpers.CreateMockMongo(plans: [plan]);
        StubSuccessfulPublish(mongo, AsPublished(plan, weekNumber: 1));

        var ep = CreateEndpoint(mongo, linkAuthorizationService: PlanTestHelpers.CreateDenyingLinkAuthorizationService());

        var req = new PublishWeekRequest
        {
            PlanId = planId,
            WeekNumber = 1
        };

        await ep.HandleAsync(req, TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(404);

        await mongo.NutritionPlans.DidNotReceive().FindOneAndUpdateAsync(
            Arg.Any<FilterDefinition<NutritionPlan>>(),
            Arg.Any<UpdateDefinition<NutritionPlan>>(),
            Arg.Any<FindOneAndUpdateOptions<NutritionPlan, NutritionPlan>>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Flag-inversion deny test: the link is active and exists, but grants only the training
    /// domain. A "no link" deny test cannot detect a guard that checks the wrong flag, since
    /// both flags are absent either way — this pins the guard to
    /// <c>CanViewNutritionPlans</c> specifically.
    /// </summary>
    [Fact]
    public async Task HandleAsync_LinkGrantsOnlyTraining_Returns404()
    {
        var planId = Guid.NewGuid();
        var plan = PlanTestHelpers.CreatePlan(
            externalId: planId, nutritionistId: _nutritionistId, weekCount: 2);
        plan.StartDate = DateTime.UtcNow.Date;
        var mongo = PlanTestHelpers.CreateMockMongo(plans: [plan]);
        StubSuccessfulPublish(mongo, AsPublished(plan, weekNumber: 1));

        var ep = CreateEndpoint(
            mongo,
            linkAuthorizationService: EndpointTestHelpers.CreateGrantingLinkAuthorizationService(
                canViewNutritionPlans: false, canViewTrainingPlans: true));

        var req = new PublishWeekRequest
        {
            PlanId = planId,
            WeekNumber = 1
        };

        await ep.HandleAsync(req, TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(404);

        await mongo.NutritionPlans.DidNotReceive().FindOneAndUpdateAsync(
            Arg.Any<FilterDefinition<NutritionPlan>>(),
            Arg.Any<UpdateDefinition<NutritionPlan>>(),
            Arg.Any<FindOneAndUpdateOptions<NutritionPlan, NutritionPlan>>(),
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
        var plan = PlanTestHelpers.CreatePlan(
            externalId: planId,
            nutritionistId: _nutritionistId,
            status: NutritionPlanStatus.Draft,
            weekCount: 1,
            version: 1);
        plan.StartDate = DateTime.UtcNow.Date;

        var mongo = PlanTestHelpers.CreateMockMongo(plans: [plan]);
        StubConflictingPublish(mongo);

        var ep = CreateEndpoint(mongo);

        var req = new PublishWeekRequest
        {
            PlanId = planId,
            WeekNumber = 1
        };

        await ep.HandleAsync(req, TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(409);

        // Siblings must NOT be archived when the targeted write loses the race.
        await mongo.NutritionPlans.DidNotReceive().UpdateManyAsync(
            Arg.Any<FilterDefinition<NutritionPlan>>(),
            Arg.Any<UpdateDefinition<NutritionPlan>>(),
            Arg.Any<UpdateOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ConcurrentSameWeekPublish_Returns409WithProblemDetailsShape()
    {
        // Verifies the race-conflict path returns 409 via SendProblemAsync (RFC 7807 Problem
        // Details) with the correct errorCode and content type, not the legacy raw anonymous-
        // object SendAsync pattern.
        var planId = Guid.NewGuid();
        var plan = PlanTestHelpers.CreatePlan(
            externalId: planId,
            nutritionistId: _nutritionistId,
            status: NutritionPlanStatus.Draft,
            weekCount: 1,
            version: 1);
        plan.StartDate = DateTime.UtcNow.Date;

        using var responseBody = new MemoryStream();
        var mongo = PlanTestHelpers.CreateMockMongo(plans: [plan]);
        StubConflictingPublish(mongo);
        var ep = CreateEndpoint(mongo, responseBody);

        var req = new PublishWeekRequest
        {
            PlanId = planId,
            WeekNumber = 1
        };

        await ep.HandleAsync(req, TestContext.Current.CancellationToken);

        // 1. HTTP status
        ep.HttpContext.Response.StatusCode.Should().Be(409);

        // 2. errorCode extension in the RFC 7807 body — the raw SendAsync pattern would write
        //    { "Error": "..." } with no "errorCode" field, so this assertion locks the contract.
        responseBody.Seek(0, SeekOrigin.Begin);
        using var doc = await JsonDocument.ParseAsync(responseBody);
        doc.RootElement.GetProperty("errorCode").GetString()
            .Should().Be(ErrorCodes.PlanVersionConflict);
    }
}
