using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Features.Recipes.Shared;
using FormUp.Application.Infrastructure.Data.MongoDb;
using MongoDB.Driver;

namespace FormUp.Application.Features.Recipes.ConfirmRecipeImage;

/// <summary>
/// Persists the recipe image blob URL on the recipe document.
/// Main slot overwrites <c>ImageUrl</c>. Gallery slot appends to <c>GalleryImageUrls</c> (cap = 6).
/// Only the nutritionist who created the recipe can set its images. The blobUrl must be the exact
/// presigned key issued for this recipe and slot, so a caller cannot persist an arbitrary URL that
/// is later rendered to other coaches.
/// </summary>
/// <param name="mongo">MongoDB context.</param>
/// <param name="imageUpload">Image upload service — validates the blobUrl against this recipe's presigned key.</param>
[RemovedCoachRole(AppRoles.Nutritionist, RemovedCoachRoleMode.Refuse)]
public class ConfirmRecipeImageEndpoint(IMongoContext mongo, IImageUploadService imageUpload) : Endpoint<ConfirmRecipeImageRequest>
{
    private const int GalleryCap = 6;

    /// <inheritdoc />
    public override void Configure()
    {
        Put("/recipes/{RecipeId}/image");
        Roles(AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Confirm recipe image upload";
            s.Description = "Sets the image URL on the recipe document after a successful blob upload. "
                            + "Pass the blobUrl returned by POST /recipes/{id}/image/upload-url. "
                            + "Use slot=main to set the main image (overwrites); slot=gallery to append "
                            + "to the gallery (max 6 entries). "
                            + "Only the nutritionist who created the recipe can confirm its images.";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(ConfirmRecipeImageRequest req, CancellationToken ct)
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

        var filter = Builders<Recipe>.Filter.Eq(r => r.ExternalId, req.RecipeId);

        using var cursor = await mongo.Recipes.FindAsync(filter, cancellationToken: ct);
        var recipe = await cursor.FirstOrDefaultAsync(ct);

        if (recipe is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (recipe.NutritionistId != nutritionistId)
        {
            this.ThrowErrorWithCode(ErrorCodes.RecipeNotOwned,
                "You can only set images on your own recipes.");
            return;
        }

        var isGallery = req.Slot.Equals("gallery", StringComparison.OrdinalIgnoreCase);

        // Re-check gallery cap at confirm time (race: another confirm could have filled it
        // between the upload-url call and this confirm call).
        if (isGallery && recipe.GalleryImageUrls.Count >= GalleryCap)
        {
            this.ThrowErrorWithCode(ErrorCodes.RecipeGalleryFull,
                $"The recipe gallery is full. Maximum {GalleryCap} gallery images are allowed.");
            return;
        }

        var slot = isGallery ? RecipeImageKeys.GallerySlot : RecipeImageKeys.MainSlot;

        if (!IsValidKey(req, recipe, slot))
        {
            this.ThrowErrorWithCode(ErrorCodes.InvalidBlobUrl, "BlobUrl does not match this recipe's image upload key.");
            return;
        }

        var ownedRecipe = Builders<Recipe>.Filter.Eq(r => r.ExternalId, req.RecipeId)
            & Builders<Recipe>.Filter.Eq(r => r.NutritionistId, nutritionistId);

        if (isGallery)
        {
            // Atomic append: owner, room left (no 6th element) and URL not already present, so a
            // concurrent confirm cannot overflow the cap or duplicate the entry.
            var result = await mongo.Recipes.UpdateOneAsync(
                ownedRecipe
                    & Builders<Recipe>.Filter.Exists($"galleryImageUrls.{GalleryCap - 1}", false)
                    & Builders<Recipe>.Filter.Not(Builders<Recipe>.Filter.AnyEq(r => r.GalleryImageUrls, req.BlobUrl)),
                Builders<Recipe>.Update
                    .Push(r => r.GalleryImageUrls, req.BlobUrl)
                    .Set(r => r.DateUpdated, DateTime.UtcNow),
                cancellationToken: ct);

            if (result.ModifiedCount == 0)
            {
                this.ThrowErrorWithCode(ErrorCodes.RecipeGalleryFull,
                    $"The recipe gallery is full. Maximum {GalleryCap} gallery images are allowed.");
                return;
            }

            await Send.NoContentAsync(ct);
            return;
        }

        // Ownership filter guards against a concurrent delete or reassignment since the Find above.
        await mongo.Recipes.UpdateOneAsync(
            ownedRecipe,
            Builders<Recipe>.Update
                .Set(r => r.ImageUrl, req.BlobUrl)
                .Set(r => r.DateUpdated, DateTime.UtcNow),
            cancellationToken: ct);

        await Send.NoContentAsync(ct);
    }

    private bool IsValidKey(ConfirmRecipeImageRequest req, Recipe recipe, string slot)
    {
        if (!RecipeImageKeys.TryParse(req.BlobUrl, slot, out var keyId))
        {
            return false;
        }

        if (!imageUpload.IsValidBlobUrlForSubPath(ImageUploadScope.Recipe, $"{req.RecipeId}/{slot}-{keyId:N}", req.BlobUrl))
        {
            return false;
        }

        return req.BlobUrl != recipe.ImageUrl && !recipe.GalleryImageUrls.Contains(req.BlobUrl);
    }
}
