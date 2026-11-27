namespace FormUp.Application.Features.Recipes.CopyRecipe;

/// <summary>
/// Request for copying a recipe into the caller's own library.
/// </summary>
public class CopyRecipeRequest
{
    /// <summary>Route parameter — public id of the recipe to copy.</summary>
    public Guid RecipeId { get; set; }
}
