using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Enums;
using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Infrastructure.Data.MongoDb;
using MongoDB.Driver;

namespace FormUp.Application.Features.Recipes.DeleteRecipeImage;

/// <summary>
/// Clears the main image URL on a recipe. The gallery is untouched and the blob is not deleted.
/// Only the nutritionist who created the recipe can remove its image.
/// </summary>
/// <param name="mongo">MongoDB context.</param>
[RemovedCoachRole(AppRoles.Nutritionist, RemovedCoachRoleMode.Refuse)]
public class DeleteRecipeImageEndpoint(IMongoContext mongo) : Endpoint<DeleteRecipeImageRequest>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Delete("/recipes/{RecipeId}/image");
        Roles(AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Remove recipe image";
            s.Description = "Clears the main ImageUrl on the recipe. The gallery is left untouched and the "
                            + "underlying blob is not deleted. Idempotent — returns 204 even if the image is "
                            + "already unset. Only the nutritionist who created the recipe can remove its image.";
            s.Responses[StatusCodes.Status204NoContent] = "Image cleared (or was already unset)";
            s.Responses[StatusCodes.Status400BadRequest] = "RECIPE_NOT_OWNED — the caller does not own this recipe";
            s.Responses[StatusCodes.Status404NotFound] = "Recipe not found";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(DeleteRecipeImageRequest req, CancellationToken ct)
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

        using var cursor = await mongo.Recipes.FindAsync(
            Builders<Recipe>.Filter.Eq(r => r.ExternalId, req.RecipeId), cancellationToken: ct);
        var recipe = await cursor.FirstOrDefaultAsync(ct);

        if (recipe is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (recipe.NutritionistId != nutritionistId)
        {
            this.ThrowErrorWithCode(ErrorCodes.RecipeNotOwned, "You can only remove the image on your own recipes.");
            return;
        }

        await mongo.Recipes.UpdateOneAsync(
            Builders<Recipe>.Filter.Eq(r => r.ExternalId, req.RecipeId)
                & Builders<Recipe>.Filter.Eq(r => r.NutritionistId, nutritionistId),
            Builders<Recipe>.Update
                .Unset(r => r.ImageUrl)
                .Set(r => r.DateUpdated, DateTime.UtcNow),
            cancellationToken: ct);

        await Send.NoContentAsync(ct);
    }
}
