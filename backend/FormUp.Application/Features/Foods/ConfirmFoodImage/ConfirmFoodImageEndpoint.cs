using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Features.Foods.Shared;
using FormUp.Application.Infrastructure.Data.MongoDb;
using MongoDB.Driver;

namespace FormUp.Application.Features.Foods.ConfirmFoodImage;

/// <summary>
/// Persists the food image blob URL on the food document: main slot sets <c>ImageUrl</c>, gallery slot
/// appends to <c>GalleryImageUrls</c> (cap = 6). The blobUrl must be a key issued for this food and slot.
/// </summary>
/// <param name="mongo">MongoDB context.</param>
/// <param name="imageUpload">Image upload service — validates the blobUrl against this food's presigned key.</param>
[RemovedCoachRole(AppRoles.Nutritionist, RemovedCoachRoleMode.Refuse)]
public class ConfirmFoodImageEndpoint(IMongoContext mongo, IImageUploadService imageUpload) : Endpoint<ConfirmFoodImageRequest>
{
    private const int GalleryCap = 6;

    /// <inheritdoc />
    public override void Configure()
    {
        Put("/foods/{FoodId}/image");
        Roles(AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Confirm food image upload";
            s.Description = "Sets the image URL on the food document after a successful blob upload. "
                            + "Pass the blobUrl returned by POST /foods/{id}/image/upload-url. "
                            + "Use slot=main to set the main image (replaces it); slot=gallery to append "
                            + "to the gallery (max 6 entries). "
                            + "Only the nutritionist who created the food can confirm its images.";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(ConfirmFoodImageRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);

        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var nutritionistId = Guid.Parse(userId);

        if (await this.RespondIfCoachFeatureNotEntitledAsync(
                nutritionistId, entitlements => entitlements.CanUseIngredients,
                "The current subscription does not include ingredients.", ct))
        {
            return;
        }

        var filter = Builders<Food>.Filter.Eq(f => f.ExternalId, req.FoodId)
            & Builders<Food>.Filter.Eq(f => f.IsDeleted, false);

        using var cursor = await mongo.Foods.FindAsync(filter, cancellationToken: ct);
        var food = await cursor.FirstOrDefaultAsync(ct);

        if (food is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (food.NutritionistId != nutritionistId)
        {
            this.ThrowErrorWithCode(ErrorCodes.FoodNotOwned, "You can only set the image on your own custom foods.");
            return;
        }

        var isGallery = req.Slot.Equals("gallery", StringComparison.OrdinalIgnoreCase);

        // Re-check gallery cap at confirm time (race: another confirm could have filled it
        // between the upload-url call and this confirm call).
        if (isGallery && food.GalleryImageUrls.Count >= GalleryCap)
        {
            this.ThrowErrorWithCode(ErrorCodes.FoodGalleryFull,
                $"The food gallery is full. Maximum {GalleryCap} gallery images are allowed.");
            return;
        }

        var slot = isGallery ? FoodImageKeys.GallerySlot : FoodImageKeys.MainSlot;

        // Only a key issued by upload-url for this food and slot may be persisted, otherwise an arbitrary
        // URL would be rendered to other coaches browsing the public food library.
        if (!IsValidKey(req, food, slot))
        {
            this.ThrowErrorWithCode(ErrorCodes.InvalidBlobUrl, "BlobUrl does not match this food's image upload key.");
            return;
        }

        // The IsDeleted guard covers a concurrent soft-delete between the Find above and the write.
        var ownedFood = Builders<Food>.Filter.Eq(f => f.ExternalId, req.FoodId)
            & Builders<Food>.Filter.Eq(f => f.NutritionistId, nutritionistId)
            & Builders<Food>.Filter.Eq(f => f.IsDeleted, false);

        if (isGallery)
        {
            // Atomic append: room left (no 6th element) and URL not already present, so a
            // concurrent confirm cannot overflow the cap or duplicate the entry.
            var result = await mongo.Foods.UpdateOneAsync(
                ownedFood
                    & Builders<Food>.Filter.Exists($"galleryImageUrls.{GalleryCap - 1}", false)
                    & Builders<Food>.Filter.Not(Builders<Food>.Filter.AnyEq(f => f.GalleryImageUrls, req.BlobUrl)),
                Builders<Food>.Update
                    .Push(f => f.GalleryImageUrls, req.BlobUrl)
                    .Set(f => f.DateUpdated, DateTime.UtcNow),
                cancellationToken: ct);

            if (result.ModifiedCount == 0)
            {
                this.ThrowErrorWithCode(ErrorCodes.FoodGalleryFull,
                    $"The food gallery is full. Maximum {GalleryCap} gallery images are allowed.");
                return;
            }

            await Send.NoContentAsync(ct);
            return;
        }

        await mongo.Foods.UpdateOneAsync(
            ownedFood,
            Builders<Food>.Update
                .Set(f => f.ImageUrl, req.BlobUrl)
                .Set(f => f.DateUpdated, DateTime.UtcNow),
            cancellationToken: ct);

        await Send.NoContentAsync(ct);
    }

    private bool IsValidKey(ConfirmFoodImageRequest req, Food food, string slot)
    {
        if (!FoodImageKeys.TryParse(req.BlobUrl, slot, out var keyId))
        {
            return false;
        }

        if (!imageUpload.IsValidBlobUrlForSubPath(ImageUploadScope.Food, $"{req.FoodId}/{slot}-{keyId:N}", req.BlobUrl))
        {
            return false;
        }

        return req.BlobUrl != food.ImageUrl && !food.GalleryImageUrls.Contains(req.BlobUrl);
    }
}
