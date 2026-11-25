using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.Recipes.Shared;
using FormUp.Application.Infrastructure.Data.MongoDb;
using MongoDB.Driver;

namespace FormUp.Application.Features.Recipes.UnhideRecipe;

/// <summary>
/// Restores a recipe the caller hid to their own library.
/// </summary>
/// <param name="mongo">MongoDB context.</param>
[RemovedCoachRole(AppRoles.Nutritionist, RemovedCoachRoleMode.Refuse)]
internal sealed class UnhideRecipeEndpoint(IMongoContext mongo) : Endpoint<UnhideRecipeRequest>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Put("/recipes/{RecipeId}/unhide");
        Roles(AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Unhide recipe";
            s.Description = "Restores a recipe the caller hid. Idempotent: succeeds when the recipe was not hidden.";
            s.Response(StatusCodes.Status204NoContent, "Recipe visible again");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or invalid credentials");
            s.Response(StatusCodes.Status403Forbidden, "The coach role was removed");
            s.Response(StatusCodes.Status404NotFound, "Recipe not found, or another coach's private recipe");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(UnhideRecipeRequest req, CancellationToken ct)
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

        await mongo.Recipes.UpdateOneAsync(
            Builders<Recipe>.Filter.Eq(r => r.ExternalId, req.RecipeId),
            Builders<Recipe>.Update.Pull(r => r.HiddenByNutritionistIds, nutritionistId),
            cancellationToken: ct);

        await Send.NoContentAsync(ct);
    }
}
