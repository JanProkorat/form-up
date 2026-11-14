using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Enums;
using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Infrastructure.Data.MongoDb;
using MongoDB.Driver;

namespace FormUp.Application.Features.Recipes.DeleteRecipe;

/// <summary>
/// Deletes a recipe owned by the current nutritionist.
/// </summary>
/// <param name="mongo">MongoDB context.</param>
[RemovedCoachRole(AppRoles.Nutritionist, RemovedCoachRoleMode.Refuse)]
public class DeleteRecipeEndpoint(IMongoContext mongo)
    : Endpoint<DeleteRecipeRequest, object>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Delete("/recipes/{RecipeId}");
        Roles(AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Delete recipe";
            s.Description = "Permanently deletes a recipe owned by the current nutritionist.";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(DeleteRecipeRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);

        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var nutritionistId = Guid.Parse(userId);

        if (await this.RespondIfCoachFeatureNotEntitledAsync(
                nutritionistId, entitlements => entitlements.CanUseRecipes,
                "The current subscription does not include recipes.", ct))
        {
            return;
        }

        var filter = Builders<Recipe>.Filter.Eq(r => r.ExternalId, req.RecipeId)
            & Builders<Recipe>.Filter.Eq(r => r.NutritionistId, nutritionistId);

        var result = await mongo.Recipes.DeleteOneAsync(filter, ct);

        if (result.DeletedCount == 0)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // The recipe is gone for everyone, so every coach's tag assignment for it goes too.
        await mongo.RecipeTagAssignments.DeleteManyAsync(a => a.RecipeExternalId == req.RecipeId, ct);

        await Send.NoContentAsync(ct);
    }
}
