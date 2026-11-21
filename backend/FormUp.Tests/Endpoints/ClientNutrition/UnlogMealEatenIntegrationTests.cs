using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace FormUp.Tests.Endpoints.ClientNutrition;

/// <summary>
/// Testcontainers integration tests (real MongoDB) for unmarking a meal as eaten while keeping
/// its photos. The mocked Mongo context ignores filters, so the delete/keep split, the legacy
/// LogDate backfill and the cross-client isolation can only be proven against a real database.
/// </summary>
[Collection(TestCollection.Shard4)]
public class UnlogMealEatenIntegrationTests(FitnessApiFactory factory)
{
    private const string PhotoUrl = "https://minio.local/bucket/photo1.jpg";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static DateTime Today => DateTime.UtcNow.Date;

    /// <summary>
    /// Seeds an Active plan starting today with one published week and a single 150 g / 100 kcal-per-100 g meal.
    /// </summary>
    private async Task<(Guid PlanId, Guid MealId)> SeedPlanAsync(Guid clientUserId)
    {
        var mealId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var days = Enumerable.Range(1, 7).Select(d => new PlanDay { DayOfWeek = d, Meals = [] }).ToList();
        foreach (var day in days)
        {
            day.Meals.Add(new PlanMeal
            {
                MealId = mealId,
                Kind = MealKind.Lunch,
                Order = 1,
                Foods =
                [
                    new MealFood
                    {
                        FoodExternalId = Guid.NewGuid(),
                        FoodName = "Rice",
                        NutrientValuePer100Grams = new NutrientValue { Kcal = 100, Protein = 10, Carbs = 10, Fat = 5 },
                        AmountGrams = 150
                    }
                ]
            });
        }

        var plan = new NutritionPlan
        {
            ExternalId = planId,
            ClientId = clientUserId,
            NutritionistId = Guid.NewGuid(),
            Name = "Unmark Test Plan",
            Status = NutritionPlanStatus.Active,
            StartDate = Today,
            Version = 1,
            DateCreated = Today,
            Weeks = [new PlanWeek { WeekNumber = 1, Status = WeekStatus.Published, DatePublished = Today, Days = days }]
        };

        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        await mongo.NutritionPlans.InsertOneAsync(plan, cancellationToken: TestContext.Current.CancellationToken);
        return (planId, mealId);
    }

    private async Task<MealLog> SeedLogAsync(
        Guid clientUserId, Guid planId, Guid mealId, bool eaten, bool withPhoto, string? note = null, bool legacyNoLogDate = false)
    {
        var log = new MealLog
        {
            ClientId = clientUserId,
            PlanId = planId,
            MealId = mealId,
            LogDate = legacyNoLogDate ? default : Today,
            EatenAt = eaten ? DateTime.UtcNow : null,
            Photos = withPhoto ? [new MealPhoto { BlobUrl = PhotoUrl, UploadedAt = DateTime.UtcNow }] : [],
            Note = note
        };

        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        await mongo.MealLogs.InsertOneAsync(log, cancellationToken: TestContext.Current.CancellationToken);
        return log;
    }

    private async Task<List<MealLog>> LoadLogsAsync(Guid clientUserId, Guid mealId)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        return await mongo.MealLogs
            .Find(l => l.ClientId == clientUserId && l.MealId == mealId)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<TodayLogDto> GetTodayAsync(Actor client)
    {
        var response = await client.Http.GetAsync("/client/nutrition/log/today", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<TodayLogDto>(JsonOptions, TestContext.Current.CancellationToken);
        return body!;
    }

    [Fact]
    public async Task Unmark_RowWithPhotosAndNote_KeepsRowAsNotEaten()
    {
        var client = await TestActors.Client(factory).CreateAsync();
        var (planId, mealId) = await SeedPlanAsync(client.UserId);
        var seeded = await SeedLogAsync(client.UserId, planId, mealId, eaten: true, withPhoto: true, note: "tasty");

        var response = await client.Http.DeleteAsync($"/client/nutrition/log/meals/{mealId}/eaten", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var rows = await LoadLogsAsync(client.UserId, mealId);
        var row = rows.Should().ContainSingle().Subject;
        row.Id.Should().Be(seeded.Id, "PlanPhoto.MealLogId points at this row");
        row.EatenAt.Should().BeNull();
        row.LogDate.Should().Be(Today);
        row.Photos.Should().ContainSingle().Which.BlobUrl.Should().Be(PhotoUrl);
        row.Note.Should().Be("tasty");

        var today = await GetTodayAsync(client);
        var meal = today.MealsEaten.Should().ContainSingle().Subject;
        meal.EatenAt.Should().BeNull();
        meal.Photos.Should().ContainSingle();
        today.TotalConsumed.Kcal.Should().Be(0, "a photo-only row is not consumed");
    }

    [Fact]
    public async Task Unmark_RowWithoutPhotosOrNote_DeletesRow()
    {
        var client = await TestActors.Client(factory).CreateAsync();
        var (planId, mealId) = await SeedPlanAsync(client.UserId);
        await SeedLogAsync(client.UserId, planId, mealId, eaten: true, withPhoto: false);

        var response = await client.Http.DeleteAsync($"/client/nutrition/log/meals/{mealId}/eaten", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await LoadLogsAsync(client.UserId, mealId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Unmark_LegacyRowWithoutLogDateWithPhotos_StaysVisibleInTodayLog()
    {
        var client = await TestActors.Client(factory).CreateAsync();
        var (planId, mealId) = await SeedPlanAsync(client.UserId);
        await SeedLogAsync(client.UserId, planId, mealId, eaten: true, withPhoto: true, legacyNoLogDate: true);

        var response = await client.Http.DeleteAsync($"/client/nutrition/log/meals/{mealId}/eaten", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var row = (await LoadLogsAsync(client.UserId, mealId)).Should().ContainSingle().Subject;
        row.EatenAt.Should().BeNull();
        row.LogDate.Should().Be(Today, "the legacy row must be backfilled or it vanishes once EatenAt is unset");

        var today = await GetTodayAsync(client);
        today.MealsEaten.Should().ContainSingle().Which.Photos.Should().ContainSingle();
    }

    [Fact]
    public async Task Remark_AfterUnmarkWithPhotos_ReusesRowWithoutDuplicatingPhotos()
    {
        var client = await TestActors.Client(factory).CreateAsync();
        var (planId, mealId) = await SeedPlanAsync(client.UserId);
        await SeedLogAsync(client.UserId, planId, mealId, eaten: true, withPhoto: true);
        await client.Http.DeleteAsync($"/client/nutrition/log/meals/{mealId}/eaten", TestContext.Current.CancellationToken);

        var response = await client.Http.PostAsJsonAsync(
            $"/client/nutrition/log/meals/{mealId}/eaten",
            new { PhotoBlobUrls = new[] { PhotoUrl, "https://minio.local/bucket/photo2.jpg" } },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var row = (await LoadLogsAsync(client.UserId, mealId)).Should().ContainSingle().Subject;
        row.EatenAt.Should().NotBeNull();
        row.Photos.Select(p => p.BlobUrl).Should().BeEquivalentTo(PhotoUrl, "https://minio.local/bucket/photo2.jpg");

        var today = await GetTodayAsync(client);
        today.MealsEaten.Should().ContainSingle().Which.EatenAt.Should().NotBeNull();
        today.TotalConsumed.Kcal.Should().Be(150);
    }

    [Fact]
    public async Task Unmark_ByOtherClient_LeavesFirstClientsRowUntouched()
    {
        var clientA = await TestActors.Client(factory).CreateAsync();
        var clientB = await TestActors.Client(factory).CreateAsync();
        var (planId, mealId) = await SeedPlanAsync(clientA.UserId);
        await SeedLogAsync(clientA.UserId, planId, mealId, eaten: true, withPhoto: true);
        await SeedLogAsync(clientA.UserId, planId, Guid.NewGuid(), eaten: true, withPhoto: false);

        var response = await clientB.Http.DeleteAsync($"/client/nutrition/log/meals/{mealId}/eaten", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var row = (await LoadLogsAsync(clientA.UserId, mealId)).Should().ContainSingle().Subject;
        row.EatenAt.Should().NotBeNull();
        row.Photos.Should().ContainSingle();
    }

    [Fact]
    public async Task GetFullPlan_PhotoOnlyRow_IsNotInEatenMealIds()
    {
        var client = await TestActors.Client(factory).CreateAsync();
        var (planId, mealId) = await SeedPlanAsync(client.UserId);
        await SeedLogAsync(client.UserId, planId, mealId, eaten: false, withPhoto: true);

        var response = await client.Http.GetAsync("/client/nutrition/plan/full", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<FullPlanDto>(JsonOptions, TestContext.Current.CancellationToken);
        body!.EatenMealIds.Should().NotContain(mealId);
    }

    private record TodayLogDto(List<TodayMealDto> MealsEaten, TotalsDto TotalConsumed);

    private record TodayMealDto(Guid MealId, DateTime? EatenAt, List<PhotoDto> Photos);

    private record PhotoDto(string BlobUrl);

    private record TotalsDto(decimal Kcal);

    private record FullPlanDto(List<Guid> EatenMealIds);
}
