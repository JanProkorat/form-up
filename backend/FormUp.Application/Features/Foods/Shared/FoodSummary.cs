using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Services;

namespace FormUp.Application.Features.Foods.Shared;

/// <summary>
/// Common response DTO for food items used across multiple endpoints.
/// </summary>
public class FoodSummary
{
    /// <summary>
    /// Public-facing food identifier.
    /// </summary>
    public Guid FoodId { get; set; }

    /// <summary>
    /// Resolved food name for display (localized based on Accept-Language).
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Original (canonical) food name, unaffected by language resolution.
    /// </summary>
    public string RawName { get; set; } = string.Empty;

    /// <summary>
    /// English name, if available.
    /// </summary>
    public string? NameEn { get; set; }

    /// <summary>
    /// Czech name, if available.
    /// </summary>
    public string? NameCs { get; set; }

    /// <summary>
    /// German name, if available.
    /// </summary>
    public string? NameDe { get; set; }

    /// <summary>
    /// Nutritional values per 100 grams.
    /// </summary>
    public NutrientValueDto NutrientValue { get; set; } = new();

    /// <summary>
    /// Allergens contained in this food.
    /// </summary>
    public List<Allergen> Allergens { get; set; } = [];

    /// <summary>
    /// Dietary preferences this food satisfies.
    /// </summary>
    public List<DietaryPreference> DietaryPreferences { get; set; } = [];

    /// <summary>
    /// The caller's own coach-private tags on this food; empty if the caller has none assigned.
    /// </summary>
    public List<FoodTagDto> Tags { get; set; } = [];

    /// <summary>
    /// Common serving sizes. The first entry is the default serving.
    /// </summary>
    public List<ServingSizeDto> CommonServings { get; set; } = [];

    /// <summary>
    /// Food category.
    /// </summary>
    public FoodCategory Category { get; set; }

    /// <summary>
    /// Optional user note.
    /// </summary>
    public string? Note { get; set; }

    /// <summary>
    /// Visibility of the food (Public = visible to all nutritionists, Private = visible only to its creator).
    /// </summary>
    public FoodVisibility Visibility { get; set; }

    /// <summary>
    /// URL of the main food image in blob storage. Null when no image has been uploaded.
    /// </summary>
    public string? ImageUrl { get; set; }

    /// <summary>
    /// URLs of gallery images (up to 6 entries); new uploads use unique keys, legacy ones may be positional.
    /// </summary>
    public List<string> GalleryImageUrls { get; set; } = [];

    /// <summary>
    /// True when the authenticated caller is the nutritionist who created this food.
    /// Clients can use this flag to decide whether to show edit/delete affordances.
    /// </summary>
    public bool IsOwnedByCurrentUser { get; set; }

    /// <summary>
    /// True when this food has no owning nutritionist (a platform system/catalog entry).
    /// Lets clients distinguish the three Library badge states — System, Mine
    /// (<see cref="IsOwnedByCurrentUser"/>), and Shared (another coach's Public food, neither of
    /// the above) — without exposing the owner's identifier itself.
    /// </summary>
    public bool IsSystem { get; set; }

    /// <summary>
    /// True when the authenticated caller has hidden this food from their library. Only the caller's
    /// own flag is exposed, never who else hid it.
    /// </summary>
    public bool IsHiddenByCurrentUser { get; set; }

    /// <summary>
    /// Maps a <see cref="Food"/> document to a <see cref="FoodSummary"/> DTO.
    /// </summary>
    /// <param name="food">The food document.</param>
    /// <param name="language">Two-letter language code for name resolution (e.g. "cs", "de"). Defaults to "en".</param>
    /// <param name="currentUserId">Id of the authenticated user; used to resolve <see cref="IsOwnedByCurrentUser"/>.</param>
    /// <param name="tags">The caller's own tag chips for this food (#1120), pre-resolved via
    /// <see cref="FoodTagLookup"/> — this factory does not query the database itself.</param>
    public static FoodSummary FromDocument(
        Food food, string? language = null, Guid? currentUserId = null, List<FoodTagDto>? tags = null) => new()
    {
        FoodId = food.ExternalId,
        Name = food.LocalizedNames?.Resolve(language) ?? food.Name,
        RawName = food.Name,
        NameEn = food.LocalizedNames?.En,
        NameCs = food.LocalizedNames?.Cs,
        NameDe = food.LocalizedNames?.De,
        NutrientValue = new NutrientValueDto
        {
            Kcal = food.NutrientValue.Kcal,
            Protein = food.NutrientValue.Protein,
            Carbs = food.NutrientValue.Carbs,
            Fat = food.NutrientValue.Fat,
            Fiber = food.NutrientValue.Fiber,
            Sugar = food.NutrientValue.Sugar,
            SaturatedFat = food.NutrientValue.SaturatedFat,
            Salt = food.NutrientValue.Salt
        },
        Category = food.Category,
        Note = food.Note,
        Visibility = food.Visibility,
        ImageUrl = food.ImageUrl,
        GalleryImageUrls = food.GalleryImageUrls,
        IsOwnedByCurrentUser = currentUserId.HasValue
            && food.NutritionistId.HasValue
            && food.NutritionistId.Value == currentUserId.Value,
        IsSystem = food.NutritionistId is null,
        IsHiddenByCurrentUser = currentUserId.HasValue && food.HiddenByNutritionistIds.Contains(currentUserId.Value),
        Allergens = FoodEnumListMapping.ParseStoredNames<Allergen>(food.Allergens),
        DietaryPreferences = FoodEnumListMapping.ParseStoredNames<DietaryPreference>(food.DietaryPreferences),
        Tags = tags ?? [],
        CommonServings = food.CommonServings
            .Select(s => new ServingSizeDto { Label = s.Label, WeightGrams = s.WeightGrams })
            .ToList()
    };
}

/// <summary>
/// Nutrient values DTO for API responses.
/// </summary>
public class NutrientValueDto
{
    /// <summary>
    /// Energy in kilocalories per 100 grams.
    /// </summary>
    public decimal Kcal { get; set; }

    /// <summary>
    /// Protein in grams per 100 grams.
    /// </summary>
    public decimal Protein { get; set; }

    /// <summary>
    /// Carbohydrates in grams per 100 grams.
    /// </summary>
    public decimal Carbs { get; set; }

    /// <summary>
    /// Fat in grams per 100 grams.
    /// </summary>
    public decimal Fat { get; set; }

    /// <summary>
    /// Fiber in grams per 100 grams.
    /// </summary>
    public decimal? Fiber { get; set; }

    /// <summary>
    /// Sugar in grams per 100 grams.
    /// </summary>
    public decimal? Sugar { get; set; }

    /// <summary>
    /// Saturated fat in grams per 100 grams.
    /// </summary>
    public decimal? SaturatedFat { get; set; }

    /// <summary>
    /// Salt in grams per 100 grams.
    /// </summary>
    public decimal? Salt { get; set; }
}

/// <summary>
/// Serving size DTO for API responses.
/// </summary>
public class ServingSizeDto
{
    /// <summary>
    /// Human-readable label.
    /// </summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Weight in grams.
    /// </summary>
    public decimal WeightGrams { get; set; }
}
