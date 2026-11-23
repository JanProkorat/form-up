using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace FormUp.Tests.Endpoints.Admin;

/// <summary>
/// Real-pipeline coverage for #2088: an admin-only account manages the system recipe, food and exercise libraries,
/// is told 404 for anything a coach owns, and coaches keep their existing refusal codes on system items. Runs on the
/// real Mongo of the integration host, so the query-level scoping is evaluated for real.
/// </summary>
[Collection(TestCollection.Name)]
public class AdminLibraryIntegrationTests(FitnessApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    private static HttpRequestMessage Json(HttpMethod method, string path, object body) =>
        new(method, path) { Content = JsonContent.Create(body) };

    private async Task<Guid> InsertFoodAsync(Guid? ownerId, FoodVisibility visibility = FoodVisibility.Public)
    {
        using var scope = factory.Services.CreateScope();
        var food = new Food
        {
            ExternalId = Guid.NewGuid(),
            Name = $"Admin Library Food {Guid.NewGuid():N}",
            NutritionistId = ownerId,
            Visibility = visibility,
            NutrientValue = new NutrientValue { Kcal = 200, Protein = 10, Carbs = 20, Fat = 5 },
            DateCreated = DateTime.UtcNow,
        };
        await scope.ServiceProvider.GetRequiredService<IMongoContext>().Foods.InsertOneAsync(food, cancellationToken: Ct);

        return food.ExternalId;
    }

    private async Task<Guid> InsertRecipeAsync(Guid ownerId)
    {
        using var scope = factory.Services.CreateScope();
        var recipe = new Recipe
        {
            ExternalId = Guid.NewGuid(),
            NutritionistId = ownerId,
            Name = $"Admin Library Recipe {Guid.NewGuid():N}",
            Visibility = RecipeVisibility.Public,
            TotalNutrients = new NutrientTotals(),
            DateCreated = DateTime.UtcNow,
        };
        await scope.ServiceProvider.GetRequiredService<IMongoContext>().Recipes.InsertOneAsync(recipe, cancellationToken: Ct);

        return recipe.ExternalId;
    }

    private async Task<Guid> InsertExerciseAsync(Guid? trainerId, bool isCustom)
    {
        using var scope = factory.Services.CreateScope();
        var exercise = new Exercise
        {
            ExternalId = Guid.NewGuid(),
            Name = $"Admin Library Exercise {Guid.NewGuid():N}",
            IsCustom = isCustom,
            TrainerId = trainerId,
            Source = isCustom ? "custom" : "system",
            DateCreated = DateTime.UtcNow,
        };
        await scope.ServiceProvider.GetRequiredService<IMongoContext>().Exercises.InsertOneAsync(exercise, cancellationToken: Ct);

        return exercise.ExternalId;
    }

    private static object RecipeBody(Guid foodId) => new
    {
        Name = $"Admin Recipe {Guid.NewGuid():N}",
        MealTypes = new[] { "Dinner" },
        Visibility = "Private",
        Foods = new[] { new { FoodExternalId = foodId, AmountGrams = 100m } },
    };

    private static object FoodBody(string visibility = "Private") => new
    {
        Name = $"Admin Food {Guid.NewGuid():N}",
        Visibility = visibility,
        NutrientValue = new { Kcal = 89, Protein = 10, Carbs = 10, Fat = 1 },
        CommonServings = new[] { new { Label = "1 piece", WeightGrams = 50 } },
    };

    private static object ExerciseBody(string name) => new
    {
        Name = name,
        MuscleGroups = new[] { "Chest" },
        Equipment = "Barbell",
        Category = "Strength",
        Difficulty = "Beginner",
    };

    // ── recipes ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task AdminOnly_Recipe_CreateReadUpdateDelete_WorksOnSystemRecipe_AndForcesPublic()
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var systemFood = await InsertFoodAsync(null);

        var created = await admin.Http.PostAsJsonAsync("/recipes", RecipeBody(systemFood), Ct);

        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var body = await ReadAsync(created);
        body.GetProperty("isSystem").GetBoolean().Should().BeTrue();
        body.GetProperty("visibility").GetString().Should().Be("Public", "the requested Private is ignored for a system recipe");
        var recipeId = body.GetProperty("recipeId").GetGuid();

        (await admin.Http.GetAsync($"/recipes/{recipeId}", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var updated = await admin.Http.PutAsJsonAsync($"/recipes/{recipeId}", new
        {
            Name = "Renamed system recipe",
            Version = body.GetProperty("version").GetInt32(),
            MealTypes = new[] { "Lunch" },
            Foods = new[] { new { FoodExternalId = systemFood, AmountGrams = 120m } },
        }, Ct);
        updated.StatusCode.Should().Be(HttpStatusCode.OK, await updated.Content.ReadAsStringAsync(Ct));

        (await admin.Http.DeleteAsync($"/recipes/{recipeId}", Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task AdminOnly_Recipe_UpdateWithStaleVersion_Returns409()
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var systemFood = await InsertFoodAsync(null);
        var body = await ReadAsync(await admin.Http.PostAsJsonAsync("/recipes", RecipeBody(systemFood), Ct));

        var response = await admin.Http.PutAsJsonAsync($"/recipes/{body.GetProperty("recipeId").GetGuid()}", new
        {
            Name = "Stale",
            Version = body.GetProperty("version").GetInt32() + 5,
            MealTypes = new[] { "Lunch" },
            Foods = new[] { new { FoodExternalId = systemFood, AmountGrams = 120m } },
        }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.RecipeVersionConflict);
    }

    [Fact]
    public async Task AdminOnly_Recipe_OnCoachRecipe_ReadWriteAndImageReturn404()
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coachRecipe = await InsertRecipeAsync(Guid.NewGuid());
        var systemFood = await InsertFoodAsync(null);

        (await admin.Http.GetAsync($"/recipes/{coachRecipe}", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await admin.Http.SendAsync(Json(HttpMethod.Put, $"/recipes/{coachRecipe}", new
        {
            Name = "Hijack",
            Version = 1,
            MealTypes = new[] { "Lunch" },
            Foods = new[] { new { FoodExternalId = systemFood, AmountGrams = 100m } },
        }), Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await admin.Http.DeleteAsync($"/recipes/{coachRecipe}", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await admin.Http.DeleteAsync($"/recipes/{coachRecipe}/image", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AdminOnly_Recipe_WithCoachFood_Returns400RecipeFoodNotAvailable()
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coachFood = await InsertFoodAsync(Guid.NewGuid());

        var response = await admin.Http.PostAsJsonAsync("/recipes", RecipeBody(coachFood), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.RecipeFoodNotAvailable);
    }

    // ── foods ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AdminOnly_Food_CreateReadUpdateDelete_WorksOnSystemFood_AndForcesPublic()
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);

        var created = await admin.Http.PostAsJsonAsync("/foods", FoodBody("Private"), Ct);

        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var body = await ReadAsync(created);
        body.GetProperty("isSystem").GetBoolean().Should().BeTrue();
        body.GetProperty("visibility").GetString().Should().Be("Public");
        var foodId = body.GetProperty("foodId").GetGuid();

        (await admin.Http.GetAsync($"/foods/{foodId}", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.Http.PutAsJsonAsync($"/foods/{foodId}", FoodBody(), Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.Http.DeleteAsync($"/foods/{foodId}", Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task AdminOnly_Food_OnCoachFood_ReadWriteAndImageReturn404()
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coachFood = await InsertFoodAsync(Guid.NewGuid());

        (await admin.Http.GetAsync($"/foods/{coachFood}", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await admin.Http.PutAsJsonAsync($"/foods/{coachFood}", FoodBody(), Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await admin.Http.DeleteAsync($"/foods/{coachFood}", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await admin.Http.DeleteAsync($"/foods/{coachFood}/image", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Nutritionist_EditOrDeleteSystemFood_StillReturns400FoodNotOwned()
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var systemFood = await InsertFoodAsync(null);

        var update = await coach.Http.PutAsJsonAsync($"/foods/{systemFood}", FoodBody(), Ct);
        var delete = await coach.Http.DeleteAsync($"/foods/{systemFood}", Ct);

        update.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await update.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.FoodNotOwned);
        delete.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await delete.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.FoodNotOwned);
    }

    // ── exercises ────────────────────────────────────────────────────────────

    [Fact]
    public async Task AdminOnly_Exercise_CreateReadUpdateDelete_WorksOnSystemExercise()
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);

        var created = await admin.Http.PostAsJsonAsync("/exercises", ExerciseBody($"Admin Exercise {Guid.NewGuid():N}"), Ct);

        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var body = await ReadAsync(created);
        body.GetProperty("isCustom").GetBoolean().Should().BeFalse();
        var exerciseId = body.GetProperty("exerciseId").GetGuid();

        (await admin.Http.GetAsync($"/exercises/{exerciseId}", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var updated = await admin.Http.SendAsync(Json(HttpMethod.Put, $"/exercises/{exerciseId}", new
        {
            Name = "Renamed system exercise",
            Version = body.GetProperty("version").GetInt32(),
            MuscleGroups = new[] { "Chest" },
            Equipment = "Barbell",
            Category = "Strength",
            Difficulty = "Beginner",
        }), Ct);
        updated.StatusCode.Should().Be(HttpStatusCode.OK, await updated.Content.ReadAsStringAsync(Ct));

        var version = (await ReadAsync(updated)).GetProperty("version").GetInt32();
        (await admin.Http.DeleteAsync($"/exercises/{exerciseId}?Version={version}", Ct))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task AdminOnly_Exercise_StaleVersion_Returns409()
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var exerciseId = await InsertExerciseAsync(null, isCustom: false);

        var response = await admin.Http.DeleteAsync($"/exercises/{exerciseId}?Version=9", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.ExerciseVersionConflict);
    }

    [Fact]
    public async Task AdminOnly_Exercise_OnCoachExercise_ReadWriteAndUploadReturn404()
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coachExercise = await InsertExerciseAsync(Guid.NewGuid(), isCustom: true);

        (await admin.Http.GetAsync($"/exercises/{coachExercise}", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await admin.Http.DeleteAsync($"/exercises/{coachExercise}?Version=1", Ct)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await admin.Http.PostAsJsonAsync(
            $"/exercises/{coachExercise}/upload-url", new { ContentType = "video/mp4" }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Trainer_UpdateSystemExercise_StillReturns400SystemExercise()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var systemExercise = await InsertExerciseAsync(null, isCustom: false);

        var response = await coach.Http.DeleteAsync($"/exercises/{systemExercise}?Version=1", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.SystemExercise);
    }
}
