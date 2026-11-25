using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.Recipes.GetRecipe;
using FormUp.Application.Features.Recipes.Shared;
using FormUp.Application.Infrastructure.Data.MongoDb;
using MongoDB.Driver;

namespace FormUp.Application.Features.Recipes.CopyRecipe;

/// <summary>
/// Copies any recipe the caller can see (their own, or a public one) into a new private recipe they own.
/// The ingredient snapshots and totals are rebuilt from the current foods, not trusted from the source.
/// </summary>
/// <param name="mongo">MongoDB context.</param>
/// <param name="imageUpload">Validates the source image URLs.</param>
/// <param name="blobStorage">Blob storage used to duplicate the images.</param>
/// <param name="timeProvider">Clock.</param>
/// <param name="logger">Logger.</param>
[RemovedCoachRole(AppRoles.Nutritionist, RemovedCoachRoleMode.Refuse)]
internal sealed class CopyRecipeEndpoint(
    IMongoContext mongo,
    IImageUploadService imageUpload,
    IBlobStorageService blobStorage,
    TimeProvider timeProvider,
    ILogger<CopyRecipeEndpoint> logger) : Endpoint<CopyRecipeRequest, GetRecipeResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/recipes/{RecipeId}/copy");
        Roles(AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Copy recipe to own library";
            s.Description = "Clones a visible recipe (own, or another coach's or the system's public recipe) into a new "
                + "private recipe owned by the caller, with its images duplicated. Ingredients are re-resolved against "
                + "the caller's visible foods and the totals recalculated. Tag assignments are not copied.";
            s.Response<GetRecipeResponse>(StatusCodes.Status201Created, "Copy created");
            s.Response(StatusCodes.Status400BadRequest, "An ingredient food is deleted or not visible to the caller");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or invalid credentials");
            s.Response(StatusCodes.Status403Forbidden, "The subscription does not include recipes, or the coach role was removed");
            s.Response(StatusCodes.Status404NotFound, "Recipe not found, or another coach's private recipe");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(CopyRecipeRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);

        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var scope = LibraryCallerScope.From(User, Guid.Parse(userId));
        var nutritionistId = scope.UserId;

        if (await this.RespondIfCoachFeatureNotEntitledAsync(
                nutritionistId, entitlements => entitlements.CanUseRecipes,
                "The current subscription does not include recipes.", ct))
        {
            return;
        }

        var filter = Builders<Recipe>.Filter.Eq(r => r.ExternalId, req.RecipeId)
            & RecipeVisibilityFilter.BuildOwnOrPublic(nutritionistId);

        using var cursor = await mongo.Recipes.FindAsync(filter, cancellationToken: ct);
        var source = await cursor.FirstOrDefaultAsync(ct);

        if (source is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var foodLookup = await RecipeContent.LoadUsableFoodsAsync(
            mongo, source.Foods.Select(f => f.FoodExternalId), scope, ct);

        var mealFoods = new List<MealFood>();

        foreach (var sourceFood in source.Foods)
        {
            if (!foodLookup.TryGetValue(sourceFood.FoodExternalId, out var food))
            {
                this.ThrowErrorWithCode(ErrorCodes.RecipeFoodNotAvailable,
                    $"Food with ID '{sourceFood.FoodExternalId}' is not available.");
                return;
            }

            mealFoods.Add(RecipeContent.ToMealFood(food, new RecipeFoodDto
            {
                FoodExternalId = sourceFood.FoodExternalId,
                AmountGrams = sourceFood.AmountGrams,
                Note = sourceFood.Note
            }));
        }

        var copy = new Recipe
        {
            ExternalId = Guid.NewGuid(),
            NutritionistId = nutritionistId,
            Name = source.Name,
            Description = source.Description,
            PrepTimeMinutes = source.PrepTimeMinutes,
            CookTimeMinutes = source.CookTimeMinutes,
            Servings = source.Servings,
            Difficulty = source.Difficulty,
            MealTypes = source.MealTypes is null ? null : [.. source.MealTypes],
            DietaryPreferences = [.. source.DietaryPreferences],
            Steps = source.Steps?
                .Select(step => new RecipeStep { Title = step.Title, Description = step.Description, Minutes = step.Minutes })
                .ToList(),
            Note = source.Note,
            Foods = mealFoods,
            TotalNutrients = RecipeContent.CalculateTotals(mealFoods),
            Visibility = RecipeVisibility.Private,
            DateCreated = timeProvider.GetUtcNow().UtcDateTime,
            DateUpdated = null,
            Version = 1
        };

        var imageCopier = new LibraryImageCopier(imageUpload, blobStorage, logger);

        copy.ImageUrl = await imageCopier.CopyAsync(
            ImageUploadScope.Recipe, source.ExternalId, copy.ExternalId, RecipeImageKeys.MainSlot, source.ImageUrl, ct);
        copy.GalleryImageUrls = await imageCopier.CopyGalleryAsync(
            ImageUploadScope.Recipe, source.ExternalId, copy.ExternalId, RecipeImageKeys.GallerySlot, source.GalleryImageUrls, ct);

        await mongo.Recipes.InsertOneAsync(copy, cancellationToken: ct);

        await Send.CreatedAtAsync<GetRecipeEndpoint>(
            new { RecipeId = copy.ExternalId },
            GetRecipeResponse.FromDocument(copy, nutritionistId, RecipeContent.DeriveAllergens(copy, foodLookup)),
            cancellation: ct);
    }
}
