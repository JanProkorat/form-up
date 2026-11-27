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
/// The week meal log reports the per-item eaten state of a partly eaten and a fully eaten meal.
/// </summary>
[Collection(TestCollection.Shard4)]
public class GetWeekMealLogItemStateTests(FitnessApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private async Task<MealDto> GetTodaysMealAsync(Actor client)
    {
        var date = MealItemPlanSeeder.Today.ToString("yyyy-MM-dd");
        var response = await client.Http.GetAsync($"/client/nutrition/log/week?date={date}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var week = (await response.Content.ReadFromJsonAsync<WeekDto>(JsonOptions, TestContext.Current.CancellationToken))!;

        return week.Days.Single(day => day.Date == date).Meals.Single();
    }

    [Fact]
    public async Task GetWeek_PartlyEatenMeal_IsNotEatenButListsTheTickedItems()
    {
        var client = await TestActors.Client(factory).CreateAsync();
        var seeded = await MealItemPlanSeeder.SeedAsync(factory, client.UserId);
        await client.Http.PostAsJsonAsync(
            $"/client/nutrition/log/meals/{seeded.MealId}/items/{seeded.RecipeId}/eaten", new { }, TestContext.Current.CancellationToken);

        var meal = await GetTodaysMealAsync(client);

        meal.Eaten.Should().BeFalse();
        meal.EatenState.Should().Be(MealEatenState.PartlyEaten);
        meal.EatenItemIds.Should().BeEquivalentTo([seeded.RecipeId]);
        meal.EatenAt.Should().BeNull();
    }

    [Fact]
    public async Task GetWeek_FullyEatenMeal_IsEatenWithEveryItemId()
    {
        var client = await TestActors.Client(factory).CreateAsync();
        var seeded = await MealItemPlanSeeder.SeedAsync(factory, client.UserId);
        await client.Http.PostAsJsonAsync(
            $"/client/nutrition/log/meals/{seeded.MealId}/eaten", new { }, TestContext.Current.CancellationToken);

        var meal = await GetTodaysMealAsync(client);

        meal.Eaten.Should().BeTrue();
        meal.EatenState.Should().Be(MealEatenState.Eaten);
        meal.EatenItemIds.Should().BeEquivalentTo([seeded.FoodAId, seeded.FoodBId, seeded.RecipeId]);
        meal.EatenAt.Should().NotBeNull();
    }

    private record WeekDto(List<DayDto> Days);

    private record DayDto(string Date, List<MealDto> Meals);

    private record MealDto(bool Eaten, MealEatenState EatenState, List<Guid> EatenItemIds, DateTime? EatenAt);
}
