using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Infrastructure.Data.MongoDb;
using MongoDB.Driver;

namespace FormUp.Application.Features.Foods.RemoveFoodGalleryImage;

/// <summary>
/// Removes one gallery image URL from a food. The blob is not deleted and the main image is untouched.
/// </summary>
/// <param name="mongo">MongoDB context.</param>
[RemovedCoachRole(AppRoles.Nutritionist, RemovedCoachRoleMode.Refuse)]
public class RemoveFoodGalleryImageEndpoint(IMongoContext mongo) : Endpoint<RemoveFoodGalleryImageRequest>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Delete("/foods/{FoodId}/gallery");
        Roles(AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Remove a food gallery image";
            s.Description = "Pulls the exact imageUrl (query parameter) from the food's gallery, keeping the order "
                            + "of the remaining entries. Idempotent: returns 204 even if the URL is not in the gallery. "
                            + "The blob is not deleted. Only the food's creator can remove its gallery images.";
            s.Responses[StatusCodes.Status204NoContent] = "Image removed (or was not in the gallery)";
            s.Responses[StatusCodes.Status400BadRequest] = "FOOD_NOT_OWNED, or invalid imageUrl";
            s.Responses[StatusCodes.Status404NotFound] = "Food not found or soft-deleted";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(RemoveFoodGalleryImageRequest req, CancellationToken ct)
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
            this.ThrowErrorWithCode(ErrorCodes.FoodNotOwned, "You can only remove images on your own custom foods.");
            return;
        }

        await mongo.Foods.UpdateOneAsync(
            activeFood & Builders<Food>.Filter.Eq(f => f.NutritionistId, nutritionistId),
            Builders<Food>.Update
                .Pull(f => f.GalleryImageUrls, req.ImageUrl)
                .Set(f => f.DateUpdated, DateTime.UtcNow),
            cancellationToken: ct);

        await Send.NoContentAsync(ct);
    }
}
