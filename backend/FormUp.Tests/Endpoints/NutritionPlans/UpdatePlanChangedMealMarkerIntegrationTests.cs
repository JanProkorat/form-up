using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace FormUp.Tests.Endpoints.NutritionPlans;

/// <summary>
/// Integration tests for <see cref="PlanMeal.ChangedAtPlanVersion"/>: the full-state plan update
/// stamps it on new, re-composed or moved meals only and carries it over on every other meal.
/// </summary>
[Collection(TestCollection.Shard3)]
public class UpdatePlanChangedMealMarkerIntegrationTests(FitnessApiFactory factory)
{
    private const int StoredVersion = 4;
    private const int StoredMarker = 2;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly MealFood _food = PlanTestHelpers.CreateMealFood(foodName: "Rice");

    private async Task<(Actor Nutritionist, Guid PlanId, Guid MealId)> SeedAsync()
    {
        var nutritionist = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, nutritionist, client).AsRole(UserRole.Nutritionist).CreateAsync(Ct);

        var mealId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        await mongo.NutritionPlans.InsertOneAsync(new NutritionPlan
        {
            ExternalId = planId,
            ClientId = client.UserId,
            NutritionistId = nutritionist.UserId,
            Name = "Marker plan",
            Status = NutritionPlanStatus.Draft,
            Version = StoredVersion,
            DateCreated = DateTime.UtcNow.AddDays(-1),
            Weeks =
            [
                new PlanWeek
                {
                    WeekNumber = 1,
                    Status = WeekStatus.Draft,
                    Days =
                    [
                        new PlanDay
                        {
                            DayOfWeek = 1,
                            Meals =
                            [
                                new PlanMeal
                                {
                                    MealId = mealId,
                                    Kind = MealKind.Lunch,
                                    Order = 1,
                                    Foods = [_food],
                                    ChangedAtPlanVersion = StoredMarker,
                                },
                            ],
                        },
                    ],
                },
            ],
        }, cancellationToken: Ct);
        return (nutritionist, planId, mealId);
    }

    private object FoodBody(decimal? amountGrams = null) => new
    {
        FoodExternalId = _food.FoodExternalId,
        FoodName = _food.FoodName,
        AmountGrams = amountGrams ?? _food.AmountGrams,
    };

    private static object MealBody(Guid? mealId, string kind, params object[] foods) =>
        new { MealId = mealId, Kind = kind, Order = 1, Foods = foods };

    /// <summary>Sends a one-week update whose days hold the given meals (element N is day N+1).</summary>
    private static Task<HttpResponseMessage> PutAsync(Actor nutritionist, Guid planId, params object[][] mealsPerDay) =>
        nutritionist.Http.PutAsJsonAsync($"/nutrition/plans/{planId}", new
        {
            PlanId = planId,
            Name = "Marker plan",
            Version = StoredVersion,
            Weeks = new[]
            {
                new
                {
                    WeekNumber = 1,
                    Days = mealsPerDay.Select((meals, index) => new { DayOfWeek = index + 1, Meals = meals }).ToArray(),
                },
            },
        }, Ct);

    private async Task<List<(int DayOfWeek, PlanMeal Meal)>> StoredMealsAsync(Guid planId)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        var plan = await mongo.NutritionPlans.Find(p => p.ExternalId == planId).FirstAsync(Ct);
        return plan.Weeks.SelectMany(w => w.Days.SelectMany(d => d.Meals.Select(m => (d.DayOfWeek, m)))).ToList();
    }

    [Fact]
    public async Task UpdatePlan_UnchangedMeal_KeepsStoredMarker()
    {
        var (nutritionist, planId, mealId) = await SeedAsync();

        var response = await PutAsync(nutritionist, planId, [MealBody(mealId, "Lunch", FoodBody())]);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        (await StoredMealsAsync(planId)).Single().Meal.ChangedAtPlanVersion.Should().Be(StoredMarker);
    }

    [Fact]
    public async Task UpdatePlan_UnchangedMealThatNeverHadAMarker_StaysWithoutOne()
    {
        var (nutritionist, planId, mealId) = await SeedAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
            await mongo.NutritionPlans.UpdateOneAsync(
                p => p.ExternalId == planId,
                Builders<NutritionPlan>.Update.Unset("weeks.0.days.0.meals.0.changedAtPlanVersion"),
                cancellationToken: Ct);
        }

        var response = await PutAsync(nutritionist, planId, [MealBody(mealId, "Lunch", FoodBody())]);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        (await StoredMealsAsync(planId)).Single().Meal.ChangedAtPlanVersion.Should().BeNull();
    }

    [Fact]
    public async Task UpdatePlan_ChangedFoodAmount_StampsTheNewVersion()
    {
        var (nutritionist, planId, mealId) = await SeedAsync();

        var response = await PutAsync(nutritionist, planId, [MealBody(mealId, "Lunch", FoodBody(_food.AmountGrams + 50))]);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        (await StoredMealsAsync(planId)).Single().Meal.ChangedAtPlanVersion.Should().Be(StoredVersion + 1);
    }

    [Fact]
    public async Task UpdatePlan_ChangedKind_StampsTheNewVersion()
    {
        var (nutritionist, planId, mealId) = await SeedAsync();

        var response = await PutAsync(nutritionist, planId, [MealBody(mealId, "Dinner", FoodBody())]);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        (await StoredMealsAsync(planId)).Single().Meal.ChangedAtPlanVersion.Should().Be(StoredVersion + 1);
    }

    [Fact]
    public async Task UpdatePlan_MealMovedToAnotherDay_StampsTheNewVersion()
    {
        var (nutritionist, planId, mealId) = await SeedAsync();

        var response = await PutAsync(nutritionist, planId, [], [MealBody(mealId, "Lunch", FoodBody())]);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var moved = (await StoredMealsAsync(planId)).Single();
        moved.DayOfWeek.Should().Be(2);
        moved.Meal.ChangedAtPlanVersion.Should().Be(StoredVersion + 1);
    }

    [Fact]
    public async Task UpdatePlan_NewMeal_IsStampedWhileTheUntouchedOneKeepsItsMarker()
    {
        var (nutritionist, planId, mealId) = await SeedAsync();

        var response = await PutAsync(
            nutritionist, planId,
            [MealBody(mealId, "Lunch", FoodBody()), MealBody(Guid.NewGuid(), "Dinner", FoodBody())]);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var stored = await StoredMealsAsync(planId);
        stored.Single(s => s.Meal.MealId == mealId).Meal.ChangedAtPlanVersion.Should().Be(StoredMarker);
        stored.Single(s => s.Meal.MealId != mealId).Meal.ChangedAtPlanVersion.Should().Be(StoredVersion + 1);
    }
}
