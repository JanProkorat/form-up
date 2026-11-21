using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.ClientNutrition.LogMealEaten;
using FormUp.Application.Features.NutritionPlans.GetPlan;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace FormUp.Tests.Endpoints.NutritionPlans;

/// <summary>
/// Integration tests for what the client ate offline against a plan the coach changed: the log keeps
/// the client's version, and <c>GET /nutrition/plans/{PlanId}</c> shows it to the coach as a locked meal
/// with a conflict marker, also when the meal has since left the plan.
/// </summary>
[Collection(TestCollection.Shard2)]
public class GetPlanMealLockConflictTests(FitnessApiFactory factory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(Actor Client, Actor Coach, SeededItemMeal Seeded)> SeedAsync()
    {
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).AsRole(UserRole.Nutritionist).CreateAsync(Ct);
        var seeded = await MealItemPlanSeeder.SeedAsync(factory, client.UserId);
        await MealItemPlanSeeder.SetNutritionistAsync(factory, seeded.PlanId, coach.UserId);

        return (client, coach, seeded);
    }

    private static Task<HttpResponseMessage> LogOfflineAsync(
        Actor client, SeededItemMeal seeded, int basedOnPlanVersion, List<SeenMealItemRequest>? seen) =>
        client.Http.PostAsJsonAsync(
            $"/client/nutrition/log/meals/{seeded.MealId}/eaten",
            new LogMealEatenRequest { MealId = seeded.MealId, BasedOnPlanVersion = basedOnPlanVersion, SeenMealItems = seen },
            Ct);

    private static async Task<GetPlanResponse> GetPlanAsync(Actor coach, Guid planId)
    {
        var response = await coach.Http.GetAsync($"/nutrition/plans/{planId}", Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

        return (await response.Content.ReadFromJsonAsync<GetPlanResponse>(Json, Ct))!;
    }

    private async Task<MealLog> LoadLogAsync(Actor client, SeededItemMeal seeded) =>
        (await MealItemPlanSeeder.LoadLogsAsync(factory, client.UserId, seeded.MealId)).Should().ContainSingle().Subject;

    private static SeenMealItemRequest Seen(
        Guid itemId, Guid refId, SeenMealItemKind kind, decimal amount, decimal kcal) => new()
    {
        ItemId = itemId,
        RefId = refId,
        Kind = kind,
        Name = "Seen",
        Amount = amount,
        Kcal = kcal,
    };

    [Fact]
    public async Task PlanChanged_LogKeepsTheClientVersion_AndTheCoachSeesAMarkerWithItemTags()
    {
        var (client, coach, seeded) = await SeedAsync();
        await MealItemPlanSeeder.StampMealChangeAsync(factory, seeded, planVersion: 6);
        var liveMeal = (await MealItemPlanSeeder.LoadPlanAsync(factory, seeded)).Weeks[0].Days[0].Meals[0];
        var foodA = liveMeal.Foods.Single(food => food.ItemId == seeded.FoodAId);
        var foodB = liveMeal.Foods.Single(food => food.ItemId == seeded.FoodBId);
        var removedItemId = Guid.NewGuid();
        // The client saw v4: food A as it is now, food B smaller, and an item the coach has since dropped.
        var seen = new List<SeenMealItemRequest>
        {
            Seen(seeded.FoodAId, foodA.FoodExternalId, SeenMealItemKind.Food, 100m, 100m),
            Seen(seeded.FoodBId, foodB.FoodExternalId, SeenMealItemKind.Food, 150m, 150m),
            Seen(removedItemId, Guid.NewGuid(), SeenMealItemKind.Food, 50m, 50m),
        };

        var response = await LogOfflineAsync(client, seeded, basedOnPlanVersion: 4, seen);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var log = await LoadLogAsync(client, seeded);
        log.EatenSnapshot!.Select(item => item.ItemId)
            .Should().BeEquivalentTo([seeded.FoodAId, seeded.FoodBId, removedItemId]);
        log.EatenSnapshot!.Single(item => item.ItemId == seeded.FoodBId).Kcal.Should().Be(150m);
        log.FoodsEaten.Should().BeEmpty("the coach's live foods are not what the client ate");
        log.MealKind.Should().Be(MealKind.Lunch);
        var storedPlan = await MealItemPlanSeeder.LoadPlanAsync(factory, seeded);
        storedPlan.Version.Should().Be(6);
        storedPlan.Weeks[0].Days[0].Meals[0].Foods.Single(food => food.ItemId == seeded.FoodBId).AmountGrams.Should().Be(200m);

        var mealLock = (await GetPlanAsync(coach, seeded.PlanId)).MealLocks.First(entry => entry.MealId == seeded.MealId);
        mealLock.LockState.Should().Be(MealLockState.Eaten);
        mealLock.IsRemovedFromPlan.Should().BeFalse();
        mealLock.EatenSnapshot!.Select(item => item.ItemId)
            .Should().BeEquivalentTo([seeded.FoodAId, seeded.FoodBId, removedItemId]);
        var conflict = mealLock.Conflict.Should().NotBeNull().And.Subject.As<MealLockConflictDto>();
        conflict.Kind.Should().Be(OfflineSyncConflictKind.PlanChanged);
        conflict.BasedOnPlanVersion.Should().Be(4);
        conflict.CurrentPlanVersion.Should().Be(6);
        conflict.Reviewed.Should().BeFalse();
        conflict.ItemTags.Select(tag => (tag.ItemId, tag.Tag)).Should().BeEquivalentTo(
        [
            (seeded.FoodBId, MealItemConflictTag.Changed),
            (removedItemId, MealItemConflictTag.Removed),
            (seeded.RecipeId, MealItemConflictTag.Added),
        ]);
    }

    [Fact]
    public async Task MealRemoved_WithSeenItems_LogsTheMeal_AndTheCoachStillSeesItAfterTheRemovalRecordIsPruned()
    {
        var (client, coach, seeded) = await SeedAsync();
        await MealItemPlanSeeder.RemoveMealAsync(factory, seeded, planVersion: 4);
        var seen = new List<SeenMealItemRequest>
        {
            Seen(seeded.FoodAId, Guid.NewGuid(), SeenMealItemKind.Food, 100m, 100m),
            Seen(seeded.RecipeId, Guid.NewGuid(), SeenMealItemKind.Recipe, 1m, 300m),
        };

        var response = await LogOfflineAsync(client, seeded, basedOnPlanVersion: 1, seen);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = (await response.Content.ReadFromJsonAsync<LogMealEatenResponse>(Json, Ct))!;
        body.MealLogged.Should().BeTrue();
        body.SyncConflictKind.Should().Be(OfflineSyncConflictKind.MealRemoved);
        var log = await LoadLogAsync(client, seeded);
        log.PlanId.Should().Be(seeded.PlanId);
        log.MealKind.Should().Be(MealKind.Lunch);
        log.LogDate.Should().Be(MealItemPlanSeeder.Today);
        log.EatenSnapshot!.Select(item => item.ItemId).Should().BeEquivalentTo([seeded.FoodAId, seeded.RecipeId]);

        await MealItemPlanSeeder.ClearRemovedMealsAsync(factory, seeded);
        var plan = await GetPlanAsync(coach, seeded.PlanId);

        plan.Weeks.SelectMany(week => week.Days).SelectMany(day => day.Meals)
            .Should().NotContain(meal => meal.MealId == seeded.MealId);
        var mealLock = plan.MealLocks.Should().ContainSingle(entry => entry.MealId == seeded.MealId).Subject;
        mealLock.IsRemovedFromPlan.Should().BeTrue();
        mealLock.IsLocked.Should().BeTrue();
        mealLock.LockState.Should().Be(MealLockState.Eaten);
        mealLock.MealKind.Should().Be(MealKind.Lunch);
        mealLock.Date.Should().Be(DateOnly.FromDateTime(MealItemPlanSeeder.Today));
        mealLock.WeekNumber.Should().Be(1);
        mealLock.DayOfWeek.Should().Be(1);
        mealLock.EatenSnapshot!.Select(item => item.ItemId).Should().BeEquivalentTo([seeded.FoodAId, seeded.RecipeId]);
        mealLock.Conflict!.Kind.Should().Be(OfflineSyncConflictKind.MealRemoved);
        mealLock.Conflict.ItemTags.Should().OnlyContain(tag => tag.Tag == MealItemConflictTag.Removed)
            .And.HaveCount(2);
    }

    [Fact]
    public async Task GetPlan_ConflictOfAnotherClientOnTheSamePlanAndMeal_IsNotShown()
    {
        var (client, coach, seeded) = await SeedAsync();
        await LogOfflineAsync(client, seeded, basedOnPlanVersion: 1, seen: null);
        var stranger = await TestActors.Client(factory).CreateAsync(Ct);
        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
            await mongo.OfflineSyncConflicts.InsertOneAsync(new OfflineSyncConflict
            {
                ExternalId = Guid.NewGuid(),
                ClientId = stranger.UserId,
                PlanId = seeded.PlanId,
                PlanAuthorId = coach.UserId,
                Operation = "LogMealEaten",
                Kind = OfflineSyncConflictKind.PlanChanged,
                MealId = seeded.MealId,
                PlanName = "Plan",
                MealLogged = true,
            }, cancellationToken: Ct);
        }

        var mealLock = (await GetPlanAsync(coach, seeded.PlanId)).MealLocks.First(entry => entry.MealId == seeded.MealId);

        mealLock.LockState.Should().Be(MealLockState.Eaten);
        mealLock.Conflict.Should().BeNull();
    }

    [Fact]
    public async Task GetPlan_CoachWithoutALiveLink_Returns404()
    {
        var (_, _, seeded) = await SeedAsync();
        var outsider = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        await MealItemPlanSeeder.SetNutritionistAsync(factory, seeded.PlanId, outsider.UserId);

        var response = await outsider.Http.GetAsync($"/nutrition/plans/{seeded.PlanId}", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
