using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Enums;
using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Infrastructure.Data.MongoDb;
using MongoDB.Driver;

namespace FormUp.Application.Features.Foods.DeleteFoodImage;

/// <summary>
/// Clears the main image URL on a food document. The gallery is untouched.
/// Only the nutritionist who created the food can remove its image.
/// The underlying blob is never deleted; orphan cleanup is a separate concern.
/// </summary>
/// <param name="mongo">MongoDB context.</param>
[RemovedCoachRole(AppRoles.Nutritionist, RemovedCoachRoleMode.Refuse)]
public class DeleteFoodImageEndpoint(IMongoContext mongo) : Endpoint<DeleteFoodImageRequest>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Delete("/foods/{FoodId}/image");
        Roles(AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Remove food image";
            s.Description = "Clears the main ImageUrl on the food document. The gallery is left "
                            + "untouched, and the underlying blob is not deleted. "
                            + "Idempotent — returns 204 even if the image is already unset. "
                            + "Only the nutritionist who created the food can remove its image.";
            s.Responses[StatusCodes.Status204NoContent] = "Image cleared (or was already unset)";
            s.Responses[StatusCodes.Status400BadRequest] = "FOOD_NOT_OWNED — the caller does not own this food";
            s.Responses[StatusCodes.Status404NotFound] = "Food not found or soft-deleted";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(DeleteFoodImageRequest req, CancellationToken ct)
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
            this.ThrowErrorWithCode(ErrorCodes.FoodNotOwned, "You can only remove the image on your own custom foods.");
            return;
        }

        // Guard against a concurrent soft-delete between the FindAsync above and this
        // write, same race guard as ConfirmFoodImageEndpoint.
        var update = Builders<Food>.Update
            .Set(f => f.ImageUrl, null)
            .Set(f => f.DateUpdated, DateTime.UtcNow);

        await mongo.Foods.UpdateOneAsync(
            Builders<Food>.Filter.Eq(f => f.ExternalId, req.FoodId)
                & Builders<Food>.Filter.Eq(f => f.IsDeleted, false),
            update,
            cancellationToken: ct);

        await Send.NoContentAsync(ct);
    }
}
