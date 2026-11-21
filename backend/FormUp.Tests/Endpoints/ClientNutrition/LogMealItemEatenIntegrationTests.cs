using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using FormUp.Application.Domain.Enums;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;

namespace FormUp.Tests.Endpoints.ClientNutrition;

/// <summary>
/// Testcontainers integration tests for ticking a single meal item: partly eaten rows, the all-items
/// cascade, the not-found shapes, offline replay, and the totals of a partly eaten meal.
/// </summary>
[Collection(TestCollection.Shard2)]
public class LogMealItemEatenIntegrationTests(FitnessApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string ItemRoute(Guid mealId, Guid itemId) =>
        $"/client/nutrition/log/meals/{mealId}/items/{itemId}/eaten";

    private static async Task<ItemStateDto> ReadStateAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<ItemStateDto>(JsonOptions, TestContext.Current.CancellationToken))!;
    }

    [Fact]
    public async Task Tick_OneOfThreeItems_IsPartlyEatenWithoutEatenAt()
    {
        var client = await TestActors.Client(factory).CreateAsync();
        var seeded = await MealItemPlanSeeder.SeedAsync(factory, client.UserId);

        var state = await ReadStateAsync(await client.Http.PostAsJsonAsync(
            ItemRoute(seeded.MealId, seeded.FoodAId), new { }, TestContext.Current.CancellationToken));

        state.EatenState.Should().Be(MealEatenState.PartlyEaten);
        state.EatenItemIds.Should().BeEquivalentTo([seeded.FoodAId]);
        state.EatenAt.Should().BeNull();
        var row = (await MealItemPlanSeeder.LoadLogsAsync(factory, client.UserId, seeded.MealId)).Should().ContainSingle().Subject;
        row.EatenAt.Should().BeNull();
        row.EatenItemIds.Should().BeEquivalentTo([seeded.FoodAId]);
    }

    [Fact]
    public async Task Tick_EveryItem_SetsEatenAtAndCountsTheWholeMeal()
    {
        var client = await TestActors.Client(factory).CreateAsync();
        var seeded = await MealItemPlanSeeder.SeedAsync(factory, client.UserId);
        await client.Http.PostAsJsonAsync(ItemRoute(seeded.MealId, seeded.FoodAId), new { }, TestContext.Current.CancellationToken);
        await client.Http.PostAsJsonAsync(ItemRoute(seeded.MealId, seeded.FoodBId), new { }, TestContext.Current.CancellationToken);

        var state = await ReadStateAsync(await client.Http.PostAsJsonAsync(
            ItemRoute(seeded.MealId, seeded.RecipeId), new { }, TestContext.Current.CancellationToken));

        state.EatenState.Should().Be(MealEatenState.Eaten);
        state.EatenAt.Should().NotBeNull();
        var row = (await MealItemPlanSeeder.LoadLogsAsync(factory, client.UserId, seeded.MealId)).Should().ContainSingle().Subject;
        row.EatenAt.Should().NotBeNull();
        row.EatenItemIds.Should().BeEquivalentTo([seeded.FoodAId, seeded.FoodBId, seeded.RecipeId]);
    }

    [Fact]
    public async Task Tick_PartlyEatenMeal_TodayLogCountsOnlyTheEatenItemsIncludingRecipe()
    {
        var client = await TestActors.Client(factory).CreateAsync();
        var seeded = await MealItemPlanSeeder.SeedAsync(factory, client.UserId);
        await client.Http.PostAsJsonAsync(ItemRoute(seeded.MealId, seeded.FoodBId), new { }, TestContext.Current.CancellationToken);
        await client.Http.PostAsJsonAsync(ItemRoute(seeded.MealId, seeded.RecipeId), new { }, TestContext.Current.CancellationToken);

        var response = await client.Http.GetAsync("/client/nutrition/log/today", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var today = (await response.Content.ReadFromJsonAsync<TodayDto>(JsonOptions, TestContext.Current.CancellationToken))!;
        var meal = today.MealsEaten.Should().ContainSingle().Subject;
        meal.EatenState.Should().Be(MealEatenState.PartlyEaten);
        meal.EatenAt.Should().BeNull();
        meal.EatenItemIds.Should().BeEquivalentTo([seeded.FoodBId, seeded.RecipeId]);
        meal.Totals.Kcal.Should().Be(500, "200 kcal of food B plus the 300 kcal recipe; food A is not eaten");
        today.TotalConsumed.Kcal.Should().Be(500);
    }

    [Fact]
    public async Task LogWholeMeal_MarksEveryItemEaten()
    {
        var client = await TestActors.Client(factory).CreateAsync();
        var seeded = await MealItemPlanSeeder.SeedAsync(factory, client.UserId);
        await client.Http.PostAsJsonAsync(ItemRoute(seeded.MealId, seeded.FoodAId), new { }, TestContext.Current.CancellationToken);

        var response = await client.Http.PostAsJsonAsync(
            $"/client/nutrition/log/meals/{seeded.MealId}/eaten", new { }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var row = (await MealItemPlanSeeder.LoadLogsAsync(factory, client.UserId, seeded.MealId)).Should().ContainSingle().Subject;
        row.EatenAt.Should().NotBeNull();
        row.EatenItemIds.Should().BeEquivalentTo([seeded.FoodAId, seeded.FoodBId, seeded.RecipeId]);
    }

    [Fact]
    public async Task Tick_UnknownItem_Returns404WithMealItemNotFoundCode()
    {
        var client = await TestActors.Client(factory).CreateAsync();
        var seeded = await MealItemPlanSeeder.SeedAsync(factory, client.UserId);

        var response = await client.Http.PostAsJsonAsync(
            ItemRoute(seeded.MealId, Guid.NewGuid()), new { }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        body.RootElement.GetProperty("errorCode").GetString().Should().Be("MEAL_ITEM_NOT_FOUND");
        (await MealItemPlanSeeder.LoadLogsAsync(factory, client.UserId, seeded.MealId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Tick_MealNotInTheClientsPlan_Returns404()
    {
        var client = await TestActors.Client(factory).CreateAsync();
        var seeded = await MealItemPlanSeeder.SeedAsync(factory, client.UserId);

        var response = await client.Http.PostAsJsonAsync(
            ItemRoute(Guid.NewGuid(), seeded.FoodAId), new { }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Tick_ItemOfAnotherClientsPlan_Returns404AndWritesNothing()
    {
        var owner = await TestActors.Client(factory).CreateAsync();
        var other = await TestActors.Client(factory).CreateAsync();
        var seeded = await MealItemPlanSeeder.SeedAsync(factory, owner.UserId);

        var response = await other.Http.PostAsJsonAsync(
            ItemRoute(seeded.MealId, seeded.FoodAId), new { }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await MealItemPlanSeeder.LoadLogsAsync(factory, other.UserId, seeded.MealId)).Should().BeEmpty();
        (await MealItemPlanSeeder.LoadLogsAsync(factory, owner.UserId, seeded.MealId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Tick_ReplayedClientRequestId_AppliesOnceAndReturnsTheSameState()
    {
        var client = await TestActors.Client(factory).CreateAsync();
        var seeded = await MealItemPlanSeeder.SeedAsync(factory, client.UserId);
        var requestId = Guid.NewGuid();
        var body = new { ClientRequestId = requestId, OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-1) };

        var first = await ReadStateAsync(await client.Http.PostAsJsonAsync(
            ItemRoute(seeded.MealId, seeded.FoodAId), body, TestContext.Current.CancellationToken));
        await client.Http.DeleteAsync(
            $"/client/nutrition/log/meals/{seeded.MealId}/items/{seeded.FoodAId}/eaten", TestContext.Current.CancellationToken);
        var replay = await ReadStateAsync(await client.Http.PostAsJsonAsync(
            ItemRoute(seeded.MealId, seeded.FoodAId), body, TestContext.Current.CancellationToken));

        first.EatenItemIds.Should().BeEquivalentTo([seeded.FoodAId]);
        replay.EatenItemIds.Should().BeEmpty("the replay must not re-apply the tick after the later untick");
        (await MealItemPlanSeeder.LoadLogsAsync(factory, client.UserId, seeded.MealId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Tick_OccurredAtTooOld_Returns400()
    {
        var client = await TestActors.Client(factory).CreateAsync();
        var seeded = await MealItemPlanSeeder.SeedAsync(factory, client.UserId);

        var response = await client.Http.PostAsJsonAsync(
            ItemRoute(seeded.MealId, seeded.FoodAId),
            new { OccurredAt = DateTimeOffset.UtcNow.AddDays(-8) },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private record ItemStateDto(Guid MealId, Guid ItemId, MealEatenState EatenState, List<Guid> EatenItemIds, DateTime? EatenAt);

    private record TodayDto(List<TodayMealDto> MealsEaten, TotalsDto TotalConsumed);

    private record TodayMealDto(
        Guid MealId, DateTime? EatenAt, MealEatenState EatenState, List<Guid> EatenItemIds, TotalsDto Totals);

    private record TotalsDto(decimal Kcal);
}
