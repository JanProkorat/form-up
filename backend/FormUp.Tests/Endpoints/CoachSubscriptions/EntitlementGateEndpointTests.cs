using System.Security.Claims;
using FastEndpoints;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.NutritionPlans.CopyPlan;
using FormUp.Application.Features.NutritionPlans.CreatePlan;
using FormUp.Application.Features.NutritionPlans.PublishWeek;
using FormUp.Application.Features.NutritionPlans.UpdatePlan;
using FormUp.Application.Features.Trainers.AssignQuestionnaire;
using FormUp.Application.Features.Trainers.ReplaceQuestionnaire;
using FormUp.Application.Features.TrainingPlans.CreateTrainingPlan;
using FormUp.Application.Features.TrainingPlans.PublishTrainingWeek;
using FormUp.Application.Features.TrainingPlans.UpdateTrainingPlan;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Builders;
using FormUp.Tests.Endpoints.NutritionPlans;
using FormUp.Tests.Endpoints.TrainingPlans;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace FormUp.Tests.Endpoints.CoachSubscriptions;

/// <summary>
/// Every endpoint gated on a subscription feature answers 403 SUBSCRIPTION_REQUIRED when the coach's plan lacks
/// that feature. The plan endpoints check it after their lookup, so each case first lets the lookup succeed (and
/// one case per update endpoint shows a missing plan still answers 404). The two instantiate endpoints are covered
/// in <see cref="InstantiateEntitlementGateIntegrationTests"/>.
/// </summary>
public class EntitlementGateEndpointTests
{
    private const long ProfileId = 41;

    private readonly Guid _userId = Guid.NewGuid();

    /// <summary>A database where the caller's coach is Active on a plan with every feature off.</summary>
    private IApplicationDbContext DbOnAPlanWithoutFeatures()
    {
        var plan = new SubscriptionPlan
        {
            Id = 9,
            Code = "no-features",
            NameCs = "Plan",
            NameEn = "Plan",
            NameDe = "Plan",
            Currency = "CZK",
            BothRolesIncluded = true,
        };
        var subscription = new CoachSubscription
        {
            ProfessionalProfileId = ProfileId,
            SubscriptionPlanId = plan.Id,
            SubscriptionPlanPriceId = 1,
            SubscriptionPlan = plan,
            Status = SubscriptionStatus.Active,
        };

        return new MockDbBuilder()
            .With(EntityBuilder.ProfessionalProfile.WithId(ProfileId).WithUserId(_userId).Build())
            .With(plan)
            .With(subscription)
            .Build();
    }

    private Action<DefaultHttpContext> Caller(string role, MemoryStream body) => ctx =>
    {
        ctx.Request.HttpContext.User = new ClaimsPrincipal(
            new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(_userId, role)));
        ctx.Request.HttpContext.Response.Body = body;
    };

    private static async Task AssertSubscriptionRequiredAsync(IEndpoint endpoint, MemoryStream body)
    {
        endpoint.HttpContext.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        body.Position = 0;
        (await new StreamReader(body).ReadToEndAsync(TestContext.Current.CancellationToken))
            .Should().Contain(ErrorCodes.SubscriptionRequired);
    }

    private static IMongoContext EmptyMongo() => Substitute.For<IMongoContext>();

    private static IClientLinkAuthorizationService GrantingLinks() =>
        EndpointTestHelpers.CreateGrantingLinkAuthorizationService();

    private UpdatePlanEndpoint CreateUpdatePlanEndpoint(IMongoContext mongo, MemoryStream body) =>
        Factory.Create<UpdatePlanEndpoint>(
            Caller(AppRoles.Nutritionist, body), mongo, Substitute.For<IMacroCalculatorService>(), DbOnAPlanWithoutFeatures(),
            Substitute.For<IRealtimeNotifier>(), new PlanConcurrencyGuard(), GrantingLinks(), TimeProvider.System);

    private UpdateTrainingPlanEndpoint CreateUpdateTrainingPlanEndpoint(IMongoContext mongo, MemoryStream body) =>
        Factory.Create<UpdateTrainingPlanEndpoint>(
            Caller(AppRoles.Trainer, body), mongo, Substitute.For<ISessionLockService>(), Substitute.For<IRealtimeNotifier>(),
            new PlanConcurrencyGuard(), DbOnAPlanWithoutFeatures(), GrantingLinks());

    [Fact]
    public async Task CreatePlan_PlanWithoutCanCreatePlans_Returns403SubscriptionRequired()
    {
        var body = new MemoryStream();
        var ep = Factory.Create<CreatePlanEndpoint>(
            Caller(AppRoles.Nutritionist, body), EmptyMongo(), GrantingLinks(), DbOnAPlanWithoutFeatures());

        await ep.HandleAsync(new CreatePlanRequest(), TestContext.Current.CancellationToken);

        await AssertSubscriptionRequiredAsync(ep, body);
    }

    [Fact]
    public async Task UpdatePlan_PlanWithoutCanCreatePlans_Returns403SubscriptionRequired()
    {
        var plan = PlanTestHelpers.CreatePlan(nutritionistId: _userId);
        var body = new MemoryStream();
        var ep = CreateUpdatePlanEndpoint(PlanTestHelpers.CreateMockMongo(plans: [plan]), body);

        await ep.HandleAsync(new UpdatePlanRequest { PlanId = plan.ExternalId, Version = plan.Version }, TestContext.Current.CancellationToken);

        await AssertSubscriptionRequiredAsync(ep, body);
    }

    [Fact]
    public async Task UpdatePlan_MissingPlanOnAPlanWithoutCanCreatePlans_StillReturns404()
    {
        var ep = CreateUpdatePlanEndpoint(PlanTestHelpers.CreateMockMongo(plans: []), new MemoryStream());

        await ep.HandleAsync(new UpdatePlanRequest { PlanId = Guid.NewGuid(), Version = 1 }, TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task PublishWeek_PlanWithoutCanCreatePlans_Returns403SubscriptionRequired()
    {
        var plan = PlanTestHelpers.CreatePlan(nutritionistId: _userId);
        var body = new MemoryStream();
        var ep = Factory.Create<PublishWeekEndpoint>(
            Caller(AppRoles.Nutritionist, body), PlanTestHelpers.CreateMockMongo(plans: [plan]), DbOnAPlanWithoutFeatures(),
            Substitute.For<INotificationService>(), Substitute.For<IRealtimeNotifier>(), new PlanConcurrencyGuard(), GrantingLinks());

        await ep.HandleAsync(new PublishWeekRequest { PlanId = plan.ExternalId, WeekNumber = 1 }, TestContext.Current.CancellationToken);

        await AssertSubscriptionRequiredAsync(ep, body);
    }

    [Fact]
    public async Task CopyNutritionPlan_PlanWithoutCanCreatePlans_Returns403SubscriptionRequired()
    {
        var plan = PlanTestHelpers.CreatePlan(nutritionistId: _userId);
        var body = new MemoryStream();
        var ep = Factory.Create<CopyNutritionPlanEndpoint>(
            Caller(AppRoles.Nutritionist, body), PlanTestHelpers.CreateMockMongo(plans: [plan]), GrantingLinks(),
            DbOnAPlanWithoutFeatures(), TimeProvider.System);

        await ep.HandleAsync(new CopyNutritionPlanRequest { PlanId = plan.ExternalId }, TestContext.Current.CancellationToken);

        await AssertSubscriptionRequiredAsync(ep, body);
    }

    [Fact]
    public async Task CreateTrainingPlan_PlanWithoutCanCreatePlans_Returns403SubscriptionRequired()
    {
        var body = new MemoryStream();
        var ep = Factory.Create<CreateTrainingPlanEndpoint>(
            Caller(AppRoles.Trainer, body), EmptyMongo(), GrantingLinks(), DbOnAPlanWithoutFeatures());

        await ep.HandleAsync(new CreateTrainingPlanRequest(), TestContext.Current.CancellationToken);

        await AssertSubscriptionRequiredAsync(ep, body);
    }

    [Fact]
    public async Task UpdateTrainingPlan_PlanWithoutCanCreatePlans_Returns403SubscriptionRequired()
    {
        var plan = TrainingPlanTestHelpers.CreatePlan(trainerId: _userId);
        var body = new MemoryStream();
        var ep = CreateUpdateTrainingPlanEndpoint(TrainingPlanTestHelpers.CreateMockMongo(plan), body);

        await ep.HandleAsync(new UpdateTrainingPlanRequest { PlanId = plan.ExternalId, Version = plan.Version }, TestContext.Current.CancellationToken);

        await AssertSubscriptionRequiredAsync(ep, body);
    }

    [Fact]
    public async Task UpdateTrainingPlan_MissingPlanOnAPlanWithoutCanCreatePlans_StillReturns404()
    {
        var ep = CreateUpdateTrainingPlanEndpoint(TrainingPlanTestHelpers.CreateMockMongo(), new MemoryStream());

        await ep.HandleAsync(new UpdateTrainingPlanRequest { PlanId = Guid.NewGuid(), Version = 1 }, TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task PublishTrainingWeek_PlanWithoutCanCreatePlans_Returns403SubscriptionRequired()
    {
        var plan = TrainingPlanTestHelpers.CreatePlan(trainerId: _userId);
        var body = new MemoryStream();
        var ep = Factory.Create<PublishTrainingWeekEndpoint>(
            Caller(AppRoles.Trainer, body), TrainingPlanTestHelpers.CreateMockMongo(plan), DbOnAPlanWithoutFeatures(),
            Substitute.For<INotificationService>(), Substitute.For<IRealtimeNotifier>(), Substitute.For<ISessionLockService>(),
            new PlanConcurrencyGuard(), GrantingLinks());

        await ep.HandleAsync(new PublishTrainingWeekRequest { PlanId = plan.ExternalId, WeekNumber = 1 }, TestContext.Current.CancellationToken);

        await AssertSubscriptionRequiredAsync(ep, body);
    }

    [Fact]
    public async Task AssignQuestionnaire_PlanWithoutCanSendQuestionnaires_Returns403SubscriptionRequired()
    {
        var body = new MemoryStream();
        var ep = Factory.Create<AssignQuestionnaireEndpoint>(
            Caller(AppRoles.Trainer, body), DbOnAPlanWithoutFeatures(), Substitute.For<IRealtimeNotifier>(),
            Substitute.For<INotificationService>(), Substitute.For<ILogger<AssignQuestionnaireEndpoint>>());

        await ep.HandleAsync(new AssignQuestionnaireRequest(), TestContext.Current.CancellationToken);

        await AssertSubscriptionRequiredAsync(ep, body);
    }

    [Fact]
    public async Task ReplaceQuestionnaire_PlanWithoutCanSendQuestionnaires_Returns403SubscriptionRequired()
    {
        var body = new MemoryStream();
        var ep = Factory.Create<ReplaceQuestionnaireEndpoint>(
            Caller(AppRoles.Trainer, body), DbOnAPlanWithoutFeatures(), Substitute.For<IRealtimeNotifier>(),
            Substitute.For<INotificationService>(), Substitute.For<ILogger<ReplaceQuestionnaireEndpoint>>());

        await ep.HandleAsync(new ReplaceQuestionnaireRequest(), TestContext.Current.CancellationToken);

        await AssertSubscriptionRequiredAsync(ep, body);
    }
}
