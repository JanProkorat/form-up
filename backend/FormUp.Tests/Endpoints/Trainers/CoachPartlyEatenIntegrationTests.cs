using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FormUp.Application.Domain.Enums;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;

namespace FormUp.Tests.Endpoints.Trainers;

/// <summary>
/// Partly eaten meals in the coach timeline, dashboard and progress, and in the client's own plan list
/// and full plan. Real Mongo: the row filters used to select by <c>EatenAt</c>, which a partly eaten row lacks.
/// </summary>
[Collection(TestCollection.Shard3)]
public class CoachPartlyEatenIntegrationTests(FitnessApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string TodayText => MealItemPlanSeeder.Today.ToString("yyyy-MM-dd");

    private async Task<(Actor Coach, Actor Client, SeededItemMeal Seeded)> SeedAsync()
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync();
        var client = await TestActors.Client(factory).CreateAsync();
        await TestActors.Link(factory, coach, client).AsRole(UserRole.Nutritionist).CreateAsync();
        var seeded = await MealItemPlanSeeder.SeedAsync(factory, client.UserId);
        await MealItemPlanSeeder.SetNutritionistAsync(factory, seeded.PlanId, coach.UserId);

        return (coach, client, seeded);
    }

    private async Task<T> GetAsync<T>(HttpClient http, string url)
    {
        var response = await http.GetAsync(url, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return (await response.Content.ReadFromJsonAsync<T>(JsonOptions, TestContext.Current.CancellationToken))!;
    }

    private async Task<MealDayDto> TimelineDayAsync(Actor coach, Actor client)
    {
        var timeline = await GetAsync<TimelineDto>(coach.Http, $"/trainer/clients/{client.PublicId}/timeline");

        return timeline.Items.Single(item => item.Type == "meal_day").MealDay!;
    }

    [Fact]
    public async Task Timeline_PartlyEatenMeal_CountsAsPartlyEatenNotLogged()
    {
        var (coach, client, seeded) = await SeedAsync();
        await MealItemPlanSeeder.InsertLogAsync(factory, client.UserId, seeded, eatenAt: null, seeded.FoodAId);

        var day = await TimelineDayAsync(coach, client);

        day.MealsLogged.Should().Be(0);
        day.MealsPartlyEaten.Should().Be(1);
        day.MealsPlanned.Should().Be(1);
    }

    [Fact]
    public async Task Timeline_FullyEatenMeal_CountsAsLoggedNotPartlyEaten()
    {
        var (coach, client, seeded) = await SeedAsync();
        await MealItemPlanSeeder.InsertLogAsync(
            factory, client.UserId, seeded, DateTime.UtcNow, seeded.FoodAId, seeded.FoodBId, seeded.RecipeId);

        var day = await TimelineDayAsync(coach, client);

        day.MealsLogged.Should().Be(1);
        day.MealsPartlyEaten.Should().Be(0);
    }

    [Fact]
    public async Task Timeline_PhotoOnlyRow_HasNoMealDay()
    {
        var (coach, client, seeded) = await SeedAsync();
        await MealItemPlanSeeder.InsertLogAsync(factory, client.UserId, seeded, eatenAt: null);

        var timeline = await GetAsync<TimelineDto>(coach.Http, $"/trainer/clients/{client.PublicId}/timeline");

        timeline.Items.Should().NotContain(item => item.Type == "meal_day");
    }

    [Theory]
    [InlineData(false, 400)]
    [InlineData(true, 600)]
    public async Task Dashboard_TodayKcal_CountsOnlyTheEatenItemsOfAPartlyEatenMeal(bool fullyEaten, int expectedKcal)
    {
        var (coach, client, seeded) = await SeedAsync();

        if (fullyEaten)
        {
            await MealItemPlanSeeder.InsertLogAsync(
                factory, client.UserId, seeded, DateTime.UtcNow, seeded.FoodAId, seeded.FoodBId, seeded.RecipeId);
        }
        else
        {
            await MealItemPlanSeeder.InsertLogAsync(factory, client.UserId, seeded, eatenAt: null, seeded.FoodAId, seeded.RecipeId);
        }

        var dashboard = await GetAsync<DashboardDto>(coach.Http, "/trainer/dashboard-summary");

        dashboard.Clients.Single(item => item.PublicId == client.PublicId).TodayKcal.Should().Be(expectedKcal);
    }

    [Fact]
    public async Task Progress_PartlyEatenMeal_ReportsTheShareAndThePartlyEatenCount()
    {
        var (coach, client, seeded) = await SeedAsync();
        await MealItemPlanSeeder.InsertLogAsync(factory, client.UserId, seeded, eatenAt: null, seeded.FoodAId, seeded.FoodBId);

        var progress = await GetAsync<ProgressDto>(
            coach.Http, $"/trainer/clients/{client.PublicId}/progress?from={TodayText}&to={TodayText}");

        progress.MealsLogged.Should().Be(0);
        progress.MealsPartlyEaten.Should().Be(1);
        progress.MealsEatenShare.Should().BeApproximately(2m / 3m, 0.0001m);
    }

    [Theory]
    [InlineData(false, 67)]
    [InlineData(true, 100)]
    public async Task ClientPlans_MealsLoggedPercent_ScoresAPartlyEatenMealByItsShare(bool fullyEaten, int expectedPercent)
    {
        var (_, client, seeded) = await SeedAsync();

        if (fullyEaten)
        {
            await MealItemPlanSeeder.InsertLogAsync(
                factory, client.UserId, seeded, DateTime.UtcNow, seeded.FoodAId, seeded.FoodBId, seeded.RecipeId);
        }
        else
        {
            await MealItemPlanSeeder.InsertLogAsync(factory, client.UserId, seeded, eatenAt: null, seeded.FoodAId, seeded.FoodBId);
        }

        var plans = await GetAsync<ClientPlansDto>(client.Http, "/client/plans?status=Active");

        plans.Items.Single(item => item.PlanId == seeded.PlanId).MealsLoggedPercent.Should().Be(expectedPercent);
    }

    [Fact]
    public async Task FullPlan_PartlyEatenMeal_IsListedApartFromEatenMeals()
    {
        var (_, client, seeded) = await SeedAsync();
        await MealItemPlanSeeder.InsertLogAsync(factory, client.UserId, seeded, eatenAt: null, seeded.FoodAId);

        var plan = await GetAsync<FullPlanDto>(client.Http, "/client/nutrition/plan/full");

        plan.EatenMealIds.Should().BeEmpty();
        plan.PartlyEatenMealIds.Should().BeEquivalentTo([seeded.MealId]);
        plan.EatenItemIdsByMeal[seeded.MealId].Should().BeEquivalentTo([seeded.FoodAId]);
    }

    [Fact]
    public async Task FullPlan_CoachAddsItemAfterFullEat_MovesTheMealToPartlyEaten()
    {
        var (_, client, seeded) = await SeedAsync();
        await MealItemPlanSeeder.InsertLogAsync(
            factory, client.UserId, seeded, DateTime.UtcNow, seeded.FoodAId, seeded.FoodBId, seeded.RecipeId);
        await MealItemPlanSeeder.AddFoodAsync(factory, seeded);

        var plan = await GetAsync<FullPlanDto>(client.Http, "/client/nutrition/plan/full");

        plan.EatenMealIds.Should().BeEmpty();
        plan.PartlyEatenMealIds.Should().BeEquivalentTo([seeded.MealId]);
    }

    [Fact]
    public async Task FullPlan_FullyEatenMeal_StaysInEatenMealIds()
    {
        var (_, client, seeded) = await SeedAsync();
        await MealItemPlanSeeder.InsertLogAsync(factory, client.UserId, seeded, DateTime.UtcNow);

        var plan = await GetAsync<FullPlanDto>(client.Http, "/client/nutrition/plan/full");

        plan.EatenMealIds.Should().BeEquivalentTo([seeded.MealId]);
        plan.PartlyEatenMealIds.Should().BeEmpty();
    }

    private record TimelineDto(List<TimelineItemDto> Items);

    private record TimelineItemDto(string Type, MealDayDto? MealDay);

    private record MealDayDto(int MealsLogged, int MealsPartlyEaten, int? MealsPlanned);

    private record DashboardDto(List<DashboardClientDto> Clients);

    private record DashboardClientDto(Guid PublicId, decimal? TodayKcal);

    private record ProgressDto(int? MealsLogged, int? MealsPartlyEaten, decimal? MealsEatenShare);

    private record ClientPlansDto(List<ClientPlanDto> Items);

    private record ClientPlanDto(Guid PlanId, int? MealsLoggedPercent);

    private record FullPlanDto(
        List<Guid> EatenMealIds, List<Guid> PartlyEatenMealIds, Dictionary<Guid, List<Guid>> EatenItemIdsByMeal);
}
