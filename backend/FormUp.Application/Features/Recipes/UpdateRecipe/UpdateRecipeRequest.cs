using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.Recipes.Shared;

namespace FormUp.Application.Features.Recipes.UpdateRecipe;

/// <summary>
/// Request model for updating an existing recipe.
/// </summary>
public class UpdateRecipeRequest
{
    /// <summary>
    /// Public identifier of the recipe to update.
    /// </summary>
    public Guid RecipeId { get; set; }

    /// <summary>
    /// Optimistic concurrency version. Must match the current document version.
    /// </summary>
    public int Version { get; set; }

    /// <summary>
    /// Updated name of the recipe.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Updated description or preparation instructions.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Estimated preparation time in minutes.
    /// </summary>
    public int? PrepTimeMinutes { get; set; }

    /// <summary>
    /// Cooking time in minutes.
    /// </summary>
    public int? CookTimeMinutes { get; set; }

    /// <summary>
    /// Number of servings the recipe yields. Must be greater than zero.
    /// </summary>
    public int Servings { get; set; } = 1;

    /// <summary>
    /// Preparation difficulty; null clears it.
    /// </summary>
    public RecipeDifficulty? Difficulty { get; set; }

    /// <summary>
    /// Meal types the recipe is suited for. At least one is required.
    /// </summary>
    public List<RecipeMealType> MealTypes { get; set; } = [];

    /// <summary>
    /// Dietary preferences the recipe satisfies.
    /// </summary>
    public List<DietaryPreference> DietaryPreferences { get; set; } = [];

    /// <summary>
    /// Ordered preparation steps (list order is step order). Steps with neither title nor description are dropped; at most 50,
    /// title at most 120 and description at most 2000 characters, optional minutes 1 to 180.
    /// </summary>
    public List<RecipeStepDto>? Steps { get; set; }

    /// <summary>Optional tip or note.</summary>
    public string? Note { get; set; }

    /// <summary>
    /// Updated list of food items in the recipe.
    /// </summary>
    public List<RecipeFoodDto> Foods { get; set; } = [];

    /// <summary>
    /// Updated visibility. When omitted, the recipe's existing visibility is preserved.
    /// Only the recipe's creator can change this value.
    /// </summary>
    public RecipeVisibility? Visibility { get; set; }
}
