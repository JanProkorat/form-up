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

namespace FormUp.Application.Features.Recipes.CreateRecipe;

/// <summary>
/// Creates a new recipe with denormalized food data and computed nutrient totals.
/// </summary>
/// <param name="mongo">MongoDB context.</param>
[RemovedCoachRole(AppRoles.Nutritionist, RemovedCoachRoleMode.Refuse)]
public class CreateRecipeEndpoint(IMongoContext mongo)
    : Endpoint<CreateRecipeRequest, GetRecipeResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/recipes");
        Roles(AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Create recipe";
            s.Description = "Creates a new recipe with food items and calculated nutrient totals.";
            s.Response<GetRecipeResponse>(StatusCodes.Status201Created, "Recipe created");
            s.Responses[StatusCodes.Status400BadRequest] = "Invalid request body or unavailable ingredient";
            s.Responses[StatusCodes.Status401Unauthorized] = "Missing or invalid credentials";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(CreateRecipeRequest req, CancellationToken ct)
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

        var foodLookup = await RecipeContent.LoadUsableFoodsAsync(
            mongo, req.Foods.Select(f => f.FoodExternalId), nutritionistId, ct);

        var mealFoods = new List<MealFood>();

        foreach (var item in req.Foods)
        {
            if (!foodLookup.TryGetValue(item.FoodExternalId, out var food))
            {
                this.ThrowErrorWithCode(ErrorCodes.RecipeFoodNotAvailable,
                    $"Food with ID '{item.FoodExternalId}' is not available.");
                return;
            }

            mealFoods.Add(RecipeContent.ToMealFood(food, item));
        }

        var recipe = new Recipe
        {
            ExternalId = Guid.NewGuid(),
            NutritionistId = nutritionistId,
            Name = req.Name,
            Description = req.Description,
            PrepTimeMinutes = req.PrepTimeMinutes,
            CookTimeMinutes = req.CookTimeMinutes,
            Servings = req.Servings,
            Difficulty = req.Difficulty,
            MealTypes = FoodEnumListMapping.ToStoredNames(req.MealTypes.Distinct()),
            DietaryPreferences = FoodEnumListMapping.ToStoredNames(req.DietaryPreferences.Distinct()),
            Steps = RecipeContent.NormalizeSteps(req.Steps),
            Note = req.Note,
            Foods = mealFoods,
            TotalNutrients = RecipeContent.CalculateTotals(mealFoods),
            Visibility = req.Visibility,
            DateCreated = DateTime.UtcNow
        };

        await mongo.Recipes.InsertOneAsync(recipe, cancellationToken: ct);

        await HttpContext.Response.SendAsync(
            GetRecipeResponse.FromDocument(
                recipe, nutritionistId, RecipeContent.DeriveAllergens(recipe, foodLookup)),
            201,
            cancellation: ct);
    }
}
