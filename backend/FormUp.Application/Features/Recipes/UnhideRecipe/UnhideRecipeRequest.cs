namespace FormUp.Application.Features.Recipes.UnhideRecipe;

/// <summary>
/// Request for restoring a hidden recipe to the caller's library.
/// </summary>
public class UnhideRecipeRequest
{
    /// <summary>Route parameter — public id of the recipe to unhide.</summary>
    public Guid RecipeId { get; set; }
}
