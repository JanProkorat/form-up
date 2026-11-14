using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Enums;
using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Infrastructure.Data.MongoDb;
using MongoDB.Driver;

namespace FormUp.Application.Features.Recipes.RemoveRecipeGalleryImage;

/// <summary>
/// Removes one gallery image URL from a recipe. The blob is not deleted and the recipe Version is untouched.
/// </summary>
/// <param name="mongo">MongoDB context.</param>
[RemovedCoachRole(AppRoles.Nutritionist, RemovedCoachRoleMode.Refuse)]
public class RemoveRecipeGalleryImageEndpoint(IMongoContext mongo) : Endpoint<RemoveRecipeGalleryImageRequest>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Delete("/recipes/{RecipeId}/gallery");
        Roles(AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Remove a recipe gallery image";
            s.Description = "Pulls the exact imageUrl (query parameter) from the recipe's gallery, keeping the order "
                            + "of the remaining entries. Idempotent: returns 204 even if the URL is not in the gallery. "
                            + "The blob is not deleted. Only the recipe's creator can remove its gallery images.";
            s.Responses[StatusCodes.Status204NoContent] = "Image removed (or was not in the gallery)";
            s.Responses[StatusCodes.Status400BadRequest] = "RECIPE_NOT_OWNED, or invalid imageUrl";
            s.Responses[StatusCodes.Status404NotFound] = "Recipe not found";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(RemoveRecipeGalleryImageRequest req, CancellationToken ct)
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
            this.ThrowErrorWithCode(ErrorCodes.RecipeNotOwned, "You can only remove images on your own recipes.");
            return;
        }

        await mongo.Recipes.UpdateOneAsync(
            Builders<Recipe>.Filter.Eq(r => r.ExternalId, req.RecipeId)
                & Builders<Recipe>.Filter.Eq(r => r.NutritionistId, nutritionistId),
            Builders<Recipe>.Update
                .Pull(r => r.GalleryImageUrls, req.ImageUrl)
                .Set(r => r.DateUpdated, DateTime.UtcNow),
            cancellationToken: ct);

        await Send.NoContentAsync(ct);
    }
}
