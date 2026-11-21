using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace FormUp.Tests.Endpoints.NutritionPlanTemplates;

/// <summary>
/// Item ids of a template meal survive a full-state update, with and without the id being sent.
/// </summary>
[Collection(TestCollection.Shard2)]
public class UpdateTemplateItemIdTests(FitnessApiFactory factory)
{
    private record Seeded(NutritionPlanTemplate Template, Guid MealId, Guid FoodItemId, Guid RecipeItemId, Guid FoodRef, Guid RecipeRef);

    private async Task<(Actor Owner, Seeded Seeded)> SeedAsync()
    {
        var owner = await TestActors.Nutritionist(factory).CreateAsync();
        var seeded = new Seeded(
            new NutritionPlanTemplate
            {
                ExternalId = Guid.NewGuid(),
                OwnerId = owner.UserId,
                Name = "Item Id Template",
                Visibility = LibraryVisibility.Private,
                Version = 1,
                DateCreated = DateTime.UtcNow,
                WeekCount = 1,
                Weeks = [],
            },
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        seeded.Template.Weeks =
        [
            new TemplateWeek
            {
                WeekNumber = 1,
                Days =
                [
                    new PlanDay
                    {
                        DayOfWeek = 1,
                        Meals =
                        [
                            new PlanMeal
                            {
                                MealId = seeded.MealId,
                                Kind = MealKind.Lunch,
                                Order = 1,
                                Foods = [new MealFood { ItemId = seeded.FoodItemId, FoodExternalId = seeded.FoodRef, FoodName = "Rice", AmountGrams = 100 }],
                                Recipes = [new MealRecipe { ItemId = seeded.RecipeItemId, RecipeId = seeded.RecipeRef, RecipeName = "Bowl", Servings = 1 }],
                            },
                        ],
                    },
                ],
            },
        ];

        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        await mongo.NutritionPlanTemplates.InsertOneAsync(seeded.Template, cancellationToken: TestContext.Current.CancellationToken);

        return (owner, seeded);
    }

    private async Task<PlanMeal> PutAsync(Actor owner, Seeded seeded, Guid? mealId, Guid? foodItemId, Guid? recipeItemId)
    {
        var response = await owner.Http.PutAsJsonAsync(
            $"/nutrition/plan-templates/{seeded.Template.ExternalId}",
            new
            {
                Name = "Item Id Template",
                Version = 1,
                Supplements = Array.Empty<object>(),
                Weeks = new[]
                {
                    new
                    {
                        WeekNumber = 1,
                        Days = new[]
                        {
                            new
                            {
                                DayOfWeek = 1,
                                Meals = new[]
                                {
                                    new
                                    {
                                        MealId = mealId,
                                        Kind = "Lunch",
                                        Order = 1,
                                        Foods = new[] { new { ItemId = foodItemId, FoodExternalId = seeded.FoodRef, FoodName = "Rice", AmountGrams = 150 } },
                                        Recipes = new[] { new { ItemId = recipeItemId, RecipeId = seeded.RecipeRef, RecipeName = "Bowl", Servings = 2 } },
                                    },
                                },
                            },
                        },
                    },
                },
            },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        var stored = await mongo.NutritionPlanTemplates
            .Find(t => t.ExternalId == seeded.Template.ExternalId)
            .FirstAsync(TestContext.Current.CancellationToken);

        return stored.Weeks[0].Days[0].Meals[0];
    }

    [Fact]
    public async Task Update_SentItemIds_AreKept()
    {
        var (owner, seeded) = await SeedAsync();

        var meal = await PutAsync(owner, seeded, seeded.MealId, seeded.FoodItemId, seeded.RecipeItemId);

        meal.Foods[0].ItemId.Should().Be(seeded.FoodItemId);
        meal.Foods[0].AmountGrams.Should().Be(150);
        meal.Recipes[0].ItemId.Should().Be(seeded.RecipeItemId);
    }

    [Fact]
    public async Task Update_NoItemIdsSent_MatchesByFoodAndRecipeReference()
    {
        var (owner, seeded) = await SeedAsync();

        var meal = await PutAsync(owner, seeded, seeded.MealId, null, null);

        meal.Foods[0].ItemId.Should().Be(seeded.FoodItemId);
        meal.Recipes[0].ItemId.Should().Be(seeded.RecipeItemId);
    }

    [Fact]
    public async Task Update_MealWithoutMealId_GetsNewItemIds()
    {
        var (owner, seeded) = await SeedAsync();

        var meal = await PutAsync(owner, seeded, null, seeded.FoodItemId, seeded.RecipeItemId);

        meal.MealId.Should().NotBe(seeded.MealId);
        meal.Foods[0].ItemId.Should().NotBe(Guid.Empty).And.NotBe(seeded.FoodItemId);
        meal.Recipes[0].ItemId.Should().NotBe(Guid.Empty).And.NotBe(seeded.RecipeItemId);
    }
}
