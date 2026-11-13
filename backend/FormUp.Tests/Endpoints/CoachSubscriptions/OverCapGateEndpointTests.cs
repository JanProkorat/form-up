using System.Security.Claims;
using FastEndpoints;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.NutritionPlans.CopyPlan;
using FormUp.Application.Features.NutritionPlans.CreatePlan;
using FormUp.Application.Features.NutritionPlans.LinkQuestionnaire;
using FormUp.Application.Features.NutritionPlans.PublishWeek;
using FormUp.Application.Features.NutritionPlans.UpdatePlan;
using FormUp.Application.Features.TrainingPlans.CreateTrainingPlan;
using FormUp.Application.Features.TrainingPlans.PublishTrainingWeek;
using FormUp.Application.Features.TrainingPlans.UnlockTrainingSession;
using FormUp.Application.Features.TrainingPlans.UpdateTrainingPlan;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Application.Infrastructure.Services;
using FormUp.Tests.Builders;
using FormUp.Tests.Endpoints.NutritionPlans;
using FormUp.Tests.Endpoints.TrainingPlans;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace FormUp.Tests.Endpoints.CoachSubscriptions;

/// <summary>
/// After a paid plan lapses, plan actions for a client over the Free plan's cap answer 403 CLIENT_OVER_PLAN_LIMIT:
/// always for publish, create and copy; once the plan's last published week has ended (or at once when none is
/// published) for the edit actions, which before that still allow content edits but not a start-date move.
/// The two instantiate endpoints are covered in <see cref="OverCapIntegrationTests"/>.
/// </summary>
public class OverCapGateEndpointTests
{
    private const long CoachProfileId = 41;

    private readonly Guid _coachUserId = Guid.NewGuid();
    private readonly Guid[] _clientUserIds = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
    private readonly Guid[] _clientPublicIds = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];

    /// <summary>Clients 0 and 1 are the two longest linked and kept; client 2 is over the Free plan's cap of 2.</summary>
    private Guid KeptClientUserId => _clientUserIds[0];

    private Guid OverCapClientUserId => _clientUserIds[2];

    private static DateTime ThisMonday()
    {
        var today = DateTime.UtcNow.Date;

        return today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
    }

    /// <summary>A database where the caller's coach is Canceled on a paid plan and has three Connected clients.</summary>
    private IApplicationDbContext LapsedCoachDb()
    {
        var plan = new SubscriptionPlan
        {
            Id = 9,
            Code = "pro",
            NameCs = "Pro",
            NameEn = "Pro",
            NameDe = "Pro",
            Currency = "CZK",
            BothRolesIncluded = true,
        };
        var builder = new MockDbBuilder()
            .With(EntityBuilder.ProfessionalProfile.WithId(CoachProfileId).WithUserId(_coachUserId).Build())
            .With(plan)
            .With(new CoachSubscription
            {
                ProfessionalProfileId = CoachProfileId,
                SubscriptionPlanId = plan.Id,
                SubscriptionPlanPriceId = 1,
                SubscriptionPlan = plan,
                Status = SubscriptionStatus.Canceled,
            });

        for (var index = 0; index < _clientUserIds.Length; index++)
        {
            builder
                .With(EntityBuilder.ClientProfile
                    .WithId(index + 1)
                    .WithUserId(_clientUserIds[index])
                    .WithPublicId(_clientPublicIds[index])
                    .Build())
                .With(EntityBuilder.ClientProfessionalLink
                    .WithId(index + 1)
                    .WithClientProfileId(index + 1)
                    .WithProfessionalProfileId(CoachProfileId)
                    .WithActivatedAtUtc(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(index))
                    .Build());
        }

        return builder.Build();
    }

    private Action<DefaultHttpContext> Caller(string role, MemoryStream body) => ctx =>
    {
        ctx.Request.HttpContext.User = new ClaimsPrincipal(
            new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(_coachUserId, role)));
        ctx.Request.HttpContext.Response.Body = body;
    };

    private static async Task AssertOverCapAsync(IEndpoint endpoint, MemoryStream body)
    {
        endpoint.HttpContext.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        body.Position = 0;
        (await new StreamReader(body).ReadToEndAsync(TestContext.Current.CancellationToken))
            .Should().Contain(ErrorCodes.ClientOverPlanLimit);
    }

    private static IClientLinkAuthorizationService GrantingLinks() =>
        EndpointTestHelpers.CreateGrantingLinkAuthorizationService();

    private NutritionPlan NutritionPlanFor(Guid clientUserId, DateTime? startDate = null, bool published = false)
    {
        var plan = PlanTestHelpers.CreatePlan(nutritionistId: _coachUserId, clientId: clientUserId);
        plan.StartDate = startDate;

        if (published)
        {
            plan.Weeks[0].Status = WeekStatus.Published;
            plan.Status = NutritionPlanStatus.Active;
        }

        return plan;
    }

    private TrainingPlan TrainingPlanFor(Guid clientUserId, DateTime? startDate = null, bool published = false)
    {
        var plan = TrainingPlanTestHelpers.CreatePlan(trainerId: _coachUserId, clientId: clientUserId);
        plan.StartDate = startDate;

        if (published)
        {
            plan.Weeks[0].Status = WeekStatus.Published;
        }

        return plan;
    }

    private UpdatePlanEndpoint CreateUpdatePlanEndpoint(NutritionPlan plan, MemoryStream body) =>
        Factory.Create<UpdatePlanEndpoint>(
            Caller(AppRoles.Nutritionist, body), PlanTestHelpers.CreateMockMongo(plans: [plan]),
            Substitute.For<IMacroCalculatorService>(), LapsedCoachDb(), Substitute.For<IRealtimeNotifier>(),
            new PlanConcurrencyGuard(), GrantingLinks(), TimeProvider.System);

    private static UpdatePlanRequest SameWeekUpdate(NutritionPlan plan, DateTime? startDate) => new()
    {
        PlanId = plan.ExternalId,
        Version = plan.Version,
        Name = plan.Name,
        StartDate = startDate,
        Weeks =
        [
            new UpdateWeekRequest
            {
                WeekNumber = 1,
                Days = Enumerable.Range(1, 7).Select(day => new UpdateDayRequest { DayOfWeek = day }).ToList(),
            },
        ],
    };

    private UpdateTrainingPlanEndpoint CreateUpdateTrainingPlanEndpoint(TrainingPlan plan, MemoryStream body) =>
        Factory.Create<UpdateTrainingPlanEndpoint>(
            Caller(AppRoles.Trainer, body), TrainingPlanTestHelpers.CreateMockMongo(plan), Substitute.For<ISessionLockService>(),
            Substitute.For<IRealtimeNotifier>(), new PlanConcurrencyGuard(), LapsedCoachDb(), GrantingLinks());

    [Fact]
    public async Task PublishWeek_ClientOverCap_Returns403()
    {
        var plan = NutritionPlanFor(OverCapClientUserId);
        var body = new MemoryStream();
        var ep = Factory.Create<PublishWeekEndpoint>(
            Caller(AppRoles.Nutritionist, body), PlanTestHelpers.CreateMockMongo(plans: [plan]), LapsedCoachDb(),
            Substitute.For<INotificationService>(), Substitute.For<IRealtimeNotifier>(), new PlanConcurrencyGuard(), GrantingLinks());

        await ep.HandleAsync(new PublishWeekRequest { PlanId = plan.ExternalId, WeekNumber = 1 }, TestContext.Current.CancellationToken);

        await AssertOverCapAsync(ep, body);
    }

    [Fact]
    public async Task PublishTrainingWeek_ClientOverCap_Returns403()
    {
        var plan = TrainingPlanFor(OverCapClientUserId);
        var body = new MemoryStream();
        var ep = Factory.Create<PublishTrainingWeekEndpoint>(
            Caller(AppRoles.Trainer, body), TrainingPlanTestHelpers.CreateMockMongo(plan), LapsedCoachDb(),
            Substitute.For<INotificationService>(), Substitute.For<IRealtimeNotifier>(), Substitute.For<ISessionLockService>(),
            new PlanConcurrencyGuard(), GrantingLinks());

        await ep.HandleAsync(new PublishTrainingWeekRequest { PlanId = plan.ExternalId, WeekNumber = 1 }, TestContext.Current.CancellationToken);

        await AssertOverCapAsync(ep, body);
    }

    [Fact]
    public async Task CreatePlan_ClientOverCap_Returns403()
    {
        var body = new MemoryStream();
        var ep = Factory.Create<CreatePlanEndpoint>(
            Caller(AppRoles.Nutritionist, body), Substitute.For<IMongoContext>(), GrantingLinks(), LapsedCoachDb());

        await ep.HandleAsync(
            new CreatePlanRequest { ClientId = _clientPublicIds[2], Name = "Plan" }, TestContext.Current.CancellationToken);

        await AssertOverCapAsync(ep, body);
    }

    [Fact]
    public async Task CreateTrainingPlan_ClientOverCap_Returns403()
    {
        var body = new MemoryStream();
        var ep = Factory.Create<CreateTrainingPlanEndpoint>(
            Caller(AppRoles.Trainer, body), Substitute.For<IMongoContext>(), GrantingLinks(), LapsedCoachDb());

        await ep.HandleAsync(
            new CreateTrainingPlanRequest { ClientId = _clientPublicIds[2], Name = "Plan" }, TestContext.Current.CancellationToken);

        await AssertOverCapAsync(ep, body);
    }

    [Fact]
    public async Task CopyNutritionPlan_TargetClientOverCap_Returns403()
    {
        var source = NutritionPlanFor(KeptClientUserId);
        var body = new MemoryStream();
        var ep = Factory.Create<CopyNutritionPlanEndpoint>(
            Caller(AppRoles.Nutritionist, body), PlanTestHelpers.CreateMockMongo(plans: [source]), GrantingLinks(),
            LapsedCoachDb(), TimeProvider.System);

        await ep.HandleAsync(
            new CopyNutritionPlanRequest { PlanId = source.ExternalId, ClientId = _clientPublicIds[2] },
            TestContext.Current.CancellationToken);

        await AssertOverCapAsync(ep, body);
    }

    [Fact]
    public async Task UpdatePlan_OverCapClientWithNoPublishedWeek_Returns403()
    {
        var plan = NutritionPlanFor(OverCapClientUserId, ThisMonday());
        var body = new MemoryStream();
        var ep = CreateUpdatePlanEndpoint(plan, body);

        await ep.HandleAsync(SameWeekUpdate(plan, plan.StartDate), TestContext.Current.CancellationToken);

        await AssertOverCapAsync(ep, body);
    }

    [Fact]
    public async Task UpdatePlan_OverCapClientAfterTheLastPublishedWeekEnded_Returns403()
    {
        var plan = NutritionPlanFor(OverCapClientUserId, ThisMonday().AddDays(-28), published: true);
        var body = new MemoryStream();
        var ep = CreateUpdatePlanEndpoint(plan, body);

        await ep.HandleAsync(SameWeekUpdate(plan, plan.StartDate), TestContext.Current.CancellationToken);

        await AssertOverCapAsync(ep, body);
    }

    [Fact]
    public async Task UpdatePlan_OverCapClientMovingTheStartDateOfAPublishedWeek_Returns403()
    {
        var plan = NutritionPlanFor(OverCapClientUserId, ThisMonday(), published: true);
        var body = new MemoryStream();
        var ep = CreateUpdatePlanEndpoint(plan, body);

        await ep.HandleAsync(SameWeekUpdate(plan, plan.StartDate!.Value.AddDays(7)), TestContext.Current.CancellationToken);

        await AssertOverCapAsync(ep, body);
    }

    [Fact]
    public async Task UpdatePlan_OverCapClientWhilePublishedWeekRuns_StillSavesContentEdits()
    {
        var plan = NutritionPlanFor(OverCapClientUserId, ThisMonday(), published: true);
        var ep = CreateUpdatePlanEndpoint(plan, new MemoryStream());

        await ep.HandleAsync(SameWeekUpdate(plan, plan.StartDate), TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task UpdatePlan_KeptClientWithNoPublishedWeek_IsNotRefused()
    {
        var plan = NutritionPlanFor(KeptClientUserId, ThisMonday());
        var ep = CreateUpdatePlanEndpoint(plan, new MemoryStream());

        await ep.HandleAsync(SameWeekUpdate(plan, plan.StartDate), TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task UpdateTrainingPlan_OverCapClientAfterTheLastPublishedWeekEnded_Returns403()
    {
        var plan = TrainingPlanFor(OverCapClientUserId, ThisMonday().AddDays(-28), published: true);
        var body = new MemoryStream();
        var ep = CreateUpdateTrainingPlanEndpoint(plan, body);

        await ep.HandleAsync(
            new UpdateTrainingPlanRequest { PlanId = plan.ExternalId, Version = plan.Version, StartDate = plan.StartDate },
            TestContext.Current.CancellationToken);

        await AssertOverCapAsync(ep, body);
    }

    [Fact]
    public async Task UpdateTrainingPlan_OverCapClientMovingTheStartDateOfAPublishedWeek_Returns403()
    {
        var plan = TrainingPlanFor(OverCapClientUserId, ThisMonday(), published: true);
        var body = new MemoryStream();
        var ep = CreateUpdateTrainingPlanEndpoint(plan, body);

        await ep.HandleAsync(
            new UpdateTrainingPlanRequest
            {
                PlanId = plan.ExternalId,
                Version = plan.Version,
                StartDate = plan.StartDate!.Value.AddDays(7),
            },
            TestContext.Current.CancellationToken);

        await AssertOverCapAsync(ep, body);
    }

    [Fact]
    public async Task LinkNutritionQuestionnaire_OverCapClientWithNoPublishedWeek_Returns403()
    {
        var plan = NutritionPlanFor(OverCapClientUserId);
        var body = new MemoryStream();
        var ep = Factory.Create<LinkQuestionnaireEndpoint>(
            Caller(AppRoles.Nutritionist, body), PlanTestHelpers.CreateMockMongo(plans: [plan]), LapsedCoachDb(),
            new PlanConcurrencyGuard(), GrantingLinks());

        await ep.HandleAsync(
            new LinkNutritionQuestionnaireRequest { PlanId = plan.ExternalId, Version = plan.Version },
            TestContext.Current.CancellationToken);

        await AssertOverCapAsync(ep, body);
    }

    [Fact]
    public async Task UnlockTrainingSession_OverCapClientAfterTheLastPublishedWeekEnded_Returns403()
    {
        var plan = TrainingPlanFor(OverCapClientUserId, ThisMonday().AddDays(-28), published: true);
        var body = new MemoryStream();
        var ep = Factory.Create<UnlockTrainingSessionEndpoint>(
            Caller(AppRoles.Trainer, body), LapsedCoachDb(), TrainingPlanTestHelpers.CreateMockMongo(plan),
            Substitute.For<ISessionLockService>(), Options.Create(new TrainingLockOptions()), Substitute.For<IRealtimeNotifier>(),
            GrantingLinks());

        await ep.HandleAsync(
            new UnlockTrainingSessionRequest { PlanId = plan.ExternalId, SessionId = Guid.NewGuid() },
            TestContext.Current.CancellationToken);

        await AssertOverCapAsync(ep, body);
    }
}
