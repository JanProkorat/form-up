namespace FormUp.Application.Features.Recipes.HideRecipe;

/// <summary>
/// Request for hiding a recipe from the caller's library.
/// </summary>
public class HideRecipeRequest
{
    /// <summary>Route parameter — public id of the recipe to hide.</summary>
    public Guid RecipeId { get; set; }
}
