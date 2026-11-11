using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Services;

namespace FormUp.Application.Features.Recipes.Shared;

/// <summary>
/// Full recipe detail returned by get and create/update endpoints.
/// </summary>
public class GetRecipeResponse
{
    /// <summary>
    /// Public identifier of the recipe.
    /// </summary>
    public Guid RecipeId { get; set; }

    /// <summary>
    /// Name of the recipe.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Optional description or preparation instructions.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Estimated preparation/cooking time in minutes.
    /// </summary>
    public int? PrepTimeMinutes { get; set; }

    /// <summary>
    /// Ordered preparation steps.
    /// </summary>
    public List<RecipeStepDto>? Steps { get; set; }

    /// <summary>
    /// Optional tip or note.
    /// </summary>
    public string? Note { get; set; }

    /// <summary>
    /// List of food items with denormalized nutrient snapshots.
    /// </summary>
    public List<MealFood> Foods { get; set; } = [];

    /// <summary>
    /// Computed total macronutrients for the entire recipe.
    /// </summary>
    public NutrientTotals TotalNutrients { get; set; } = new();

    /// <summary>
    /// Visibility of the recipe (Public = visible to all nutritionists, Private = visible only to its creator).
    /// </summary>
    public RecipeVisibility Visibility { get; set; }

    /// <summary>
    /// URL of the recipe's main image in blob storage, or null if no image has been uploaded.
    /// </summary>
    public string? ImageUrl { get; set; }

    /// <summary>
    /// URLs of gallery images (up to 6 entries). Each entry points to a blob at
    /// <c>recipes/{recipeId}/gallery-{n}.{ext}</c>.
    /// </summary>
    public List<string> GalleryImageUrls { get; set; } = [];

    /// <summary>
    /// True when the authenticated caller is the nutritionist who created this recipe.
    /// Clients of the API can use this flag to decide whether to show edit/delete affordances.
    /// </summary>
    public bool IsOwnedByCurrentUser { get; set; }

    /// <summary>
    /// When the recipe was created.
    /// </summary>
    public DateTime DateCreated { get; set; }

    /// <summary>
    /// When the recipe was last updated.
    /// </summary>
    public DateTime? DateUpdated { get; set; }

    /// <summary>
    /// Optimistic concurrency version. Clients must echo this value back on update.
    /// </summary>
    public int Version { get; set; }

    /// <summary>
    /// Number of servings the recipe yields.
    /// </summary>
    public int Servings { get; set; } = 1;

    /// <summary>
    /// Preparation difficulty, or null when not set.
    /// </summary>
    public RecipeDifficulty? Difficulty { get; set; }

    /// <summary>
    /// Cooking time in minutes, separate from <see cref="PrepTimeMinutes"/>.
    /// </summary>
    public int? CookTimeMinutes { get; set; }

    /// <summary>
    /// Meal types the recipe is suited for.
    /// </summary>
    public List<RecipeMealType> MealTypes { get; set; } = [];

    /// <summary>
    /// Dietary preferences the recipe satisfies.
    /// </summary>
    public List<DietaryPreference> DietaryPreferences { get; set; } = [];

    /// <summary>
    /// Allergens derived on read from the ingredient foods; never stored on the recipe.
    /// </summary>
    public List<Allergen> Allergens { get; set; } = [];

    /// <summary>
    /// True when the recipe belongs to the platform catalog rather than a coach.
    /// </summary>
    public bool IsSystem { get; set; }

    /// <summary>
    /// The caller's own tags on this recipe, ordered by name. Never another coach's tags.
    /// </summary>
    public List<FoodTagDto> Tags { get; set; } = [];

    private static List<RecipeStepDto>? ToStepDtos(List<RecipeStep>? steps) =>
        steps?.Select(step => new RecipeStepDto { Title = step.Title, Description = step.Description, Minutes = step.Minutes }).ToList();

    /// <summary>
    /// Maps a <see cref="Recipe"/> document to a <see cref="GetRecipeResponse"/>.
    /// </summary>
    /// <param name="recipe">The source recipe document.</param>
    /// <param name="currentUserId">Id of the authenticated user; used to resolve <see cref="IsOwnedByCurrentUser"/>.</param>
    /// <param name="allergens">Allergens derived from the ingredient foods by the caller.</param>
    /// <returns>A full recipe response.</returns>
    public static GetRecipeResponse FromDocument(
        Recipe recipe, Guid? currentUserId = null, List<Allergen>? allergens = null) => new()
    {
        RecipeId = recipe.ExternalId,
        Name = recipe.Name,
        Description = recipe.Description,
        PrepTimeMinutes = recipe.PrepTimeMinutes,
        Steps = ToStepDtos(recipe.Steps),
        Note = recipe.Note,
        Foods = recipe.Foods,
        TotalNutrients = recipe.TotalNutrients,
        Visibility = recipe.Visibility,
        ImageUrl = recipe.ImageUrl,
        GalleryImageUrls = recipe.GalleryImageUrls,
        IsOwnedByCurrentUser = currentUserId.HasValue && recipe.NutritionistId == currentUserId.Value,
        DateCreated = recipe.DateCreated,
        DateUpdated = recipe.DateUpdated,
        Version = recipe.Version,
        Servings = recipe.Servings,
        Difficulty = recipe.Difficulty,
        CookTimeMinutes = recipe.CookTimeMinutes,
        MealTypes = FoodEnumListMapping.ParseStoredNames<RecipeMealType>(recipe.MealTypes ?? []),
        DietaryPreferences = FoodEnumListMapping.ParseStoredNames<DietaryPreference>(recipe.DietaryPreferences),
        Allergens = allergens ?? [],
        IsSystem = recipe.NutritionistId == SystemUsers.AdminId
    };

    /// <summary>
    /// Maps a recipe, resolving food names using localized names when available. Allergens are
    /// derived from the foods in <paramref name="foodLookup"/>.
    /// </summary>
    public static GetRecipeResponse FromDocument(
        Recipe recipe,
        IReadOnlyDictionary<Guid, Food> foodLookup,
        string? language = null,
        Guid? currentUserId = null) => new()
    {
        RecipeId = recipe.ExternalId,
        Name = recipe.Name,
        Description = recipe.Description,
        PrepTimeMinutes = recipe.PrepTimeMinutes,
        Steps = ToStepDtos(recipe.Steps),
        Note = recipe.Note,
        Servings = recipe.Servings,
        Difficulty = recipe.Difficulty,
        CookTimeMinutes = recipe.CookTimeMinutes,
        MealTypes = FoodEnumListMapping.ParseStoredNames<RecipeMealType>(recipe.MealTypes ?? []),
        DietaryPreferences = FoodEnumListMapping.ParseStoredNames<DietaryPreference>(recipe.DietaryPreferences),
        Allergens = RecipeContent.DeriveAllergens(recipe, foodLookup),
        IsSystem = recipe.NutritionistId == SystemUsers.AdminId,
        Foods = recipe.Foods.Select(f =>
        {
            var resolvedName = foodLookup.TryGetValue(f.FoodExternalId, out var food)
                ? (food.LocalizedNames?.Resolve(language) ?? food.Name)
                : f.FoodName;
            return new MealFood
            {
                FoodExternalId = f.FoodExternalId,
                FoodName = resolvedName,
                NutrientValuePer100Grams = f.NutrientValuePer100Grams,
                AmountGrams = f.AmountGrams,
                Note = f.Note
            };
        }).ToList(),
        TotalNutrients = recipe.TotalNutrients,
        Visibility = recipe.Visibility,
        ImageUrl = recipe.ImageUrl,
        GalleryImageUrls = recipe.GalleryImageUrls,
        IsOwnedByCurrentUser = currentUserId.HasValue && recipe.NutritionistId == currentUserId.Value,
        DateCreated = recipe.DateCreated,
        DateUpdated = recipe.DateUpdated,
        Version = recipe.Version
    };
}
