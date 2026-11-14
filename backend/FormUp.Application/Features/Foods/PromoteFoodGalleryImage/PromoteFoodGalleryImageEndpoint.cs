using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Infrastructure.Data.MongoDb;
using MongoDB.Driver;

namespace FormUp.Application.Features.Foods.PromoteFoodGalleryImage;

/// <summary>
/// Makes a gallery image the food's main image. The previous main image takes the promoted entry's
/// gallery position; with no main image the entry simply moves out of the gallery.
/// </summary>
/// <param name="mongo">MongoDB context.</param>
[RemovedCoachRole(AppRoles.Nutritionist, RemovedCoachRoleMode.Refuse)]
public class PromoteFoodGalleryImageEndpoint(IMongoContext mongo) : Endpoint<PromoteFoodGalleryImageRequest>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/foods/{FoodId}/gallery/promote");
        Roles(AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Promote a food gallery image to main";
            s.Description = "Swaps the given gallery image with the current main image (same gallery position). "
                            + "With no main image the entry is removed from the gallery and set as main. "
                            + "Promoting the current main image is a no-op. Only the food's creator can promote.";
            s.Responses[StatusCodes.Status204NoContent] = "Image promoted (or already main)";
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest,"FOOD_NOT_OWNED or FOOD_GALLERY_IMAGE_NOT_FOUND");
            s.Response(StatusCodes.Status404NotFound, "Food not found or soft-deleted");
            s.Response(StatusCodes.Status409Conflict, "FOOD_IMAGES_CONFLICT — the images changed concurrently");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(PromoteFoodGalleryImageRequest req, CancellationToken ct)
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

        var activeFood = Builders<Food>.Filter.Eq(f => f.ExternalId, req.FoodId)
            & Builders<Food>.Filter.Eq(f => f.IsDeleted, false);

        using var cursor = await mongo.Foods.FindAsync(activeFood, cancellationToken: ct);
        var food = await cursor.FirstOrDefaultAsync(ct);

        if (food is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (food.NutritionistId != nutritionistId)
        {
            this.ThrowErrorWithCode(ErrorCodes.FoodNotOwned, "You can only change images on your own custom foods.");
            return;
        }

        if (req.ImageUrl == food.ImageUrl)
        {
            await Send.NoContentAsync(ct);
            return;
        }

        var index = food.GalleryImageUrls.IndexOf(req.ImageUrl);

        if (index < 0)
        {
            this.ThrowErrorWithCode(ErrorCodes.FoodGalleryImageNotFound, "The image is not in this food's gallery.");
            return;
        }

        var newGallery = new List<string>(food.GalleryImageUrls);

        if (string.IsNullOrEmpty(food.ImageUrl))
        {
            newGallery.RemoveAt(index);
        }
        else
        {
            newGallery[index] = food.ImageUrl;
        }

        // Compare-and-swap on the loaded main image and gallery (Food has no Version) so a concurrent
        // picture change is not clobbered.
        var result = await mongo.Foods.UpdateOneAsync(
            activeFood
                & Builders<Food>.Filter.Eq(f => f.NutritionistId, nutritionistId)
                & Builders<Food>.Filter.Eq(f => f.ImageUrl, food.ImageUrl)
                & Builders<Food>.Filter.Eq(f => f.GalleryImageUrls, food.GalleryImageUrls),
            Builders<Food>.Update
                .Set(f => f.ImageUrl, req.ImageUrl)
                .Set(f => f.GalleryImageUrls, newGallery)
                .Set(f => f.DateUpdated, DateTime.UtcNow),
            cancellationToken: ct);

        if (result.ModifiedCount == 0)
        {
            await this.SendProblemAsync(409, ErrorCodes.FoodImagesConflict,
                "The food images were changed by another request. Reload and try again.", ct);
            return;
        }

        await Send.NoContentAsync(ct);
    }
}
