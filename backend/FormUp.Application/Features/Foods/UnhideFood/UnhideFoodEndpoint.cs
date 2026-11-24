using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.Foods.Shared;
using FormUp.Application.Infrastructure.Data.MongoDb;
using MongoDB.Driver;

namespace FormUp.Application.Features.Foods.UnhideFood;

/// <summary>
/// Restores a food the caller hid to their own library.
/// </summary>
/// <param name="mongo">MongoDB context.</param>
[RemovedCoachRole(AppRoles.Nutritionist, RemovedCoachRoleMode.Refuse)]
internal sealed class UnhideFoodEndpoint(IMongoContext mongo) : Endpoint<UnhideFoodRequest>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Put("/foods/{FoodId}/unhide");
        Roles(AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Unhide food";
            s.Description = "Restores a food the caller hid. Idempotent: succeeds when the food was not hidden.";
            s.Response(StatusCodes.Status204NoContent, "Food visible again");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or invalid credentials");
            s.Response(StatusCodes.Status403Forbidden, "The coach role was removed");
            s.Response(StatusCodes.Status404NotFound, "Food not found, deleted, or another coach's private food");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(UnhideFoodRequest req, CancellationToken ct)
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

        await mongo.Foods.UpdateOneAsync(
            Builders<Food>.Filter.Eq(f => f.ExternalId, req.FoodId),
            Builders<Food>.Update.Pull(f => f.HiddenByNutritionistIds, nutritionistId),
            cancellationToken: ct);

        await Send.NoContentAsync(ct);
    }
}
