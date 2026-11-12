using FastEndpoints;
using FormUp.Application.Domain.Services;
using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Features.Trainers.GetClientDashboard;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Builders;
using FormUp.Tests.Endpoints.NutritionPlans;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace FormUp.Tests.Endpoints.Trainers;

public class GetClientDashboardEndpointTests
{
    private readonly Guid _trainerId = Guid.NewGuid();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly IComplianceService _complianceService = Substitute.For<IComplianceService>();

    // Returns a mock IMongoContext with no active plans — plan-first path
    // falls back to onboarding data without affecting the assertion.
    private static IMongoContext EmptyMongo() => PlanTestHelpers.CreateMockMongo();

    [Fact]
    public async Task HandleAsync_LinkedClient_ReturnsDashboard()
    {
        var clientUser = EntityBuilder.User.WithEmail("client@test.com")
            .WithFirstName("Dash").WithLastName("Client").Build();
        var trainerProfile = EntityBuilder.ProfessionalProfile.WithId(1).WithUserId(_trainerId).Build();
        var clientProfile = EntityBuilder.ClientProfile.WithId(1).WithUser(clientUser).Build();
        var link = EntityBuilder.ClientProfessionalLink
            .WithId(99)
            .WithClientProfile(clientProfile)
            .WithProfessionalProfile(trainerProfile)
            .Build();

        var db = new MockDbBuilder()
            .With(trainerProfile)
            .With(clientProfile)
            .With(link)
            .Build();

        var ep = Factory.Create<GetClientDashboardEndpoint>(
            ctx => ctx.Request.HttpContext.User = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_trainerId, AppRoles.Trainer))),
            db, _audit, _complianceService, EmptyMongo(), TimeProvider.System, PassThroughCoachDisciplineAccess.Instance,
            new EntitlementService(db, TimeProvider.System, NullLogger<EntitlementService>.Instance));

        await ep.HandleAsync(new GetClientDashboardRequest
        {
            ClientId = clientProfile.PublicId
        }, TestContext.Current.CancellationToken);

        ep.Response.Email.Should().Be("client@test.com");
        ep.Response.FirstName.Should().Be("Dash");
        ep.Response.LastName.Should().Be("Client");
        ep.Response.IsActive.Should().BeTrue();
        ep.Response.TotalMeasurements.Should().Be(0);
        ep.Response.TotalProgressPhotos.Should().Be(0);
        ep.Response.LinkId.Should().Be(99);
        ep.Response.ClientUserId.Should().Be(clientUser.Id);
    }

    [Fact]
    public async Task HandleAsync_LinkedClient_ReturnsClientUserId_MatchingApplicationUserId()
    {
        // ClientUserId must equal the ApplicationUser.Id (the FK on ClientProfile),
        // which differs from ClientPublicId. This is the identifier the weekly-check-in
        // endpoint filters on.
        var clientUser = EntityBuilder.User.WithEmail("checkin@test.com")
            .WithFirstName("Check").WithLastName("In").Build();
        var trainerProfile = EntityBuilder.ProfessionalProfile.WithId(2).WithUserId(_trainerId).Build();
        var clientProfile = EntityBuilder.ClientProfile.WithId(2).WithUser(clientUser).Build();
        var link = EntityBuilder.ClientProfessionalLink
            .WithId(100)
            .WithClientProfile(clientProfile)
            .WithProfessionalProfile(trainerProfile)
            .Build();

        var db = new MockDbBuilder()
            .With(trainerProfile)
            .With(clientProfile)
            .With(link)
            .Build();

        var ep = Factory.Create<GetClientDashboardEndpoint>(
            ctx => ctx.Request.HttpContext.User = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_trainerId, AppRoles.Trainer))),
            db, _audit, _complianceService, EmptyMongo(), TimeProvider.System, PassThroughCoachDisciplineAccess.Instance,
            new EntitlementService(db, TimeProvider.System, NullLogger<EntitlementService>.Instance));

        await ep.HandleAsync(new GetClientDashboardRequest
        {
            ClientId = clientProfile.PublicId
        }, TestContext.Current.CancellationToken);

        ep.Response.ClientUserId.Should().Be(clientUser.Id);
        ep.Response.ClientPublicId.Should().Be(clientProfile.PublicId);
        ep.Response.ClientUserId.Should().NotBe(clientProfile.PublicId);
    }

    [Fact]
    public async Task HandleAsync_UnlinkedClient_Returns404()
    {
        var trainerProfile = EntityBuilder.ProfessionalProfile.WithId(1).WithUserId(_trainerId).Build();
        var otherUser = EntityBuilder.User.WithEmail("other@test.com")
            .WithFirstName("Other").WithLastName("User").Build();
        var clientProfile = EntityBuilder.ClientProfile.WithId(1).WithUser(otherUser).Build();

        var db = new MockDbBuilder()
            .With(trainerProfile)
            .With(clientProfile)
            .Build();

        var ep = Factory.Create<GetClientDashboardEndpoint>(
            ctx => ctx.Request.HttpContext.User = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_trainerId, AppRoles.Trainer))),
            db, _audit, _complianceService, EmptyMongo(), TimeProvider.System, PassThroughCoachDisciplineAccess.Instance,
            new EntitlementService(db, TimeProvider.System, NullLogger<EntitlementService>.Instance));

        await ep.HandleAsync(new GetClientDashboardRequest
        {
            ClientId = clientProfile.PublicId
        }, TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task HandleAsync_NonexistentClient_Returns404()
    {
        var trainerProfile = EntityBuilder.ProfessionalProfile.WithId(1).WithUserId(_trainerId).Build();
        var db = new MockDbBuilder().With(trainerProfile).Build();

        var ep = Factory.Create<GetClientDashboardEndpoint>(
            ctx => ctx.Request.HttpContext.User = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_trainerId, AppRoles.Trainer))),
            db, _audit, _complianceService, EmptyMongo(), TimeProvider.System, PassThroughCoachDisciplineAccess.Instance,
            new EntitlementService(db, TimeProvider.System, NullLogger<EntitlementService>.Instance));

        await ep.HandleAsync(new GetClientDashboardRequest
        {
            ClientId = Guid.NewGuid()
        }, TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task HandleAsync_ComplianceServiceThrows_Returns200_WithNullCompliance()
    {
        // Arrange: compliance service throws — endpoint must degrade gracefully
        var clientUser = EntityBuilder.User.WithEmail("client@test.com")
            .WithFirstName("Dash").WithLastName("Client").Build();
        var trainerProfile = EntityBuilder.ProfessionalProfile.WithId(1).WithUserId(_trainerId).Build();
        var clientProfile = EntityBuilder.ClientProfile.WithId(1).WithUser(clientUser).Build();
        var link = EntityBuilder.ClientProfessionalLink
            .WithId(99)
            .WithClientProfile(clientProfile)
            .WithProfessionalProfile(trainerProfile)
            .Build();

        var db = new MockDbBuilder()
            .With(trainerProfile)
            .With(clientProfile)
            .With(link)
            .Build();

        _complianceService
            .CalculateComplianceAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>(), Arg.Any<Guid?>())
            .ThrowsAsync(new InvalidOperationException("No active nutrition plan"));

        var ep = Factory.Create<GetClientDashboardEndpoint>(
            ctx => ctx.Request.HttpContext.User = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_trainerId, AppRoles.Trainer))),
            db, _audit, _complianceService, EmptyMongo(), TimeProvider.System, PassThroughCoachDisciplineAccess.Instance,
            new EntitlementService(db, TimeProvider.System, NullLogger<EntitlementService>.Instance));

        // Act — must not throw
        await ep.HandleAsync(new GetClientDashboardRequest
        {
            ClientId = clientProfile.PublicId
        }, TestContext.Current.CancellationToken);

        // Assert — graceful degradation: 200 with null compliance fields
        ep.HttpContext.Response.StatusCode.Should().Be(200);
        ep.Response.CompliancePercent.Should().BeNull();
        ep.Response.CurrentStreak.Should().Be(0);
        ep.Response.NutritionCompliancePercent.Should().BeNull();
        ep.Response.TrainingCompliancePercent.Should().BeNull();
        ep.Response.NutritionStreak.Should().BeNull();
        ep.Response.TrainingStreak.Should().BeNull();
    }

    [Fact]
    public async Task HandleAsync_LinkedClient_NoActivePlans_StatusIsPaused()
    {
        // EmptyMongo() seeds no documents at all, so the result is reliable even though
        // PlanTestHelpers.CreateMockMongo ignores FilterDefinition — see the design review note
        // on this endpoint's status test: a seeded-plan case would be unfalsifiable on this mock
        // Mongo harness, but an unconditionally empty seed returns "no active plan" regardless of
        // whether the filter itself was honored.
        var clientUser = EntityBuilder.User.WithEmail("status@test.com")
            .WithFirstName("Status").WithLastName("Client").Build();
        var trainerProfile = EntityBuilder.ProfessionalProfile.WithId(1).WithUserId(_trainerId).Build();
        var clientProfile = EntityBuilder.ClientProfile.WithId(1).WithUser(clientUser).Build();
        var link = EntityBuilder.ClientProfessionalLink
            .WithClientProfile(clientProfile)
            .WithProfessionalProfile(trainerProfile)
            .Build();

        var db = new MockDbBuilder()
            .With(trainerProfile)
            .With(clientProfile)
            .With(link)
            .Build();

        var ep = Factory.Create<GetClientDashboardEndpoint>(
            ctx => ctx.Request.HttpContext.User = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_trainerId, AppRoles.Trainer))),
            db, _audit, _complianceService, EmptyMongo(), TimeProvider.System, PassThroughCoachDisciplineAccess.Instance,
            new EntitlementService(db, TimeProvider.System, NullLogger<EntitlementService>.Instance));

        await ep.HandleAsync(new GetClientDashboardRequest
        {
            ClientId = clientProfile.PublicId
        }, TestContext.Current.CancellationToken);

        ep.Response.Status.Should().Be(ClientListStatus.Paused);
    }

    [Fact]
    public async Task HandleAsync_ActiveNutritionPlanWithNullStartDate_StatusIsPaused()
    {
        // A lone Active plan with no StartDate is legacy/unranged data. PlanWindowResolver's
        // single-candidate fallback (used for the Goal/TargetWeightKg fields via activePlan)
        // would treat it as current, but the strict, window-only predicate this endpoint shares
        // with GetClientsEndpoint (PlanWindowResolver.ResolveCurrentPlanStrict) never counts an
        // unranged plan as current for status purposes — see GetClientsEndpointTests's
        // List_ActivePlanWithNullStartDate_NeverClassifiesActive, which pins the same rule on the
        // clients list (#1094).
        var clientUser = EntityBuilder.User.WithEmail("unranged@test.com")
            .WithFirstName("Unranged").WithLastName("Client").Build();
        var trainerProfile = EntityBuilder.ProfessionalProfile.WithId(1).WithUserId(_trainerId).Build();
        var clientProfile = EntityBuilder.ClientProfile.WithId(1).WithUser(clientUser).Build();
        var link = EntityBuilder.ClientProfessionalLink
            .WithClientProfile(clientProfile)
            .WithProfessionalProfile(trainerProfile)
            .Build();

        var db = new MockDbBuilder()
            .With(trainerProfile)
            .With(clientProfile)
            .With(link)
            .Build();

        var unrangedPlan = PlanTestHelpers.CreatePlan(clientId: clientUser.Id, status: NutritionPlanStatus.Active);
        var mongo = PlanTestHelpers.CreateMockMongo(plans: [unrangedPlan]);

        var ep = Factory.Create<GetClientDashboardEndpoint>(
            ctx => ctx.Request.HttpContext.User = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_trainerId, AppRoles.Trainer))),
            db, _audit, _complianceService, mongo, TimeProvider.System, PassThroughCoachDisciplineAccess.Instance,
            new EntitlementService(db, TimeProvider.System, NullLogger<EntitlementService>.Instance));

        await ep.HandleAsync(new GetClientDashboardRequest
        {
            ClientId = clientProfile.PublicId
        }, TestContext.Current.CancellationToken);

        ep.Response.Status.Should().Be(ClientListStatus.Paused);
    }

    [Fact]
    public async Task HandleAsync_LinkedClient_WritesAuditLog()
    {
        var clientUser = EntityBuilder.User.WithEmail("client@test.com")
            .WithFirstName("Dash").WithLastName("Client").Build();
        var trainerProfile = EntityBuilder.ProfessionalProfile.WithId(1).WithUserId(_trainerId).Build();
        var clientProfile = EntityBuilder.ClientProfile.WithId(1).WithUser(clientUser).Build();
        var link = EntityBuilder.ClientProfessionalLink
            .WithClientProfile(clientProfile)
            .WithProfessionalProfile(trainerProfile)
            .Build();

        var db = new MockDbBuilder()
            .With(trainerProfile)
            .With(clientProfile)
            .With(link)
            .Build();

        var ep = Factory.Create<GetClientDashboardEndpoint>(
            ctx => ctx.Request.HttpContext.User = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_trainerId, AppRoles.Trainer))),
            db, _audit, _complianceService, EmptyMongo(), TimeProvider.System, PassThroughCoachDisciplineAccess.Instance,
            new EntitlementService(db, TimeProvider.System, NullLogger<EntitlementService>.Instance));

        await ep.HandleAsync(new GetClientDashboardRequest
        {
            ClientId = clientProfile.PublicId
        }, TestContext.Current.CancellationToken);

        await _audit.Received(1).LogAsync(
            _trainerId,
            "Read",
            "ClientProfile",
            clientProfile.PublicId,
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_MongoPlanQueryThrows_ExceptionPropagates()
    {
        var clientUser = EntityBuilder.User.WithEmail("mongo-down@test.com").Build();
        var trainerProfile = EntityBuilder.ProfessionalProfile.WithId(7).WithUserId(_trainerId).Build();
        var clientProfile = EntityBuilder.ClientProfile.WithId(7).WithUser(clientUser).Build();
        var link = EntityBuilder.ClientProfessionalLink
            .WithId(107)
            .WithClientProfile(clientProfile)
            .WithProfessionalProfile(trainerProfile)
            .WithCanViewNutritionPlans(true)
            .Build();

        var db = new MockDbBuilder()
            .With(trainerProfile)
            .With(clientProfile)
            .With(link)
            .Build();

        var mongo = Substitute.For<IMongoContext>();
        mongo.NutritionPlans.Returns(_ => throw new MongoDB.Driver.MongoException("mongo is down"));

        var ep = Factory.Create<GetClientDashboardEndpoint>(
            ctx => ctx.Request.HttpContext.User = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_trainerId, AppRoles.Trainer))),
            db, _audit, _complianceService, mongo, TimeProvider.System, PassThroughCoachDisciplineAccess.Instance,
            new EntitlementService(db, TimeProvider.System, NullLogger<EntitlementService>.Instance));

        var act = () => ep.HandleAsync(
            new GetClientDashboardRequest { ClientId = clientProfile.PublicId },
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<MongoDB.Driver.MongoException>();
    }
}
