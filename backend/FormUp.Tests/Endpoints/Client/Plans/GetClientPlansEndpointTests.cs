using System.Security.Claims;
using FastEndpoints;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.Client.Plans.GetClientPlans;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Application.Infrastructure.Services;
using FormUp.Tests.Builders;
using FormUp.Tests.Endpoints.NutritionPlans;
using FormUp.Tests.Endpoints.TrainingPlans;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;
using NSubstitute;

namespace FormUp.Tests.Endpoints.Client.Plans;

/// <summary>
/// Unit tests for <see cref="GetClientPlansEndpoint"/>.
/// </summary>
public class GetClientPlansEndpointTests
{
    private readonly Guid _clientId = Guid.NewGuid();
    private readonly Guid _userId = Guid.NewGuid();

    private IApplicationDbContext CreateMockDb() =>
        new MockDbBuilder()
            .With(new ClientProfile { UserId = _userId, PublicId = _clientId })
            .Build();

    private GetClientPlansEndpoint CreateEndpoint(
        IMongoContext mongo,
        IApplicationDbContext db)
    {
        return Factory.Create<GetClientPlansEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_userId, AppRoles.Client))),
            mongo, db, TimeProvider.System, new MacroCalculatorService(),
            new EntitlementService(db, TimeProvider.System, NullLogger<EntitlementService>.Instance));
    }

    // ── Helpers to build mock Mongo contexts ────────────────────────────

    /// <summary>
    /// Creates a mock IMongoContext that returns the specified nutrition and training plans.
    /// Uses the public helpers from each domain's test-helpers to build the collections.
    /// </summary>
    private static IMongoContext CreateMockMongo(
        List<NutritionPlan>? nutritionPlans = null,
        List<TrainingPlan>? trainingPlans = null)
    {
        var nPlans = nutritionPlans ?? [];
        var tPlans = trainingPlans ?? [];

        // Build all collections BEFORE configuring Returns() to avoid the NSubstitute
        // "substitute inside Returns()" pitfall where an inner Substitute.For() call
        // resets the "last call" tracker on the outer substitute.
        var nCollection = BuildNutritionPlanCollection(nPlans);
        var tCollection = TrainingPlanTestHelpers.CreateMockCollection(tPlans);

        var mongo = Substitute.For<IMongoContext>();
        mongo.NutritionPlans.Returns(nCollection);
        mongo.TrainingPlans.Returns(tCollection);

        return mongo;
    }

    private static IMongoCollection<NutritionPlan> BuildNutritionPlanCollection(List<NutritionPlan> plans)
    {
        var collection = Substitute.For<IMongoCollection<NutritionPlan>>();
        var moved = false;
        var cursor = Substitute.For<IAsyncCursor<NutritionPlan>>();
        cursor.Current.Returns(plans);
        cursor.MoveNext(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (moved) return false;
            moved = true;
            return plans.Count > 0;
        });
        cursor.MoveNextAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (moved) return false;
            moved = true;
            return plans.Count > 0;
        });
        collection.FindAsync(
                Arg.Any<FilterDefinition<NutritionPlan>>(),
                Arg.Any<FindOptions<NutritionPlan, NutritionPlan>>(),
                Arg.Any<CancellationToken>())
            .Returns(cursor);
        return collection;
    }

    // ── Test: plan with Active status but zero published weeks is excluded ──

    /// <summary>
    /// When Mongo (via ElemMatch) filters out plans with no published weeks,
    /// the endpoint returns an empty list. This test simulates that: the mock
    /// returns no plans (as the real ElemMatch would), and the response is empty.
    /// </summary>
    [Fact]
    public async Task ActivePlan_WithNoPubilshedWeeks_IsNotInResponse()
    {
        // Arrange: mock returns empty collections (simulating ElemMatch excluding the plan)
        var mongo = CreateMockMongo([], []);
        var db = CreateMockDb();
        var ep = CreateEndpoint(mongo, db);

        // Act
        await ep.HandleAsync(new GetClientPlansRequest(), TestContext.Current.CancellationToken);

        // Assert
        ep.HttpContext.Response.StatusCode.Should().Be(200);
        ep.Response.Items.Should().BeEmpty();
    }

    // ── Test: plan with at least one published week IS in the response ──

    [Fact]
    public async Task ActiveNutritionPlan_WithOnePublishedWeek_IsInResponse()
    {
        // Arrange
        var plan = PlanTestHelpers.CreatePlan(
            clientId: _clientId,
            status: NutritionPlanStatus.Active,
            weekCount: 2);
        plan.Weeks[0].Status = WeekStatus.Published;
        plan.Weeks[0].DatePublished = DateTime.UtcNow.AddDays(-7);
        // week 2 stays Draft

        var mongo = CreateMockMongo([plan]);
        var db = CreateMockDb();
        var ep = CreateEndpoint(mongo, db);

        // Act
        await ep.HandleAsync(new GetClientPlansRequest(), TestContext.Current.CancellationToken);

        // Assert
        ep.Response.Items.Should().ContainSingle();
        var item = ep.Response.Items[0];
        item.PlanId.Should().Be(plan.ExternalId);
        item.Type.Should().Be("nutrition");
        item.PublishedWeekCount.Should().Be(1);
    }

    // ── Test: DailyKcal from GlobalSettings on nutrition plan ──

    [Fact]
    public async Task NutritionPlan_DailyKcalPopulated_AndHasTodaySessionIsNull()
    {
        // Arrange
        var plan = PlanTestHelpers.CreatePlan(
            clientId: _clientId,
            status: NutritionPlanStatus.Active,
            weekCount: 1,
            globalSettings: new GlobalNutritionSettings { DailyKcal = 2200 });
        plan.Weeks[0].Status = WeekStatus.Published;
        plan.Weeks[0].DatePublished = DateTime.UtcNow.AddDays(-1);

        var mongo = CreateMockMongo([plan]);
        var db = CreateMockDb();
        var ep = CreateEndpoint(mongo, db);

        // Act
        await ep.HandleAsync(new GetClientPlansRequest(), TestContext.Current.CancellationToken);

        // Assert
        ep.Response.Items.Should().ContainSingle();
        var item = ep.Response.Items[0];
        item.DailyKcal.Should().Be(2200m);
        item.HasTodaySession.Should().BeNull();
    }

    // ── Test: HasTodaySession true when today has a session ──

    [Fact]
    public async Task ActiveTrainingPlan_TodayHasSession_HasTodaySessionIsTrue()
    {
        // Arrange: today's day-of-week (1=Mon..7=Sun)
        var todayDow = (int)DateTime.UtcNow.DayOfWeek;
        todayDow = todayDow == 0 ? 7 : todayDow;

        var plan = TrainingPlanTestHelpers.CreatePlan(
            clientId: _clientId,
            status: TrainingPlanStatus.Active,
            weekCount: 1);
        plan.Weeks[0].Status = WeekStatus.Published;
        plan.Weeks[0].DatePublished = DateTime.UtcNow.AddDays(-1);
        plan.Weeks[0].Days.First(d => d.DayOfWeek == todayDow).Sessions.Add(new TrainingSession
        {
            SessionId = Guid.NewGuid(),
            Name = "Push Day",
            Order = 1
        });

        var mongo = CreateMockMongo(trainingPlans: [plan]);
        var db = CreateMockDb();
        var ep = CreateEndpoint(mongo, db);

        // Act
        await ep.HandleAsync(new GetClientPlansRequest(), TestContext.Current.CancellationToken);

        // Assert
        ep.Response.Items.Should().ContainSingle();
        var item = ep.Response.Items[0];
        item.Type.Should().Be("training");
        item.HasTodaySession.Should().BeTrue();
        item.DailyKcal.Should().BeNull();
    }

    // ── Test: HasTodaySession false when today is a rest day ──

    [Fact]
    public async Task ActiveTrainingPlan_TodayIsRestDay_HasTodaySessionIsFalse()
    {
        // Arrange: put the session on a day that is NOT today
        var todayDow = (int)DateTime.UtcNow.DayOfWeek;
        todayDow = todayDow == 0 ? 7 : todayDow;
        var sessionDow = todayDow == 7 ? 1 : todayDow + 1; // a different day

        var plan = TrainingPlanTestHelpers.CreatePlan(
            clientId: _clientId,
            status: TrainingPlanStatus.Active,
            weekCount: 1);
        plan.Weeks[0].Status = WeekStatus.Published;
        plan.Weeks[0].DatePublished = DateTime.UtcNow.AddDays(-1);
        plan.Weeks[0].Days.First(d => d.DayOfWeek == sessionDow).Sessions.Add(new TrainingSession
        {
            SessionId = Guid.NewGuid(),
            Name = "Leg Day",
            Order = 1
        });

        var mongo = CreateMockMongo(trainingPlans: [plan]);
        var db = CreateMockDb();
        var ep = CreateEndpoint(mongo, db);

        // Act
        await ep.HandleAsync(new GetClientPlansRequest(), TestContext.Current.CancellationToken);

        // Assert
        ep.Response.Items.Should().ContainSingle();
        var item = ep.Response.Items[0];
        item.HasTodaySession.Should().BeFalse();
    }

    // ── Test: plan with future StartDate → CurrentWeek is null ──

    [Fact]
    public async Task NutritionPlan_FutureStartDate_CurrentWeekIsNull()
    {
        // Arrange: StartDate is tomorrow — plan hasn't started yet
        var plan = PlanTestHelpers.CreatePlan(
            clientId: _clientId,
            status: NutritionPlanStatus.Active,
            weekCount: 4);
        plan.StartDate = DateTime.UtcNow.Date.AddDays(7); // future
        plan.Weeks[0].Status = WeekStatus.Published;
        plan.Weeks[0].DatePublished = DateTime.UtcNow.AddDays(-1);

        var mongo = CreateMockMongo([plan]);
        var db = CreateMockDb();
        var ep = CreateEndpoint(mongo, db);

        // Act
        await ep.HandleAsync(new GetClientPlansRequest(), TestContext.Current.CancellationToken);

        // Assert: plan is still listed (has a published week), but CurrentWeek is null
        ep.Response.Items.Should().ContainSingle();
        var item = ep.Response.Items[0];
        item.CurrentWeek.Should().BeNull();
        item.PublishedWeekCount.Should().Be(1);
    }

    // ── Test: status=Completed filter works (regression guard) ──

    [Fact]
    public async Task StatusCompletedFilter_ReturnsCompletedPlans()
    {
        // Arrange: one completed nutrition plan with a published week
        var completedPlan = PlanTestHelpers.CreatePlan(
            clientId: _clientId,
            status: NutritionPlanStatus.Completed,
            weekCount: 1);
        completedPlan.Weeks[0].Status = WeekStatus.Published;
        completedPlan.Weeks[0].DatePublished = DateTime.UtcNow.AddDays(-14);
        completedPlan.DateCompleted = DateTime.UtcNow.AddDays(-7);

        var mongo = CreateMockMongo([completedPlan]);
        var db = CreateMockDb();

        // Act: filter by "Completed"
        var ep = CreateEndpoint(mongo, db);
        await ep.HandleAsync(new GetClientPlansRequest { Status = "Completed" }, TestContext.Current.CancellationToken);

        // Assert
        ep.Response.Items.Should().ContainSingle();
        var item = ep.Response.Items[0];
        item.Status.Should().Be("Completed");
        item.DateCompleted.Should().NotBeNull();
    }

    // ── Test: CurrentWeek is populated when StartDate is in the past ──

    [Fact]
    public async Task TrainingPlan_StartDateInPast_CurrentWeekIsPopulated()
    {
        // Arrange: plan started 8 days ago → current week = 2
        var plan = TrainingPlanTestHelpers.CreatePlan(
            clientId: _clientId,
            status: TrainingPlanStatus.Active,
            weekCount: 4);
        plan.StartDate = DateTime.UtcNow.Date.AddDays(-8); // 8 days ago → week 2
        plan.Weeks[0].Status = WeekStatus.Published;
        plan.Weeks[0].DatePublished = DateTime.UtcNow.AddDays(-8);
        plan.Weeks[1].Status = WeekStatus.Published;
        plan.Weeks[1].DatePublished = DateTime.UtcNow.AddDays(-1);

        var mongo = CreateMockMongo(trainingPlans: [plan]);
        var db = CreateMockDb();
        var ep = CreateEndpoint(mongo, db);

        // Act
        await ep.HandleAsync(new GetClientPlansRequest(), TestContext.Current.CancellationToken);

        // Assert
        ep.Response.Items.Should().ContainSingle();
        var item = ep.Response.Items[0];
        item.CurrentWeek.Should().Be(2);
    }

    // ── Test: disambiguation across Active same-type siblings (#873) ──

    /// <summary>
    /// Reproduces the #873 disagreement: a client holds two Active training plans of the same
    /// type — one ranged (has a <c>StartDate</c> whose window covers today) and one unranged
    /// (legacy data, no <c>StartDate</c>). Both independently have a session scheduled on
    /// today's day-of-week. <see cref="FormUp.Application.Domain.Services.PlanWindowResolver.ResolveCurrentPlan{T}"/>
    /// — the same helper <c>GetTodaySessionEndpoint</c> uses to pick one Active plan as "current"
    /// — selects only the ranged plan here (it's the only in-window candidate). The unranged
    /// sibling must NOT independently report a live session even though its own week-cycle
    /// formula resolves to a week with a session today — before the fix, it did.
    /// </summary>
    [Fact]
    public async Task ActiveTrainingPlans_UnrangedSiblingOfSelectedRangedPlan_ReportsHasTodaySessionFalse()
    {
        // Arrange
        var todayDow = (int)DateTime.UtcNow.DayOfWeek;
        todayDow = todayDow == 0 ? 7 : todayDow;

        // Ranged plan — StartDate is today, one-week window covers today. This is the plan
        // ResolveCurrentPlan must select.
        var rangedPlan = TrainingPlanTestHelpers.CreatePlan(
            clientId: _clientId,
            status: TrainingPlanStatus.Active,
            weekCount: 1);
        rangedPlan.StartDate = DateTime.UtcNow.Date;
        rangedPlan.Weeks[0].Status = WeekStatus.Published;
        rangedPlan.Weeks[0].DatePublished = DateTime.UtcNow.AddDays(-1);
        rangedPlan.Weeks[0].Days.First(d => d.DayOfWeek == todayDow).Sessions.Add(new TrainingSession
        {
            SessionId = Guid.NewGuid(),
            Name = "Ranged Plan Session",
            Order = 1
        });

        // Unranged sibling — no StartDate, single published week with a session on today's
        // day-of-week. Its own legacy week-cycle formula always resolves to week 1 (the only
        // published week), so — absent disambiguation — it would independently report a live
        // session too.
        var unrangedPlan = TrainingPlanTestHelpers.CreatePlan(
            clientId: _clientId,
            status: TrainingPlanStatus.Active,
            weekCount: 1);
        unrangedPlan.Weeks[0].Status = WeekStatus.Published;
        unrangedPlan.Weeks[0].DatePublished = DateTime.UtcNow.AddDays(-1);
        unrangedPlan.Weeks[0].Days.First(d => d.DayOfWeek == todayDow).Sessions.Add(new TrainingSession
        {
            SessionId = Guid.NewGuid(),
            Name = "Unranged Plan Session",
            Order = 1
        });

        var mongo = CreateMockMongo(trainingPlans: [rangedPlan, unrangedPlan]);
        var db = CreateMockDb();
        var ep = CreateEndpoint(mongo, db);

        // Act
        await ep.HandleAsync(new GetClientPlansRequest(), TestContext.Current.CancellationToken);

        // Assert
        ep.Response.Items.Should().HaveCount(2);

        var rangedItem = ep.Response.Items.Single(i => i.PlanId == rangedPlan.ExternalId);
        rangedItem.HasTodaySession.Should().BeTrue();

        var unrangedItem = ep.Response.Items.Single(i => i.PlanId == unrangedPlan.ExternalId);
        unrangedItem.HasTodaySession.Should().BeFalse();
        // CurrentWeek stays informational even though this plan wasn't selected as "current".
        unrangedItem.CurrentWeek.Should().Be(1);
    }

    // ── Per-day nutrition averages ──

    private static void AddMeal(PlanWeek week, int dayOfWeek, decimal kcalPer100, decimal protein, decimal carbs, decimal fat, decimal grams = 100)
    {
        week.Days.First(d => d.DayOfWeek == dayOfWeek).Meals.Add(PlanTestHelpers.CreateMeal(
            foods: PlanTestHelpers.CreateMealFood(
                amountGrams: grams, kcal: kcalPer100, protein: protein, carbs: carbs, fat: fat)));
    }

    private async Task<ClientOwnPlanItem> GetSingleItemAsync(NutritionPlan plan)
    {
        var ep = CreateEndpoint(CreateMockMongo([plan]), CreateMockDb());
        await ep.HandleAsync(new GetClientPlansRequest(), TestContext.Current.CancellationToken);
        return ep.Response.Items.Single();
    }

    [Fact]
    public async Task NutritionPlan_AveragesUsePublishedWeeksAndSkipEmptyDays()
    {
        var plan = PlanTestHelpers.CreatePlan(clientId: _clientId, status: NutritionPlanStatus.Active, weekCount: 2);
        plan.Weeks[0].Status = WeekStatus.Published;
        // Two published days with meals; the other five published days are empty and must not dilute.
        AddMeal(plan.Weeks[0], 1, kcalPer100: 100, protein: 10, carbs: 20, fat: 5);
        AddMeal(plan.Weeks[0], 2, kcalPer100: 201, protein: 11, carbs: 21, fat: 6);
        // Draft week is ignored even though it has a big meal.
        AddMeal(plan.Weeks[1], 1, kcalPer100: 5000, protein: 500, carbs: 500, fat: 500);

        var item = await GetSingleItemAsync(plan);

        item.AvgKcalPerDay.Should().Be(151m); // (100 + 201) / 2 = 150.5 -> away from zero
        item.AvgProteinPerDay.Should().Be(11m); // 10.5 -> 11
        item.AvgCarbsPerDay.Should().Be(21m); // 20.5 -> 21
        item.AvgFatPerDay.Should().Be(6m); // 5.5 -> 6
        item.AvgFiberPerDay.Should().Be(0m);
    }

    [Fact]
    public async Task NutritionPlan_PublishedDaysWithoutMeals_AveragesAreNullNotZero()
    {
        var plan = PlanTestHelpers.CreatePlan(clientId: _clientId, status: NutritionPlanStatus.Active, weekCount: 1);
        plan.Weeks[0].Status = WeekStatus.Published;

        var item = await GetSingleItemAsync(plan);

        item.AvgKcalPerDay.Should().BeNull();
        item.AvgProteinPerDay.Should().BeNull();
        item.AvgCarbsPerDay.Should().BeNull();
        item.AvgFatPerDay.Should().BeNull();
        item.AvgFiberPerDay.Should().BeNull();
    }

    [Fact]
    public async Task TrainingPlan_AveragesAreNull()
    {
        var plan = TrainingPlanTestHelpers.CreatePlan(clientId: _clientId, status: TrainingPlanStatus.Active, weekCount: 1);
        plan.Weeks[0].Status = WeekStatus.Published;
        var ep = CreateEndpoint(CreateMockMongo(trainingPlans: [plan]), CreateMockDb());

        await ep.HandleAsync(new GetClientPlansRequest(), TestContext.Current.CancellationToken);

        var item = ep.Response.Items.Single();
        item.AvgKcalPerDay.Should().BeNull();
        item.AvgProteinPerDay.Should().BeNull();
        item.AvgCarbsPerDay.Should().BeNull();
        item.AvgFatPerDay.Should().BeNull();
        item.AvgFiberPerDay.Should().BeNull();
    }
}
