using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Enums;
using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Infrastructure.Data.MongoDb;
using MongoDB.Driver;

namespace FormUp.Application.Features.Foods.DeleteFood;

/// <summary>
/// Soft-deletes a custom food item. Only the owning nutritionist can delete.
/// </summary>
/// <param name="mongo">MongoDB context.</param>
[RemovedCoachRole(AppRoles.Nutritionist, RemovedCoachRoleMode.Refuse)]
public class DeleteFoodEndpoint(IMongoContext mongo) : Endpoint<DeleteFoodRequest>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Delete("/foods/{FoodId}");
        Roles(AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Delete custom food";
            s.Description = "Soft-deletes a custom food item. Only the nutritionist who created it can delete.";
            s.Responses[StatusCodes.Status204NoContent] = "Food deleted";
            s.Responses[StatusCodes.Status400BadRequest] = "The food belongs to another nutritionist";
            s.Responses[StatusCodes.Status401Unauthorized] = "Missing or invalid credentials";
            s.Responses[StatusCodes.Status404NotFound] = "Food not found, or already soft-deleted";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(DeleteFoodRequest req, CancellationToken ct)
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
            this.ThrowErrorWithCode(ErrorCodes.FoodNotOwned, "You can only delete your own custom foods.");
            return;
        }

        var update = Builders<Food>.Update
            .Set(f => f.IsDeleted, true)
            .Set(f => f.DateUpdated, DateTime.UtcNow);

        await mongo.Foods.UpdateOneAsync(
            f => f.ExternalId == req.FoodId,
            update,
            cancellationToken: ct);

        await Send.NoContentAsync(ct);
    }
}
