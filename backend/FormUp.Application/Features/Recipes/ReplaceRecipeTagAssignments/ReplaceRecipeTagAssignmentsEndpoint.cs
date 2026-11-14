using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Enums;
using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.Recipes.Shared;
using FormUp.Application.Infrastructure.Data.MongoDb;
using MongoDB.Driver;

namespace FormUp.Application.Features.Recipes.ReplaceRecipeTagAssignments;

/// <summary>
/// Replaces the full set of tags the calling nutritionist has assigned to one recipe. The recipe
/// may be the caller's own, a system recipe, or another coach's Public recipe — tagging is the
/// tagging coach's own private relationship metadata, never a claim on the recipe.
/// </summary>
/// <param name="mongo">MongoDB context.</param>
[RemovedCoachRole(AppRoles.Nutritionist, RemovedCoachRoleMode.Refuse)]
public class ReplaceRecipeTagAssignmentsEndpoint(IMongoContext mongo)
    : Endpoint<ReplaceRecipeTagAssignmentsRequest, ReplaceRecipeTagAssignmentsResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Put("/trainer/recipes/{RecipeId}/tags");
        Roles(AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Replace a recipe's tag assignments";
            s.Description = "Replaces the full set of the caller's own tags assigned to a recipe. "
                + "The recipe may be the caller's own, a system recipe, or another coach's Public recipe.";
            s.Responses[StatusCodes.Status200OK] = "The resulting tag set";
            s.Responses[StatusCodes.Status400BadRequest] = "Invalid request body";
            s.Responses[StatusCodes.Status401Unauthorized] = "Missing or invalid credentials";
            s.Responses[StatusCodes.Status404NotFound] =
                "Recipe not found or not readable by the caller, or one or more tags were not found";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(ReplaceRecipeTagAssignmentsRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);

        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var ownerUserId = Guid.Parse(userId);

        if (await this.RespondIfCoachFeatureNotEntitledAsync(
                ownerUserId, entitlements => entitlements.CanUseRecipes,
                "The current subscription does not include recipes.", ct))
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

        // The recipe must be readable by the caller — their own, a system recipe, or another
        // coach's Public recipe. Another coach's Private recipe 404s like every other recipe read.
        var recipeFilter = RecipeVisibilityFilter.BuildOwnOrPublic(ownerUserId)
            & Builders<Recipe>.Filter.Eq(r => r.ExternalId, req.RecipeId);

        var recipeExists = await mongo.Recipes.Find(recipeFilter).AnyAsync(ct);

        if (!recipeExists)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var update = Builders<RecipeTagAssignment>.Update
            .Set(a => a.TagIds, distinctTagIds)
            .Set(a => a.DateUpdated, DateTime.UtcNow)
            .SetOnInsert(a => a.DateCreated, DateTime.UtcNow);

        await mongo.RecipeTagAssignments.UpdateOneAsync(
            a => a.OwnerUserId == ownerUserId && a.RecipeExternalId == req.RecipeId,
            update,
            new UpdateOptions { IsUpsert = true },
            ct);

        await Send.OkAsync(new ReplaceRecipeTagAssignmentsResponse
        {
            RecipeId = req.RecipeId,
            Tags = ownedTags
                .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .Select(FoodTagDto.FromDocument)
                .ToList(),
        }, ct);
    }
}
