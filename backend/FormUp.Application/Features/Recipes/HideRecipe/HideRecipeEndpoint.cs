using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Features.Recipes.Shared;
using FormUp.Application.Infrastructure.Data.MongoDb;
using MongoDB.Driver;

namespace FormUp.Application.Features.Recipes.HideRecipe;

/// <summary>
/// Hides a visible recipe from the caller's own library. The recipe stays visible to everyone else.
/// </summary>
/// <param name="mongo">MongoDB context.</param>
[RemovedCoachRole(AppRoles.Nutritionist, RemovedCoachRoleMode.Refuse)]
internal sealed class HideRecipeEndpoint(IMongoContext mongo) : Endpoint<HideRecipeRequest>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Put("/recipes/{RecipeId}/hide");
        Roles(AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Hide recipe";
            s.Description = "Hides another coach's or the system's public recipe from the caller's library. "
                + "Idempotent. A coach cannot hide a recipe they own.";
            s.Response(StatusCodes.Status204NoContent, "Recipe hidden");
            s.Response(StatusCodes.Status400BadRequest, "The caller owns the recipe (RECIPE_HIDE_OWN_NOT_ALLOWED)");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or invalid credentials");
            s.Response(StatusCodes.Status403Forbidden, "The coach role was removed");
            s.Response(StatusCodes.Status404NotFound, "Recipe not found, or another coach's private recipe");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(HideRecipeRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);

        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var nutritionistId = Guid.Parse(userId);

        var filter = Builders<Recipe>.Filter.Eq(r => r.ExternalId, req.RecipeId)
            & RecipeVisibilityFilter.BuildOwnOrPublic(nutritionistId);

        using var cursor = await mongo.Recipes.FindAsync(filter, cancellationToken: ct);
        var recipe = await cursor.FirstOrDefaultAsync(ct);

        if (recipe is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (recipe.NutritionistId == nutritionistId)
        {
            this.ThrowErrorWithCode(ErrorCodes.RecipeHideOwnNotAllowed, "You cannot hide a recipe you own. Delete it instead.");
            return;
        }

        // Deliberately no Version bump: hiding is a per-coach view preference, not an edit of the recipe.
        await mongo.Recipes.UpdateOneAsync(
            Builders<Recipe>.Filter.Eq(r => r.ExternalId, req.RecipeId),
            Builders<Recipe>.Update.AddToSet(r => r.HiddenByNutritionistIds, nutritionistId),
            cancellationToken: ct);

        await Send.NoContentAsync(ct);
    }
}
