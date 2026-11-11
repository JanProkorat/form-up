using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Recipes;

/// <summary>
/// Real-pipeline coverage for the recipe write contract: ingredient visibility, the new
/// servings / meal type / difficulty / dietary / steps fields, derived allergens and optimistic
/// concurrency. The Mongo mock used by the unit tests ignores filters, so none of this can be
/// proven there.
/// </summary>
[Collection(TestCollection.Name)]
public class RecipeContentIntegrationTests(FitnessApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Guid> InsertFoodAsync(
        Guid? ownerId,
        FoodVisibility visibility,
        bool isDeleted = false,
        params string[] allergens)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        var food = new Food
        {
            ExternalId = Guid.NewGuid(),
            Name = $"Recipe Test Food {Guid.NewGuid():N}",
            NutritionistId = ownerId,
            Visibility = visibility,
            IsDeleted = isDeleted,
            Allergens = [.. allergens],
            NutrientValue = new NutrientValue { Kcal = 200, Protein = 10, Carbs = 20, Fat = 5 },
            DateCreated = DateTime.UtcNow
        };
        await mongo.Foods.InsertOneAsync(food, cancellationToken: Ct);
        return food.ExternalId;
    }

    private static object ValidBody(Guid foodId, string name = "Recipe Content Test") => new
    {
        Name = name,
        Servings = 4,
        MealTypes = new[] { "Dinner", "Lunch" },
        Foods = new[] { new { FoodExternalId = foodId, AmountGrams = 150m } }
    };

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: Ct));

    [Fact]
    public async Task Create_WithAllNewFields_ReturnsThemDerivedAllergensAndPrivateDefault()
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var foodId = await InsertFoodAsync(coach.UserId, FoodVisibility.Private, false, "milk", "Gluten");

        var response = await coach.Http.PostAsJsonAsync("/recipes", new
        {
            Name = "Full Recipe",
            Servings = 4,
            Difficulty = "Medium",
            PrepTimeMinutes = 10,
            CookTimeMinutes = 25,
            MealTypes = new[] { "Dinner" },
            DietaryPreferences = new[] { "Vegetarian" },
            Steps = new[]
            {
                new { Title = (string?)"  Prep  ", Description = "  Chop  ", Minutes = (int?)null },
                new { Title = (string?)"", Description = "   ", Minutes = (int?)5 },
                new { Title = (string?)"Only title", Description = "", Minutes = (int?)null },
                new { Title = (string?)null, Description = "Cook", Minutes = (int?)20 }
            },
            Foods = new[] { new { FoodExternalId = foodId, AmountGrams = 100m } }
        }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await ReadJsonAsync(response);
        body.GetProperty("servings").GetInt32().Should().Be(4);
        body.GetProperty("difficulty").GetString().Should().Be("Medium");
        body.GetProperty("cookTimeMinutes").GetInt32().Should().Be(25);
        body.GetProperty("mealTypes").EnumerateArray().Select(e => e.GetString()).Should().Equal("Dinner");
        body.GetProperty("dietaryPreferences").EnumerateArray().Select(e => e.GetString()).Should().Equal("Vegetarian");
        AssertSteps(body, ("Prep", "Chop", null), ("Only title", "", null), (null, "Cook", 20));
        body.GetProperty("allergens").EnumerateArray().Select(e => e.GetString()).Should().BeEquivalentTo("Gluten", "Milk");
        body.GetProperty("visibility").GetString().Should().Be("Private");
        body.GetProperty("isSystem").GetBoolean().Should().BeFalse();

        var fetched = await ReadJsonAsync(await coach.Http.GetAsync($"/recipes/{body.GetProperty("recipeId").GetGuid()}", Ct));
        fetched.GetProperty("allergens").EnumerateArray().Select(e => e.GetString()).Should().BeEquivalentTo("Gluten", "Milk");
        fetched.GetProperty("servings").GetInt32().Should().Be(4);
        AssertSteps(fetched, ("Prep", "Chop", null), ("Only title", "", null), (null, "Cook", 20));
    }

    [Fact]
    public async Task Create_StepsWithTitleDescriptionAndMinutes_RoundTrips()
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var foodId = await InsertFoodAsync(coach.UserId, FoodVisibility.Private);

        var response = await coach.Http.PostAsJsonAsync("/recipes", new
        {
            Name = "Titled Steps Recipe",
            MealTypes = new[] { "Dinner" },
            Steps = new[] { new { Title = "Simmer", Description = "Simmer gently", Minutes = 15 } },
            Foods = new[] { new { FoodExternalId = foodId, AmountGrams = 100m } }
        }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await ReadJsonAsync(response);
        AssertSteps(created, ("Simmer", "Simmer gently", 15));

        var fetched = await ReadJsonAsync(await coach.Http.GetAsync($"/recipes/{created.GetProperty("recipeId").GetGuid()}", Ct));
        AssertSteps(fetched, ("Simmer", "Simmer gently", 15));
    }

    [Fact]
    public async Task Create_StepsWithoutMinutes_RoundTripsDescriptionOnly()
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var foodId = await InsertFoodAsync(coach.UserId, FoodVisibility.Private);

        var response = await coach.Http.PostAsJsonAsync("/recipes", new
        {
            Name = "No Minutes Recipe",
            MealTypes = new[] { "Dinner" },
            Steps = new[] { new { Description = "Mix" }, new { Description = "Serve" } },
            Foods = new[] { new { FoodExternalId = foodId, AmountGrams = 100m } }
        }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await ReadJsonAsync(response);
        AssertSteps(created, (null, "Mix", null), (null, "Serve", null));

        var fetched = await ReadJsonAsync(await coach.Http.GetAsync($"/recipes/{created.GetProperty("recipeId").GetGuid()}", Ct));
        AssertSteps(fetched, (null, "Mix", null), (null, "Serve", null));
    }

    private static void AssertSteps(JsonElement body, params (string? Title, string Description, int? Minutes)[] expected)
    {
        var actual = body.GetProperty("steps").EnumerateArray()
            .Select(step => (
                Title: step.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String
                    ? title.GetString()
                    : null,
                Description: step.GetProperty("description").GetString(),
                Minutes: step.TryGetProperty("minutes", out var minutes) && minutes.ValueKind == JsonValueKind.Number
                    ? minutes.GetInt32()
                    : (int?)null))
            .ToList();

        actual.Should().Equal(expected.Select(step => (step.Title, (string?)step.Description, step.Minutes)));
    }

    [Fact]
    public async Task Create_InvalidBody_Returns400()
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var foodId = await InsertFoodAsync(null, FoodVisibility.Public);

        var response = await coach.Http.PostAsJsonAsync("/recipes", new
        {
            Name = "Bad",
            Servings = 0,
            MealTypes = Array.Empty<string>(),
            Foods = new[] { new { FoodExternalId = foodId, AmountGrams = 100m } }
        }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("ownPrivate")]
    [InlineData("system")]
    [InlineData("otherPublic")]
    public async Task Create_UsableIngredient_Returns201(string kind)
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var other = await TestActors.Nutritionist(factory).CreateAsync(Ct);

        var foodId = kind switch
        {
            "ownPrivate" => await InsertFoodAsync(coach.UserId, FoodVisibility.Private),
            "system" => await InsertFoodAsync(null, FoodVisibility.Public),
            _ => await InsertFoodAsync(other.UserId, FoodVisibility.Public)
        };

        var response = await coach.Http.PostAsJsonAsync("/recipes", ValidBody(foodId), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Theory]
    [InlineData("otherPrivate")]
    [InlineData("otherDeleted")]
    [InlineData("ownDeleted")]
    [InlineData("missing")]
    public async Task Create_UnusableIngredient_Returns400WithCode_AndPersistsNothing(string kind)
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var other = await TestActors.Nutritionist(factory).CreateAsync(Ct);

        var foodId = kind switch
        {
            "otherPrivate" => await InsertFoodAsync(other.UserId, FoodVisibility.Private),
            "otherDeleted" => await InsertFoodAsync(other.UserId, FoodVisibility.Public, true),
            "ownDeleted" => await InsertFoodAsync(coach.UserId, FoodVisibility.Public, true),
            _ => Guid.NewGuid()
        };
        var name = $"Unusable {Guid.NewGuid():N}";

        var response = await coach.Http.PostAsJsonAsync("/recipes", ValidBody(foodId, name), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("RECIPE_FOOD_NOT_AVAILABLE");

        var search = await ReadJsonAsync(await coach.Http.GetAsync($"/recipes?search={name}", Ct));
        search.GetProperty("totalCount").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Update_OtherCoachesPrivateIngredient_Returns400_AndRecipeUnchanged()
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var other = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var ownFood = await InsertFoodAsync(coach.UserId, FoodVisibility.Private);
        var foreignPrivate = await InsertFoodAsync(other.UserId, FoodVisibility.Private);

        var created = await ReadJsonAsync(await coach.Http.PostAsJsonAsync("/recipes", ValidBody(ownFood), Ct));
        var recipeId = created.GetProperty("recipeId").GetGuid();

        var response = await coach.Http.PutAsJsonAsync($"/recipes/{recipeId}", new
        {
            Version = 1,
            Name = "Renamed",
            Servings = 2,
            MealTypes = new[] { "Snack" },
            Foods = new[] { new { FoodExternalId = foreignPrivate, AmountGrams = 50m } }
        }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("RECIPE_FOOD_NOT_AVAILABLE");

        var fetched = await ReadJsonAsync(await coach.Http.GetAsync($"/recipes/{recipeId}", Ct));
        fetched.GetProperty("name").GetString().Should().Be("Recipe Content Test");
        fetched.GetProperty("version").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Update_ReplacesFields_BumpsVersion_AndClearsDifficulty()
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var foodId = await InsertFoodAsync(coach.UserId, FoodVisibility.Private);

        var created = await ReadJsonAsync(await coach.Http.PostAsJsonAsync("/recipes", new
        {
            Name = "Before",
            Servings = 2,
            Difficulty = "Hard",
            MealTypes = new[] { "Lunch" },
            Foods = new[] { new { FoodExternalId = foodId, AmountGrams = 100m } }
        }, Ct));
        var recipeId = created.GetProperty("recipeId").GetGuid();

        var response = await coach.Http.PutAsJsonAsync($"/recipes/{recipeId}", new
        {
            Version = 1,
            Name = "After",
            Servings = 6,
            MealTypes = new[] { "Dessert" },
            DietaryPreferences = new[] { "Vegan", "GlutenFree" },
            Foods = new[] { new { FoodExternalId = foodId, AmountGrams = 200m } }
        }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadJsonAsync(response);
        body.GetProperty("version").GetInt32().Should().Be(2);
        body.GetProperty("servings").GetInt32().Should().Be(6);
        body.GetProperty("difficulty").ValueKind.Should().Be(JsonValueKind.Null);
        body.GetProperty("mealTypes").EnumerateArray().Select(e => e.GetString()).Should().Equal("Dessert");
        body.GetProperty("dietaryPreferences").EnumerateArray().Select(e => e.GetString()).Should().Equal("Vegan", "GlutenFree");
    }

    [Fact]
    public async Task Update_StaleVersion_Returns409VersionConflict_AndRecipeUnchanged()
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var foodId = await InsertFoodAsync(coach.UserId, FoodVisibility.Private);

        var created = await ReadJsonAsync(await coach.Http.PostAsJsonAsync("/recipes", ValidBody(foodId), Ct));
        var recipeId = created.GetProperty("recipeId").GetGuid();

        var firstUpdate = await coach.Http.PutAsJsonAsync($"/recipes/{recipeId}", new
        {
            Version = 1,
            Name = "Winner",
            Servings = 4,
            MealTypes = new[] { "Lunch" },
            Foods = new[] { new { FoodExternalId = foodId, AmountGrams = 100m } }
        }, Ct);
        firstUpdate.StatusCode.Should().Be(HttpStatusCode.OK);

        var staleUpdate = await coach.Http.PutAsJsonAsync($"/recipes/{recipeId}", new
        {
            Version = 1,
            Name = "Loser",
            Servings = 4,
            MealTypes = new[] { "Lunch" },
            Foods = new[] { new { FoodExternalId = foodId, AmountGrams = 100m } }
        }, Ct);

        staleUpdate.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await staleUpdate.Content.ReadAsStringAsync(Ct)).Should().Contain("RECIPE_VERSION_CONFLICT");

        var fetched = await ReadJsonAsync(await coach.Http.GetAsync($"/recipes/{recipeId}", Ct));
        fetched.GetProperty("name").GetString().Should().Be("Winner");
    }
}
