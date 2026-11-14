using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Enums;
using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Infrastructure.Data.MongoDb;
using MongoDB.Driver;

namespace FormUp.Application.Features.Foods.DeleteFoodTag;

/// <summary>
/// Deletes a food tag and pulls it out of every assignment that references it. Only the owning
/// nutritionist may delete.
/// </summary>
/// <param name="mongo">MongoDB context.</param>
[RemovedCoachRole(AppRoles.Nutritionist, RemovedCoachRoleMode.Refuse)]
public class DeleteFoodTagEndpoint(IMongoContext mongo) : Endpoint<DeleteFoodTagRequest>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Delete("/trainer/food-tags/{TagId}");
        Roles(AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Delete a food tag";
            s.Description = "Permanently deletes a food tag and removes it from every assignment "
                + "that references it. Only the owning nutritionist may delete.";
            s.Responses[StatusCodes.Status204NoContent] = "Tag deleted";
            s.Responses[StatusCodes.Status401Unauthorized] = "Missing or invalid credentials";
            s.Responses[StatusCodes.Status404NotFound] = "Tag not found, or owned by a different nutritionist";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(DeleteFoodTagRequest req, CancellationToken ct)
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

        // Single owner-filtered query — a tag owned by a different nutritionist is
        // indistinguishable from one that does not exist at all.
        var tag = await mongo.FoodTags
            .Find(t => t.ExternalId == req.TagId && t.OwnerUserId == ownerUserId)
            .FirstOrDefaultAsync(ct);

        if (tag is null)
        {
            await this.SendProblemAsync(
                StatusCodes.Status404NotFound, ErrorCodes.FoodTagNotFound, "Food tag not found.", ct);
            return;
        }

        await mongo.FoodTags.DeleteOneAsync(
            t => t.ExternalId == req.TagId && t.OwnerUserId == ownerUserId, ct);

        // Pull the deleted tag id out of every assignment that still references it — a dangling
        // id from a concurrent delete racing this one is harmless (reads join to existing tags
        // only), so this is a best-effort cleanup, not a guarded transaction.
        await mongo.FoodTagAssignments.UpdateManyAsync(
            a => a.OwnerUserId == ownerUserId && a.TagIds.Contains(req.TagId),
            Builders<FoodTagAssignment>.Update.Pull(a => a.TagIds, req.TagId),
            cancellationToken: ct);

        // Tags are shared with recipes, so recipe assignments are cleaned the same way.
        await mongo.RecipeTagAssignments.UpdateManyAsync(
            a => a.OwnerUserId == ownerUserId && a.TagIds.Contains(req.TagId),
            Builders<RecipeTagAssignment>.Update.Pull(a => a.TagIds, req.TagId),
            cancellationToken: ct);

        await Send.NoContentAsync(ct);
    }
}
