using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Enums;
using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.Recipes.Shared;
using FormUp.Application.Infrastructure.Data.MongoDb;
using Microsoft.AspNetCore.Http;
using MongoDB.Driver;

namespace FormUp.Application.Features.Recipes.UpdateRecipe;

/// <summary>
/// Updates an existing recipe with new food data and recalculated nutrient totals.
/// Uses optimistic concurrency — the client must supply the current Version and it is bumped on each write.
/// </summary>
/// <param name="mongo">MongoDB context.</param>
[RemovedCoachRole(AppRoles.Nutritionist, RemovedCoachRoleMode.Refuse)]
public class UpdateRecipeEndpoint(IMongoContext mongo)
    : Endpoint<UpdateRecipeRequest, GetRecipeResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Put("/recipes/{RecipeId}");
        Roles(AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Update recipe";
            s.Description = "Updates an existing recipe's name, description, and food items. " +
                            "Uses optimistic concurrency via the Version field.";
            s.Responses[StatusCodes.Status409Conflict] = "Version conflict — the recipe was modified by another request";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(UpdateRecipeRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);

        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var nutritionistId = Guid.Parse(userId);

        if (await this.RespondIfCoachFeatureNotEntitledAsync(
                nutritionistId, entitlements => entitlements.CanUseRecipes,
                "The current subscription does not include recipes.", ct))
        {
            return;
        }

        // Find existing recipe owned by this nutritionist
        var filter = Builders<Recipe>.Filter.Eq(r => r.ExternalId, req.RecipeId)
            & Builders<Recipe>.Filter.Eq(r => r.NutritionistId, nutritionistId);

        using var recipeCursor = await mongo.Recipes.FindAsync(filter, cancellationToken: ct);
        var recipe = await recipeCursor.FirstOrDefaultAsync(ct);

        if (recipe is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // Early optimistic concurrency check (in-memory, before the DB write)
        if (recipe.Version != req.Version)
        {
            await this.SendProblemAsync(409, ErrorCodes.RecipeVersionConflict,
                "Version conflict. The recipe was modified by another request.", ct);
            return;
        }

        var foodLookup = await RecipeContent.LoadUsableFoodsAsync(
            mongo, req.Foods.Select(f => f.FoodExternalId), nutritionistId, ct);

        var mealFoods = new List<MealFood>();

        foreach (var item in req.Foods)
        {
            if (!foodLookup.TryGetValue(item.FoodExternalId, out var food))
            {
                this.ThrowErrorWithCode(ErrorCodes.RecipeFoodNotAvailable,
                    $"Food with ID '{item.FoodExternalId}' is not available.");
                return;
            }

            mealFoods.Add(RecipeContent.ToMealFood(food, item));
        }

        recipe.Name = req.Name;
        recipe.Description = req.Description;
        recipe.PrepTimeMinutes = req.PrepTimeMinutes;
        recipe.CookTimeMinutes = req.CookTimeMinutes;
        recipe.Servings = req.Servings;
        recipe.Difficulty = req.Difficulty;
        recipe.MealTypes = FoodEnumListMapping.ToStoredNames(req.MealTypes.Distinct());
        recipe.DietaryPreferences = FoodEnumListMapping.ToStoredNames(req.DietaryPreferences.Distinct());
        recipe.Steps = RecipeContent.NormalizeSteps(req.Steps);
        recipe.Note = req.Note;
        recipe.Foods = mealFoods;
        recipe.TotalNutrients = RecipeContent.CalculateTotals(mealFoods);
        if (req.Visibility.HasValue)
        {
            recipe.Visibility = req.Visibility.Value;
        }
        recipe.DateUpdated = DateTime.UtcNow;
        recipe.Version = req.Version + 1;

        // Version-guarded write: filter includes the pre-mutation version to prevent concurrent writes.
        //
        // Legacy documents (created before optimistic concurrency was added) have no
        // "version" field stored in BSON. The MongoDB.Driver deserializes them using the
        // C# property initializer (= 1), so clients receive Version = 1. However,
        // Eq(version, 1) does NOT match a field-absent BSON document — the equality
        // filter requires the field to exist. To allow the first write on legacy docs
        // to succeed, the filter also matches when: (a) the field is absent AND
        // (b) req.Version == 1 (the only value a client can receive for a legacy doc).
        // After this first write the version field is stored and all subsequent writes
        // use normal CAS (clause (a) is never true again for this document).
        var normalVersionMatch = Builders<Recipe>.Filter.Eq(r => r.Version, req.Version);
        var legacyFieldAbsent = req.Version == 1
            ? Builders<Recipe>.Filter.Not(Builders<Recipe>.Filter.Exists(r => r.Version))
            : null;
        var versionClause = legacyFieldAbsent is not null
            ? Builders<Recipe>.Filter.Or(normalVersionMatch, legacyFieldAbsent)
            : normalVersionMatch;

        var versionFilter = Builders<Recipe>.Filter.Eq(r => r.ExternalId, recipe.ExternalId)
            & versionClause;

        var result = await mongo.Recipes.ReplaceOneAsync(versionFilter, recipe, cancellationToken: ct);

        // Double-guard: if ModifiedCount == 0 a concurrent write beat us
        if (result.ModifiedCount == 0)
        {
            await this.SendProblemAsync(409, ErrorCodes.RecipeVersionConflict,
                "Version conflict. The recipe was modified concurrently.", ct);
            return;
        }

        var response = GetRecipeResponse.FromDocument(
            recipe, nutritionistId, RecipeContent.DeriveAllergens(recipe, foodLookup));

        var tagsByRecipeId = await FoodTagLookup.GetTagsByRecipeIdAsync(mongo, nutritionistId, [recipe.ExternalId], ct);
        response.Tags = tagsByRecipeId.GetValueOrDefault(recipe.ExternalId, []);

        await Send.OkAsync(response, ct);
    }
}
