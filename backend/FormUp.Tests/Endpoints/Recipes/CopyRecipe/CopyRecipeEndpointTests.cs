using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace FormUp.Tests.Endpoints.Recipes.CopyRecipe;

/// <summary>
/// Real-pipeline tests for <c>POST /recipes/{RecipeId}/copy</c>. The Mongo mock ignores filters and
/// <c>CreatedAtAsync</c> needs the full host, so every test runs against the factory.
/// </summary>
[Collection(TestCollection.Name)]
public class CopyRecipeEndpointTests(FitnessApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private FakeBlobStorageService Blobs => (FakeBlobStorageService)factory.Services.GetRequiredService<IBlobStorageService>();

    private async Task<Food> InsertFoodAsync(Guid? ownerId, FoodVisibility visibility = FoodVisibility.Public, bool isDeleted = false)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        var food = new Food
        {
            ExternalId = Guid.NewGuid(),
            Name = $"Copy Recipe Food {Guid.NewGuid():N}",
            NutritionistId = ownerId,
            Visibility = visibility,
            IsDeleted = isDeleted,
            NutrientValue = new NutrientValue { Kcal = 200, Protein = 10, Carbs = 20, Fat = 5 },
            Category = FoodCategory.Dairy,
            DateCreated = DateTime.UtcNow
        };
        await mongo.Foods.InsertOneAsync(food, cancellationToken: Ct);

        return food;
    }

    private async Task<Recipe> InsertRecipeAsync(
        Guid ownerId,
        Guid ingredientFoodId,
        RecipeVisibility visibility = RecipeVisibility.Public,
        string? imageUrl = null,
        List<string>? gallery = null,
        Guid? recipeId = null)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        var recipe = new Recipe
        {
            ExternalId = recipeId ?? Guid.NewGuid(),
            NutritionistId = ownerId,
            Name = $"Copy Source Recipe {Guid.NewGuid():N}",
            Description = "Source description",
            Servings = 3,
            Difficulty = RecipeDifficulty.Easy,
            PrepTimeMinutes = 10,
            CookTimeMinutes = 20,
            MealTypes = ["Dinner"],
            DietaryPreferences = ["Vegetarian"],
            Steps = [new RecipeStep { Title = "Cook", Description = "Cook it", Minutes = 20 }],
            Note = "Source note",
            Foods =
            [
                new MealFood
                {
                    FoodExternalId = ingredientFoodId,
                    FoodName = "Stale name",
                    NutrientValuePer100Grams = new NutrientValue { Kcal = 1, Protein = 1, Carbs = 1, Fat = 1 },
                    AmountGrams = 150,
                    Note = "Chopped"
                }
            ],
            TotalNutrients = new NutrientTotals { Kcal = 9999, Protein = 9999, Carbs = 9999, Fat = 9999 },
            Visibility = visibility,
            ImageUrl = imageUrl,
            GalleryImageUrls = gallery ?? [],
            DateCreated = DateTime.UtcNow.AddDays(-3),
            Version = 4
        };
        await mongo.Recipes.InsertOneAsync(recipe, cancellationToken: Ct);

        return recipe;
    }

    private async Task<Recipe> LoadRecipeAsync(Guid recipeId)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        return await mongo.Recipes.Find(r => r.ExternalId == recipeId).FirstAsync(Ct);
    }

    private static async Task<Guid> CopiedIdAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: Ct);

        return body.GetProperty("recipeId").GetGuid();
    }

    [Fact]
    public async Task CopyRecipe_PublicForeignRecipe_Returns201PrivateOwnedCopyWithRecomputedTotals()
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var food = await InsertFoodAsync(Guid.NewGuid());
        var source = await InsertRecipeAsync(Guid.NewGuid(), food.ExternalId);

        var response = await coach.Http.PostAsJsonAsync($"/recipes/{source.ExternalId}/copy", new { }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: Ct);
        body.GetProperty("visibility").GetString().Should().Be("Private");
        body.GetProperty("isOwnedByCurrentUser").GetBoolean().Should().BeTrue();

        var copy = await LoadRecipeAsync(body.GetProperty("recipeId").GetGuid());
        copy.ExternalId.Should().NotBe(source.ExternalId);
        copy.NutritionistId.Should().Be(coach.UserId);
        copy.Visibility.Should().Be(RecipeVisibility.Private);
        copy.Version.Should().Be(1);
        copy.DateUpdated.Should().BeNull();
        copy.Name.Should().Be(source.Name);
        copy.Servings.Should().Be(3);
        copy.Difficulty.Should().Be(RecipeDifficulty.Easy);
        copy.Steps.Should().ContainSingle().Which.Title.Should().Be("Cook");
        copy.Note.Should().Be("Source note");

        copy.Foods.Should().ContainSingle();
        copy.Foods[0].AmountGrams.Should().Be(150);
        copy.Foods[0].Note.Should().Be("Chopped");
        copy.Foods[0].FoodName.Should().Be(food.Name, "the snapshot is rebuilt from the current food, not trusted from the source");
        copy.Foods[0].NutrientValuePer100Grams.Kcal.Should().Be(200);
        copy.TotalNutrients.Kcal.Should().Be(300m);
        copy.TotalNutrients.Protein.Should().Be(15m);

        var untouched = await LoadRecipeAsync(source.ExternalId);
        untouched.NutritionistId.Should().Be(source.NutritionistId);
        untouched.Visibility.Should().Be(RecipeVisibility.Public);
        untouched.Version.Should().Be(4);
        untouched.TotalNutrients.Kcal.Should().Be(9999m);
    }

    [Fact]
    public async Task CopyRecipe_OwnPrivateRecipe_Returns201()
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var food = await InsertFoodAsync(coach.UserId, FoodVisibility.Private);
        var source = await InsertRecipeAsync(coach.UserId, food.ExternalId, RecipeVisibility.Private);

        var response = await coach.Http.PostAsJsonAsync($"/recipes/{source.ExternalId}/copy", new { }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await CopiedIdAsync(response)).Should().NotBe(source.ExternalId);
    }

    [Fact]
    public async Task CopyRecipe_PrivateForeignRecipe_Returns404()
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var food = await InsertFoodAsync(Guid.NewGuid());
        var source = await InsertRecipeAsync(Guid.NewGuid(), food.ExternalId, RecipeVisibility.Private);

        var response = await coach.Http.PostAsJsonAsync($"/recipes/{source.ExternalId}/copy", new { }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CopyRecipe_UnknownId_Returns404()
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);

        var response = await coach.Http.PostAsJsonAsync($"/recipes/{Guid.NewGuid()}/copy", new { }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CopyRecipe_PlanWithoutRecipes_Returns403SubscriptionRequired()
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.MoveToPlanAsync(factory, coach, plan => plan.CanUseRecipes = false);
        var food = await InsertFoodAsync(Guid.NewGuid());
        var source = await InsertRecipeAsync(Guid.NewGuid(), food.ExternalId);

        var response = await coach.Http.PostAsJsonAsync($"/recipes/{source.ExternalId}/copy", new { }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.SubscriptionRequired);
    }

    [Theory]
    [InlineData("hard-deleted")]
    [InlineData("soft-deleted")]
    [InlineData("private-foreign")]
    public async Task CopyRecipe_IngredientNotAvailable_Returns400RecipeFoodNotAvailable(string scenario)
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var ingredientId = scenario switch
        {
            "hard-deleted" => Guid.NewGuid(),
            "soft-deleted" => (await InsertFoodAsync(Guid.NewGuid(), isDeleted: true)).ExternalId,
            _ => (await InsertFoodAsync(Guid.NewGuid(), FoodVisibility.Private)).ExternalId
        };
        var source = await InsertRecipeAsync(Guid.NewGuid(), ingredientId);

        var response = await coach.Http.PostAsJsonAsync($"/recipes/{source.ExternalId}/copy", new { }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.RecipeFoodNotAvailable);
    }

    [Fact]
    public async Task CopyRecipe_WithImages_DuplicatesBlobsUnderTheCopyAndKeepsSourceBlobs()
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var food = await InsertFoodAsync(Guid.NewGuid());
        var sourceId = Guid.NewGuid();
        var mainPath = $"recipes/{sourceId}/main-{Guid.NewGuid():N}.webp";
        var galleryPath = $"recipes/{sourceId}/gallery-{Guid.NewGuid():N}.jpg";
        Blobs.SeedObject(mainPath, [1, 2, 3]);
        Blobs.SeedObject(galleryPath, [4, 5, 6]);
        var source = await InsertRecipeAsync(
            Guid.NewGuid(), food.ExternalId, imageUrl: mainPath, gallery: [galleryPath], recipeId: sourceId);

        var response = await coach.Http.PostAsJsonAsync($"/recipes/{source.ExternalId}/copy", new { }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var copy = await LoadRecipeAsync(await CopiedIdAsync(response));
        copy.ImageUrl.Should().MatchRegex($"^recipes/{copy.ExternalId}/main-[0-9a-f]{{32}}\\.webp$");
        copy.GalleryImageUrls.Should().ContainSingle().Which.Should().MatchRegex($"^recipes/{copy.ExternalId}/gallery-[0-9a-f]{{32}}\\.jpg$");
        Blobs.UploadedPaths.Should().Contain(copy.ImageUrl!);
        (await Blobs.ObjectExistsAsync(mainPath, Ct)).Should().BeTrue("the source blob must stay in place");
        Blobs.DeletedPaths.Should().NotContain(mainPath);

        var untouched = await LoadRecipeAsync(source.ExternalId);
        untouched.ImageUrl.Should().Be(mainPath);
    }

    [Fact]
    public async Task CopyRecipe_ImageBlobMissing_SucceedsWithoutImage()
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var food = await InsertFoodAsync(Guid.NewGuid());
        var sourceId = Guid.NewGuid();
        var source = await InsertRecipeAsync(
            Guid.NewGuid(), food.ExternalId, imageUrl: $"recipes/{sourceId}/main-{Guid.NewGuid():N}.jpg", recipeId: sourceId);

        var response = await coach.Http.PostAsJsonAsync($"/recipes/{source.ExternalId}/copy", new { }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await LoadRecipeAsync(await CopiedIdAsync(response))).ImageUrl.Should().BeNull();
    }
}
