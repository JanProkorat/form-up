using System.Security.Claims;
using FastEndpoints;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.ClientNutrition.GetTodayLog;
using FormUp.Application.Features.ClientNutrition.GetTodayPlan;
using FormUp.Application.Features.ClientNutrition.GetWeekPlan;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Services;
using FormUp.Tests.Builders;
using FormUp.Tests.Endpoints.NutritionPlans;
using FormUp.Tests.Infrastructure;
using MongoDB.Driver;
using NSubstitute;

namespace FormUp.Tests.Endpoints.ClientNutrition;

/// <summary>
/// Tests for <see cref="GetTodayLogEndpoint"/>.
/// </summary>
public class GetTodayLogEndpointTests
{
    private readonly Guid _clientId = Guid.NewGuid();

    /// <summary>
    /// Shared fake so tests can assert on <see cref="FakeBlobStorageService.SignedUrlRequests"/> —
    /// which stored BlobUrls were routed through signing before the response was sent (F9).
    /// </summary>
    private readonly FakeBlobStorageService _blobStorage = new();

    private IApplicationDbContext CreateMockDb() =>
        new MockDbBuilder()
            .With(new ClientProfile { UserId = _clientId, PublicId = _clientId })
            .Build();

    [Fact]
    public async Task HandleAsync_WithLogs_ReturnsTotals()
    {
        var mealId = Guid.NewGuid();
        var food = PlanTestHelpers.CreateMealFood(
            foodName: "Chicken",
            amountGrams: 200,
            kcal: 165,
            protein: 31,
            carbs: 0,
            fat: 3.6m);
        var meal = PlanTestHelpers.CreateMeal(mealId: mealId, kind: MealKind.Lunch, foods: food);

        var plan = PlanTestHelpers.CreatePlan(
            clientId: _clientId,
            status: NutritionPlanStatus.Active,
            globalSettings: new GlobalNutritionSettings
            {
                DailyKcal = 2000,
                ProteinGrams = 150,
                CarbsGrams = 250,
                FatGrams = 60
            });
        plan.StartDate = DateTime.UtcNow;
        plan.Weeks[0].Days[0].Meals.Add(meal);

        var logs = new List<MealLog>
        {
            new()
            {
                ClientId = _clientId,
                PlanId = plan.ExternalId,
                MealId = mealId,
                EatenAt = DateTime.UtcNow,
                FoodsEaten = [food]
            }
        };

        var mongo = PlanTestHelpers.CreateMockMongo(plans: [plan]);

        // Mock MealLogs collection
        var mealLogCollection = Substitute.For<IMongoCollection<MealLog>>();
        mealLogCollection.FindAsync(
                Arg.Any<FilterDefinition<MealLog>>(),
                Arg.Any<FindOptions<MealLog, MealLog>>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => CreateMealLogCursor(logs));
        mongo.MealLogs.Returns(mealLogCollection);

        var db = CreateMockDb();

        var ep = Factory.Create<GetTodayLogEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_clientId, AppRoles.Client))),
            mongo, db, _blobStorage, TimeProvider.System, new MacroCalculatorService());

        await ep.HandleAsync(TestContext.Current.CancellationToken);

        ep.Response.Should().NotBeNull();
        ep.Response.MealsEaten.Should().HaveCount(1);
        ep.Response.MealsEaten[0].MealName.Should().Be("Lunch");

        // 200g chicken at 165kcal/100g = 330 kcal
        ep.Response.TotalConsumed.Kcal.Should().Be(330m);
        // 200g at 31g protein/100g = 62
        ep.Response.TotalConsumed.Protein.Should().Be(62m);

        ep.Response.Remaining.Should().NotBeNull();
        ep.Response.Remaining!.Kcal.Should().Be(2000m - 330m);
    }

    [Fact]
    public async Task HandleAsync_NoLogs_ReturnsEmptyTotals()
    {
        var plan = PlanTestHelpers.CreatePlan(
            clientId: _clientId,
            status: NutritionPlanStatus.Active);
        plan.StartDate = DateTime.UtcNow;

        var mongo = PlanTestHelpers.CreateMockMongo(plans: [plan]);

        var mealLogCollection = Substitute.For<IMongoCollection<MealLog>>();
        mealLogCollection.FindAsync(
                Arg.Any<FilterDefinition<MealLog>>(),
                Arg.Any<FindOptions<MealLog, MealLog>>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => CreateMealLogCursor([]));
        mongo.MealLogs.Returns(mealLogCollection);

        var db = CreateMockDb();

        var ep = Factory.Create<GetTodayLogEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_clientId, AppRoles.Client))),
            mongo, db, _blobStorage, TimeProvider.System, new MacroCalculatorService());

        await ep.HandleAsync(TestContext.Current.CancellationToken);

        ep.Response.Should().NotBeNull();
        ep.Response.MealsEaten.Should().BeEmpty();
        ep.Response.TotalConsumed.Kcal.Should().Be(0);
        ep.Response.TotalConsumed.Protein.Should().Be(0);
    }

    [Fact]
    public async Task HandleAsync_LogWithPhotosAndNote_RoundTripsInResponse()
    {
        var mealId = Guid.NewGuid();
        var food = PlanTestHelpers.CreateMealFood(foodName: "Salmon", amountGrams: 150, kcal: 208);
        var meal = PlanTestHelpers.CreateMeal(mealId: mealId, kind: MealKind.Dinner, foods: food);

        var plan = PlanTestHelpers.CreatePlan(
            clientId: _clientId,
            status: NutritionPlanStatus.Active);
        plan.StartDate = DateTime.UtcNow;
        plan.Weeks[0].Days[0].Meals.Add(meal);

        var uploadedAt = DateTime.UtcNow.AddMinutes(-5);
        var log = new MealLog
        {
            ClientId = _clientId,
            PlanId = plan.ExternalId,
            MealId = mealId,
            EatenAt = DateTime.UtcNow,
            FoodsEaten = [food],
            Photos =
            [
                new MealPhoto { BlobUrl = "https://minio.local/bucket/photo1.jpg", UploadedAt = uploadedAt },
                new MealPhoto { BlobUrl = "https://minio.local/bucket/photo2.jpg", UploadedAt = uploadedAt }
            ],
            Note = "Great post-workout dinner"
        };

        var mongo = PlanTestHelpers.CreateMockMongo(plans: [plan]);

        var mealLogCollection = Substitute.For<IMongoCollection<MealLog>>();
        mealLogCollection.FindAsync(
                Arg.Any<FilterDefinition<MealLog>>(),
                Arg.Any<FindOptions<MealLog, MealLog>>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => CreateMealLogCursor([log]));
        mongo.MealLogs.Returns(mealLogCollection);

        var db = CreateMockDb();

        var ep = Factory.Create<GetTodayLogEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_clientId, AppRoles.Client))),
            mongo, db, _blobStorage, TimeProvider.System, new MacroCalculatorService());

        await ep.HandleAsync(TestContext.Current.CancellationToken);

        ep.Response.Should().NotBeNull();
        ep.Response.MealsEaten.Should().HaveCount(1);

        var dto = ep.Response.MealsEaten[0];
        dto.Photos.Should().HaveCount(2);

        // Positive control: both stored BlobUrls reached the signing call verbatim.
        _blobStorage.SignedUrlRequests.Should().Contain("https://minio.local/bucket/photo1.jpg");
        _blobStorage.SignedUrlRequests.Should().Contain("https://minio.local/bucket/photo2.jpg");

        // Negative control: DisplayUrl carries the signed marker — the bucket no longer grants
        // public read on diary/* (F9) — while BlobUrl stays the canonical, permanent identity
        // value so a client can safely echo it back on a later SaveMealPhotos call.
        dto.Photos[0].DisplayUrl.Should().Be("https://minio.local/bucket/photo1.jpg?signed=test");
        dto.Photos[0].BlobUrl.Should().Be("https://minio.local/bucket/photo1.jpg");
        dto.Photos[0].UploadedAt.Should().Be(uploadedAt);
        dto.Photos[1].DisplayUrl.Should().Be("https://minio.local/bucket/photo2.jpg?signed=test");
        dto.Note.Should().Be("Great post-workout dinner");
    }

    [Fact]
    public async Task HandleAsync_LogWithoutPhotosOrNote_ReturnsEmptyPhotosAndNullNote()
    {
        var mealId = Guid.NewGuid();
        var food = PlanTestHelpers.CreateMealFood(foodName: "Toast");
        var meal = PlanTestHelpers.CreateMeal(mealId: mealId, kind: MealKind.Breakfast, foods: food);

        var plan = PlanTestHelpers.CreatePlan(
            clientId: _clientId,
            status: NutritionPlanStatus.Active);
        plan.StartDate = DateTime.UtcNow;
        plan.Weeks[0].Days[0].Meals.Add(meal);

        var log = new MealLog
        {
            ClientId = _clientId,
            PlanId = plan.ExternalId,
            MealId = mealId,
            EatenAt = DateTime.UtcNow,
            FoodsEaten = [food]
            // No Photos, no Note — represents a legacy or quick-log entry
        };

        var mongo = PlanTestHelpers.CreateMockMongo(plans: [plan]);

        var mealLogCollection = Substitute.For<IMongoCollection<MealLog>>();
        mealLogCollection.FindAsync(
                Arg.Any<FilterDefinition<MealLog>>(),
                Arg.Any<FindOptions<MealLog, MealLog>>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => CreateMealLogCursor([log]));
        mongo.MealLogs.Returns(mealLogCollection);

        var db = CreateMockDb();

        var ep = Factory.Create<GetTodayLogEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_clientId, AppRoles.Client))),
            mongo, db, _blobStorage, TimeProvider.System, new MacroCalculatorService());

        await ep.HandleAsync(TestContext.Current.CancellationToken);

        ep.Response.Should().NotBeNull();
        ep.Response.MealsEaten.Should().HaveCount(1);

        var dto = ep.Response.MealsEaten[0];
        dto.Photos.Should().BeEmpty();
        dto.Note.Should().BeNull();
    }

    [Fact]
    public async Task HandleAsync_PhotoOnlyLog_NullEatenAt_AppearsInResponse()
    {
        // Regression: GetTodayLog previously filtered by EatenAt >= today which excluded
        // photo-only logs whose EatenAt is null. Verify the endpoint now returns them.
        var mealId = Guid.NewGuid();
        var food = PlanTestHelpers.CreateMealFood(foodName: "Yoghurt");
        var meal = PlanTestHelpers.CreateMeal(mealId: mealId, kind: MealKind.AfternoonSnack, foods: food);

        var plan = PlanTestHelpers.CreatePlan(
            clientId: _clientId,
            status: NutritionPlanStatus.Active);
        plan.StartDate = DateTime.UtcNow;
        plan.Weeks[0].Days[0].Meals.Add(meal);

        var uploadedAt = DateTime.UtcNow.AddMinutes(-10);
        // Photo-only log: no EatenAt, LogDate = today, created by SaveMealPhotos
        var photoOnlyLog = new MealLog
        {
            ClientId = _clientId,
            PlanId = plan.ExternalId,
            MealId = mealId,
            LogDate = DateTime.UtcNow.Date,
            EatenAt = null,
            FoodsEaten = [food],
            Photos = [new MealPhoto { BlobUrl = "https://minio.local/bucket/snack.jpg", UploadedAt = uploadedAt }],
            Note = "afternoon snack photo"
        };

        var mongo = PlanTestHelpers.CreateMockMongo(plans: [plan]);

        var mealLogCollection = Substitute.For<IMongoCollection<MealLog>>();
        mealLogCollection.FindAsync(
                Arg.Any<FilterDefinition<MealLog>>(),
                Arg.Any<FindOptions<MealLog, MealLog>>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => CreateMealLogCursor([photoOnlyLog]));
        mongo.MealLogs.Returns(mealLogCollection);

        var db = CreateMockDb();

        var ep = Factory.Create<GetTodayLogEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_clientId, AppRoles.Client))),
            mongo, db, _blobStorage, TimeProvider.System, new MacroCalculatorService());

        await ep.HandleAsync(TestContext.Current.CancellationToken);

        ep.Response.Should().NotBeNull();
        ep.Response.MealsEaten.Should().HaveCount(1);

        var dto = ep.Response.MealsEaten[0];
        dto.EatenAt.Should().BeNull();
        dto.Photos.Should().HaveCount(1);
        dto.Photos[0].DisplayUrl.Should().Be("https://minio.local/bucket/snack.jpg?signed=test");
        dto.Photos[0].BlobUrl.Should().Be("https://minio.local/bucket/snack.jpg");
        dto.Photos[0].UploadedAt.Should().Be(uploadedAt);
        dto.Note.Should().Be("afternoon snack photo");
    }

    [Fact]
    public async Task HandleAsync_PhotoWithNote_PopulatesMealPhotoDtoNote()
    {
        // A MealPhoto that carries a per-photo Note must surface in the MealPhotoDto.Note field.
        var mealId = Guid.NewGuid();
        var food = PlanTestHelpers.CreateMealFood(foodName: "Smoothie");
        var meal = PlanTestHelpers.CreateMeal(mealId: mealId, kind: MealKind.MorningSnack, foods: food);

        var plan = PlanTestHelpers.CreatePlan(
            clientId: _clientId,
            status: NutritionPlanStatus.Active);
        plan.StartDate = DateTime.UtcNow;
        plan.Weeks[0].Days[0].Meals.Add(meal);

        var uploadedAt = DateTime.UtcNow.AddMinutes(-15);
        var log = new MealLog
        {
            ClientId = _clientId,
            PlanId = plan.ExternalId,
            MealId = mealId,
            LogDate = DateTime.UtcNow.Date,
            EatenAt = null,
            FoodsEaten = [food],
            Photos =
            [
                new MealPhoto
                {
                    BlobUrl = "https://minio.local/bucket/smoothie.jpg",
                    UploadedAt = uploadedAt,
                    Note = "Blueberry variant"
                },
                new MealPhoto
                {
                    BlobUrl = "https://minio.local/bucket/smoothie2.jpg",
                    UploadedAt = uploadedAt,
                    Note = null
                }
            ],
            Note = null
        };

        var mongo = PlanTestHelpers.CreateMockMongo(plans: [plan]);

        var mealLogCollection = Substitute.For<IMongoCollection<MealLog>>();
        mealLogCollection.FindAsync(
                Arg.Any<FilterDefinition<MealLog>>(),
                Arg.Any<FindOptions<MealLog, MealLog>>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => CreateMealLogCursor([log]));
        mongo.MealLogs.Returns(mealLogCollection);

        var db = CreateMockDb();

        var ep = Factory.Create<GetTodayLogEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_clientId, AppRoles.Client))),
            mongo, db, _blobStorage, TimeProvider.System, new MacroCalculatorService());

        await ep.HandleAsync(TestContext.Current.CancellationToken);

        ep.Response.Should().NotBeNull();
        ep.Response.MealsEaten.Should().HaveCount(1);

        var dto = ep.Response.MealsEaten[0];
        dto.Photos.Should().HaveCount(2);
        dto.Photos[0].DisplayUrl.Should().Be("https://minio.local/bucket/smoothie.jpg?signed=test");
        dto.Photos[0].BlobUrl.Should().Be("https://minio.local/bucket/smoothie.jpg");
        dto.Photos[0].Note.Should().Be("Blueberry variant");
        dto.Photos[1].DisplayUrl.Should().Be("https://minio.local/bucket/smoothie2.jpg?signed=test");
        dto.Photos[1].Note.Should().BeNull();
    }

    private static IAsyncCursor<MealLog> CreateMealLogCursor(List<MealLog> logs)
    {
        var cursor = Substitute.For<IAsyncCursor<MealLog>>();
        var moved = false;
        cursor.Current.Returns(logs);
        cursor.MoveNext(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (moved) return false;
            moved = true;
            return logs.Count > 0;
        });
        cursor.MoveNextAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (moved) return false;
            moved = true;
            return logs.Count > 0;
        });
        return cursor;
    }
}
