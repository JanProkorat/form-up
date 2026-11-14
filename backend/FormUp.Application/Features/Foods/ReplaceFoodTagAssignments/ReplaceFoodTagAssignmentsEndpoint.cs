using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Enums;
using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.Foods.Shared;
using FormUp.Application.Infrastructure.Data.MongoDb;
using MongoDB.Driver;

namespace FormUp.Application.Features.Foods.ReplaceFoodTagAssignments;

/// <summary>
/// Replaces the full set of tags the calling nutritionist has assigned to one food. The food
/// itself may be the caller's own, a platform system food, or another coach's Public food —
/// tagging is the tagging coach's own private relationship metadata, never a claim on the food.
/// </summary>
/// <param name="mongo">MongoDB context.</param>
[RemovedCoachRole(AppRoles.Nutritionist, RemovedCoachRoleMode.Refuse)]
public class ReplaceFoodTagAssignmentsEndpoint(IMongoContext mongo)
    : Endpoint<ReplaceFoodTagAssignmentsRequest, ReplaceFoodTagAssignmentsResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Put("/trainer/foods/{FoodId}/tags");
        Roles(AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Replace a food's tag assignments";
            s.Description = "Replaces the full set of the caller's own tags assigned to a food. "
                + "The food may be the caller's own, a system food, or another coach's Public food.";
            s.Responses[StatusCodes.Status200OK] = "The resulting tag set";
            s.Responses[StatusCodes.Status400BadRequest] = "Invalid request body";
            s.Responses[StatusCodes.Status401Unauthorized] = "Missing or invalid credentials";
            s.Responses[StatusCodes.Status404NotFound] =
                "Food not found or not visible to the caller, or one or more tags were not found";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(ReplaceFoodTagAssignmentsRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);

        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var ownerUserId = Guid.Parse(userId);

        if (await this.RespondIfCoachFeatureNotEntitledAsync(
                ownerUserId, entitlements => entitlements.CanUseIngredients,
                "The current subscription does not include ingredients.", ct))
        {
            return;
        }
        var distinctTagIds = req.TagIds.Distinct().ToList();

        var ownedTags = await FoodTagLookup.FindOwnedTagsAsync(mongo, ownerUserId, distinctTagIds, ct);

        if (ownedTags.Count != distinctTagIds.Count)
        {
            await this.SendProblemAsync(
                StatusCodes.Status404NotFound,
                ErrorCodes.FoodTagNotFound,
                "One or more tags were not found.",
                ct);
            return;
        }

        // The food must be visible to the caller — their own, a system food, or another coach's
        // Public food. A private food belonging to a different coach 404s here just like every
        // other own-or-public read (rules/api-design.md#authorization).
        var foodFilter = FoodVisibilityFilter.BuildOwnOrPublic(ownerUserId)
            & Builders<Food>.Filter.Eq(f => f.ExternalId, req.FoodId);

        var food = await mongo.Foods.Find(foodFilter).FirstOrDefaultAsync(ct);

        if (food is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var update = Builders<FoodTagAssignment>.Update
            .Set(a => a.TagIds, distinctTagIds)
            .Set(a => a.DateUpdated, DateTime.UtcNow)
            .SetOnInsert(a => a.DateCreated, DateTime.UtcNow);

        await mongo.FoodTagAssignments.UpdateOneAsync(
            a => a.OwnerUserId == ownerUserId && a.FoodExternalId == req.FoodId,
            update,
            new UpdateOptions { IsUpsert = true },
            ct);

        await Send.OkAsync(new ReplaceFoodTagAssignmentsResponse
        {
            FoodId = req.FoodId,
            Tags = ownedTags
                .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .Select(FoodTagDto.FromDocument)
                .ToList(),
        }, ct);
    }
}
