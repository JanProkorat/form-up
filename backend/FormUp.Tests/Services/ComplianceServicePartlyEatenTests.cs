using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Services;

/// <summary>
/// Compliance scores a partly eaten meal by its eaten share. Runs against real Mongo because the
/// bug was in the row filter: a mock would return the row whatever the filter says.
/// </summary>
[Collection(TestCollection.Shard3)]
public class ComplianceServicePartlyEatenTests(FitnessApiFactory factory)
{
    private async Task<ComplianceResult> ComplianceAsync(Guid clientUserId)
    {
        using var scope = factory.Services.CreateScope();
        var compliance = scope.ServiceProvider.GetRequiredService<IComplianceService>();
        var today = MealItemPlanSeeder.Today;

        return await compliance.CalculateComplianceAsync(clientUserId, today, today, TestContext.Current.CancellationToken);
    }

    private async Task<int> StreakAsync(Guid clientUserId)
    {
        using var scope = factory.Services.CreateScope();
        var compliance = scope.ServiceProvider.GetRequiredService<IComplianceService>();

        return await compliance.CalculateStreakAsync(
            clientUserId, DateOnly.FromDateTime(MealItemPlanSeeder.Today), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Compliance_PhotoOnlyRow_ScoresZero()
    {
        var clientId = Guid.NewGuid();
        var seeded = await MealItemPlanSeeder.SeedAsync(factory, clientId);
        await MealItemPlanSeeder.InsertLogAsync(factory, clientId, seeded, eatenAt: null);

        var result = await ComplianceAsync(clientId);

        result.MealsPlanned.Should().Be(1);
        result.MealsLogged.Should().Be(0);
        result.MealsPartlyEaten.Should().Be(0);
        result.MealsEatenShare.Should().Be(0m);
        result.NutritionCompliancePercent.Should().Be(0m);
    }

    [Fact]
    public async Task Compliance_PartlyEatenRowWithoutEatenAt_ScoresTheEatenShare()
    {
        var clientId = Guid.NewGuid();
        var seeded = await MealItemPlanSeeder.SeedAsync(factory, clientId);
        await MealItemPlanSeeder.InsertLogAsync(factory, clientId, seeded, eatenAt: null, seeded.FoodAId, seeded.FoodBId);

        var result = await ComplianceAsync(clientId);

        result.MealsLogged.Should().Be(0);
        result.MealsPartlyEaten.Should().Be(1);
        result.MealsEatenShare.Should().BeApproximately(2m / 3m, 0.0001m);
        result.NutritionCompliancePercent.Should().Be(66.7m);
    }

    [Fact]
    public async Task Compliance_FullyEatenRow_ScoresAWholeMeal()
    {
        var clientId = Guid.NewGuid();
        var seeded = await MealItemPlanSeeder.SeedAsync(factory, clientId);
        await MealItemPlanSeeder.InsertLogAsync(
            factory, clientId, seeded, DateTime.UtcNow, seeded.FoodAId, seeded.FoodBId, seeded.RecipeId);

        var result = await ComplianceAsync(clientId);

        result.MealsLogged.Should().Be(1);
        result.MealsPartlyEaten.Should().Be(0);
        result.MealsEatenShare.Should().Be(1m);
        result.NutritionCompliancePercent.Should().Be(100m);
    }

    [Fact]
    public async Task Compliance_LegacyEatenRowWithoutItemIds_ScoresAWholeMeal()
    {
        var clientId = Guid.NewGuid();
        var seeded = await MealItemPlanSeeder.SeedAsync(factory, clientId);
        await MealItemPlanSeeder.InsertLogAsync(factory, clientId, seeded, DateTime.UtcNow);

        var result = await ComplianceAsync(clientId);

        result.MealsLogged.Should().Be(1);
        result.MealsEatenShare.Should().Be(1m);
        result.NutritionCompliancePercent.Should().Be(100m);
    }

    [Fact]
    public async Task Compliance_CoachAddsItemAfterFullEat_ScoresTheShareNotAWholeMeal()
    {
        var clientId = Guid.NewGuid();
        var seeded = await MealItemPlanSeeder.SeedAsync(factory, clientId);
        await MealItemPlanSeeder.InsertLogAsync(
            factory, clientId, seeded, DateTime.UtcNow, seeded.FoodAId, seeded.FoodBId, seeded.RecipeId);
        await MealItemPlanSeeder.AddFoodAsync(factory, seeded);

        var result = await ComplianceAsync(clientId);

        result.MealsLogged.Should().Be(0);
        result.MealsPartlyEaten.Should().Be(1);
        result.MealsEatenShare.Should().Be(0.75m);
        result.NutritionCompliancePercent.Should().Be(75m);
    }

    [Fact]
    public async Task Compliance_CoachRemovesTheOnlyUneatenItem_CountsAsEatenWithoutEatenAt()
    {
        var clientId = Guid.NewGuid();
        var seeded = await MealItemPlanSeeder.SeedAsync(factory, clientId);
        await MealItemPlanSeeder.InsertLogAsync(factory, clientId, seeded, eatenAt: null, seeded.FoodAId, seeded.FoodBId);
        await MealItemPlanSeeder.RemoveItemAsync(factory, seeded, seeded.RecipeId);

        var result = await ComplianceAsync(clientId);

        result.MealsLogged.Should().Be(1);
        result.MealsPartlyEaten.Should().Be(0);
        result.NutritionCompliancePercent.Should().Be(100m);
    }

    [Fact]
    public async Task Compliance_StoredIdOfRemovedItem_IsIgnored()
    {
        var clientId = Guid.NewGuid();
        var seeded = await MealItemPlanSeeder.SeedAsync(factory, clientId);
        await MealItemPlanSeeder.InsertLogAsync(factory, clientId, seeded, eatenAt: null, seeded.FoodAId, Guid.NewGuid());

        var result = await ComplianceAsync(clientId);

        result.MealsEatenShare.Should().BeApproximately(1m / 3m, 0.0001m);
        result.MealsPartlyEaten.Should().Be(1);
    }

    private async Task InsertSnapshotLogAsync(Guid clientUserId, SeededItemMeal seeded, params Guid[] snapshotItemIds)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        await mongo.MealLogs.InsertOneAsync(
            new MealLog
            {
                ClientId = clientUserId,
                PlanId = seeded.PlanId,
                MealId = seeded.MealId,
                MealKind = MealKind.Lunch,
                LogDate = MealItemPlanSeeder.Today,
                EatenAt = DateTime.UtcNow,
                EatenItemIds = snapshotItemIds.ToList(),
                EatenSnapshot = snapshotItemIds.Select(itemId => new EatenMealItem { ItemId = itemId, Name = "Seen", Kcal = 100m }).ToList(),
            },
            cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Compliance_MealEatenThenRemovedFromThePlan_CountsAsPlannedAndEatenNotOverOneHundredPercent()
    {
        var clientId = Guid.NewGuid();
        var seeded = await MealItemPlanSeeder.SeedAsync(factory, clientId);
        await InsertSnapshotLogAsync(clientId, seeded, seeded.FoodAId, seeded.FoodBId, seeded.RecipeId);
        await MealItemPlanSeeder.RemoveMealAsync(factory, seeded, planVersion: 2);

        var result = await ComplianceAsync(clientId);

        result.MealsPlanned.Should().Be(1, "the removed meal was eaten, so it joins the planned count");
        result.MealsLogged.Should().Be(1);
        result.MealsEatenShare.Should().Be(1m);
        result.NutritionCompliancePercent.Should().Be(100m);
    }

    [Fact]
    public async Task Compliance_MealEatenOfflineThenChangedByTheCoach_ScoresAsEatenByTheClientVersion()
    {
        var clientId = Guid.NewGuid();
        var seeded = await MealItemPlanSeeder.SeedAsync(factory, clientId);
        var droppedByCoach = Guid.NewGuid();
        // The client ate the foods, the recipe and an item the coach has since dropped; the coach then
        // removed food B and the recipe from the live meal.
        await InsertSnapshotLogAsync(clientId, seeded, seeded.FoodAId, seeded.FoodBId, seeded.RecipeId, droppedByCoach);
        await MealItemPlanSeeder.RemoveItemAsync(factory, seeded, seeded.FoodBId);
        await MealItemPlanSeeder.RemoveItemAsync(factory, seeded, seeded.RecipeId);

        var result = await ComplianceAsync(clientId);

        result.MealsPlanned.Should().Be(1);
        result.MealsLogged.Should().Be(1, "every item the client saw is eaten, whatever the live meal holds");
        result.MealsPartlyEaten.Should().Be(0);
        result.MealsEatenShare.Should().Be(1m);
        result.NutritionCompliancePercent.Should().Be(100m);
    }

    [Fact]
    public async Task Streak_PartlyEatenMealToday_CountsTheDayAsLogged()
    {
        var clientId = Guid.NewGuid();
        var seeded = await MealItemPlanSeeder.SeedAsync(factory, clientId);
        await MealItemPlanSeeder.InsertLogAsync(factory, clientId, seeded, eatenAt: null, seeded.FoodAId);

        (await StreakAsync(clientId)).Should().Be(1);
    }

    [Fact]
    public async Task Streak_PhotoOnlyRowToday_DoesNotCountTheDay()
    {
        var clientId = Guid.NewGuid();
        var seeded = await MealItemPlanSeeder.SeedAsync(factory, clientId);
        await MealItemPlanSeeder.InsertLogAsync(factory, clientId, seeded, eatenAt: null);

        (await StreakAsync(clientId)).Should().Be(0);
    }
}
