using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Enums;
using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Infrastructure.Data.MongoDb;
using MongoDB.Driver;

namespace FormUp.Application.Features.Recipes.PromoteRecipeGalleryImage;

/// <summary>
/// Makes a gallery image the recipe's main image. The previous main image takes the promoted entry's
/// gallery position; with no main image the entry simply moves out of the gallery. Version is untouched.
/// </summary>
/// <param name="mongo">MongoDB context.</param>
[RemovedCoachRole(AppRoles.Nutritionist, RemovedCoachRoleMode.Refuse)]
public class PromoteRecipeGalleryImageEndpoint(IMongoContext mongo) : Endpoint<PromoteRecipeGalleryImageRequest>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/recipes/{RecipeId}/gallery/promote");
        Roles(AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Promote a gallery image to main";
            s.Description = "Swaps the given gallery image with the current main image (same gallery position). "
                            + "With no main image the entry is removed from the gallery and set as main. "
                            + "Promoting the current main image is a no-op. Only the recipe's creator can promote.";
            s.Responses[StatusCodes.Status204NoContent] = "Image promoted (or already main)";
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest,"RECIPE_NOT_OWNED or RECIPE_GALLERY_IMAGE_NOT_FOUND");
            s.Response(StatusCodes.Status404NotFound, "Recipe not found");
            s.Response(StatusCodes.Status409Conflict, "RECIPE_VERSION_CONFLICT — the images changed concurrently");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(PromoteRecipeGalleryImageRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);

        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var nutritionistId = Guid.Parse(userId);

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
            this.ThrowErrorWithCode(ErrorCodes.RecipeNotOwned, "You can only change images on your own recipes.");
            return;
        }

        if (req.ImageUrl == recipe.ImageUrl)
        {
            await Send.NoContentAsync(ct);
            return;
        }

        var index = recipe.GalleryImageUrls.IndexOf(req.ImageUrl);

        if (index < 0)
        {
            this.ThrowErrorWithCode(ErrorCodes.RecipeGalleryImageNotFound, "The image is not in this recipe's gallery.");
            return;
        }

        var newGallery = new List<string>(recipe.GalleryImageUrls);

        if (string.IsNullOrEmpty(recipe.ImageUrl))
        {
            newGallery.RemoveAt(index);
        }
        else
        {
            newGallery[index] = recipe.ImageUrl;
        }

        // Compare-and-swap on the loaded main image and gallery so a concurrent picture change is not clobbered.
        var result = await mongo.Recipes.UpdateOneAsync(
            Builders<Recipe>.Filter.Eq(r => r.ExternalId, req.RecipeId)
                & Builders<Recipe>.Filter.Eq(r => r.NutritionistId, nutritionistId)
                & Builders<Recipe>.Filter.Eq(r => r.ImageUrl, recipe.ImageUrl)
                & Builders<Recipe>.Filter.Eq(r => r.GalleryImageUrls, recipe.GalleryImageUrls),
            Builders<Recipe>.Update
                .Set(r => r.ImageUrl, req.ImageUrl)
                .Set(r => r.GalleryImageUrls, newGallery)
                .Set(r => r.DateUpdated, DateTime.UtcNow),
            cancellationToken: ct);

        if (result.ModifiedCount == 0)
        {
            await this.SendProblemAsync(409, ErrorCodes.RecipeVersionConflict,
                "The recipe images were changed by another request. Reload and try again.", ct);
            return;
        }

        await Send.NoContentAsync(ct);
    }
}
