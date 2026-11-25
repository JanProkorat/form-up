using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Features.Foods.Shared;
using FormUp.Application.Infrastructure.Data.MongoDb;
using MongoDB.Driver;

namespace FormUp.Application.Features.Foods.HideFood;

/// <summary>
/// Hides a visible food from the caller's own library. The food stays visible to everyone else.
/// </summary>
/// <param name="mongo">MongoDB context.</param>
[RemovedCoachRole(AppRoles.Nutritionist, RemovedCoachRoleMode.Refuse)]
internal sealed class HideFoodEndpoint(IMongoContext mongo) : Endpoint<HideFoodRequest>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Put("/foods/{FoodId}/hide");
        Roles(AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Hide food";
            s.Description = "Hides another coach's or the system's public food from the caller's library. "
                + "Idempotent. A coach cannot hide a food they own.";
            s.Response(StatusCodes.Status204NoContent, "Food hidden");
            s.Response(StatusCodes.Status400BadRequest, "The caller owns the food (FOOD_HIDE_OWN_NOT_ALLOWED)");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or invalid credentials");
            s.Response(StatusCodes.Status403Forbidden, "The coach role was removed");
            s.Response(StatusCodes.Status404NotFound, "Food not found, deleted, or another coach's private food");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(HideFoodRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);

        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var nutritionistId = Guid.Parse(userId);

        var filter = Builders<Food>.Filter.Eq(f => f.ExternalId, req.FoodId)
            & FoodVisibilityFilter.BuildOwnOrPublic(nutritionistId);

        using var cursor = await mongo.Foods.FindAsync(filter, cancellationToken: ct);
        var food = await cursor.FirstOrDefaultAsync(ct);

        if (food is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (food.NutritionistId == nutritionistId)
        {
            this.ThrowErrorWithCode(ErrorCodes.FoodHideOwnNotAllowed, "You cannot hide a food you own. Delete it instead.");
            return;
        }

        await mongo.Foods.UpdateOneAsync(
            Builders<Food>.Filter.Eq(f => f.ExternalId, req.FoodId),
            Builders<Food>.Update.AddToSet(f => f.HiddenByNutritionistIds, nutritionistId),
            cancellationToken: ct);

        await Send.NoContentAsync(ct);
    }
}
