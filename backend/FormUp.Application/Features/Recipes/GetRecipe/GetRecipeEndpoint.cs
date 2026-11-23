using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.Recipes.Shared;
using FormUp.Application.Infrastructure.Data.MongoDb;
using MongoDB.Driver;

namespace FormUp.Application.Features.Recipes.GetRecipe;

/// <summary>
/// Retrieves a single recipe by its public identifier.
/// Nutritionists see their own recipes at any visibility and other nutritionists' public recipes.
/// </summary>
/// <param name="mongo">MongoDB context.</param>
public class GetRecipeEndpoint(IMongoContext mongo)
    : Endpoint<GetRecipeRequest, GetRecipeResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/recipes/{RecipeId}");
        Roles(AppRoles.Nutritionist, AppRoles.Admin);
        Summary(s =>
        {
            s.Summary = "Get recipe";
            s.Description = "Returns full detail of a recipe. "
                + "Nutritionists can read their own recipes (any visibility) and public recipes owned by others; "
                + "other nutritionists' private recipes return 404.";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(GetRecipeRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);

        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var scope = LibraryCallerScope.From(User, Guid.Parse(userId));
        var nutritionistId = scope.RecipeOwnerId;

        var filter = Builders<Recipe>.Filter.Eq(r => r.ExternalId, req.RecipeId)
            & RecipeVisibilityFilter.BuildOwnOrPublic(nutritionistId)
            & scope.RecipeFilter();

        using var cursor = await mongo.Recipes.FindAsync(filter, cancellationToken: ct);
        var recipe = await cursor.FirstOrDefaultAsync(ct);

        if (recipe is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // Resolve localized food names
        var foodIds = recipe.Foods.Select(f => f.FoodExternalId).Distinct().ToList();
        var foodFilter = Builders<Food>.Filter.In(f => f.ExternalId, foodIds);
        using var foodCursor = await mongo.Foods.FindAsync(foodFilter, cancellationToken: ct);
        var foods = await foodCursor.ToListAsync(ct);
        var foodLookup = foods.ToDictionary(f => f.ExternalId) as IReadOnlyDictionary<Guid, Food>;

        var language = HttpContext.Request.Headers.AcceptLanguage.FirstOrDefault()?.Split(',').FirstOrDefault()?.Trim().Split('-').FirstOrDefault();

        var response = GetRecipeResponse.FromDocument(recipe, foodLookup, language, nutritionistId);

        var tagsByRecipeId = await FoodTagLookup.GetTagsByRecipeIdAsync(mongo, nutritionistId, [recipe.ExternalId], ct);
        response.Tags = tagsByRecipeId.GetValueOrDefault(recipe.ExternalId, []);

        await Send.OkAsync(response, ct);
    }
}
