using FormUp.Application.Domain.Enums;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace FormUp.Application.Domain.Documents;

/// <summary>
/// MongoDB document representing a reusable recipe composed of multiple foods.
/// </summary>
public class Recipe
{
    /// <summary>
    /// MongoDB internal identifier.
    /// </summary>
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public ObjectId Id { get; set; }

    /// <summary>
    /// Public-facing identifier used in API requests and responses.
    /// </summary>
    [BsonElement("externalId")]
    public Guid ExternalId { get; set; }

    /// <summary>
    /// The nutritionist who owns this recipe.
    /// </summary>
    [BsonElement("nutritionistId")]
    public Guid NutritionistId { get; set; }

    /// <summary>
    /// Name of the recipe.
    /// </summary>
    [BsonElement("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Optional description or preparation instructions.
    /// </summary>
    [BsonElement("description")]
    [BsonIgnoreIfNull]
    public string? Description { get; set; }

    /// <summary>
    /// Estimated preparation/cooking time in minutes.
    /// </summary>
    [BsonElement("prepTimeMinutes")]
    [BsonIgnoreIfNull]
    public int? PrepTimeMinutes { get; set; }

    /// <summary>
    /// Ordered preparation steps.
    /// </summary>
    [BsonElement("steps")]
    [BsonIgnoreIfNull]
    public List<RecipeStep>? Steps { get; set; }

    /// <summary>
    /// Optional tip or note about the recipe.
    /// </summary>
    [BsonElement("note")]
    [BsonIgnoreIfNull]
    public string? Note { get; set; }

    /// <summary>
    /// List of food items in this recipe with denormalized nutrient snapshots.
    /// </summary>
    [BsonElement("foods")]
    public List<MealFood> Foods { get; set; } = [];

    /// <summary>
    /// Computed total macronutrients for the entire recipe.
    /// </summary>
    [BsonElement("totalNutrients")]
    public NutrientTotals TotalNutrients { get; set; } = new();

    /// <summary>
    /// Visibility level controlling who can access this recipe.
    /// Public recipes are visible to all nutritionists; private ones only to their creator.
    /// Only the creator can edit or delete, regardless of visibility.
    /// </summary>
    [BsonElement("visibility")]
    [BsonRepresentation(BsonType.String)]
    public RecipeVisibility Visibility { get; set; } = RecipeVisibility.Public;

    /// <summary>
    /// When this document was created.
    /// </summary>
    [BsonElement("dateCreated")]
    public DateTime DateCreated { get; set; }

    /// <summary>
    /// URL of the recipe's main image in blob storage (e.g. <c>recipes/{recipeId}/main.jpg</c>).
    /// Null until the nutritionist uploads and confirms a main image.
    /// </summary>
    [BsonElement("imageUrl")]
    [BsonIgnoreIfNull]
    public string? ImageUrl { get; set; }

    /// <summary>
    /// URLs of the recipe's gallery images in blob storage.
    /// Each entry looks like <c>recipes/{recipeId}/gallery-{n}.{ext}</c>.
    /// Capped at 6 entries. Append-only via the confirm endpoint.
    /// </summary>
    [BsonElement("galleryImageUrls")]
    public List<string> GalleryImageUrls { get; set; } = [];

    /// <summary>
    /// When this document was last updated.
    /// </summary>
    [BsonElement("dateUpdated")]
    [BsonIgnoreIfNull]
    public DateTime? DateUpdated { get; set; }

    /// <summary>
    /// Meal types this recipe is suited for, stored as <see cref="RecipeMealType"/> names. Older
    /// documents may hold lowercase spellings ("breakfast"); readers parse case-insensitively and
    /// filters match both spellings. Absent on some legacy documents.
    /// </summary>
    [BsonElement("mealTypes")]
    [BsonIgnoreIfNull]
    public List<string>? MealTypes { get; set; }

    /// <summary>
    /// Number of servings the recipe yields. Legacy documents without the field read as 1.
    /// </summary>
    [BsonElement("servings")]
    public int Servings { get; set; } = 1;

    /// <summary>
    /// Preparation difficulty, if set.
    /// </summary>
    [BsonElement("difficulty")]
    [BsonRepresentation(BsonType.String)]
    [BsonIgnoreIfNull]
    public RecipeDifficulty? Difficulty { get; set; }

    /// <summary>
    /// Cooking time in minutes, separate from <see cref="PrepTimeMinutes"/>.
    /// </summary>
    [BsonElement("cookTimeMinutes")]
    [BsonIgnoreIfNull]
    public int? CookTimeMinutes { get; set; }

    /// <summary>
    /// Dietary preferences the recipe satisfies. Stores <see cref="DietaryPreference"/> names as plain
    /// strings — see <see cref="Services.FoodEnumListMapping"/>. Allergens are never stored; they are derived
    /// on read from the ingredient foods.
    /// </summary>
    [BsonElement("dietaryPreferences")]
    public List<string> DietaryPreferences { get; set; } = [];

    /// <summary>
    /// Optimistic concurrency version. Incremented on each update.
    /// Legacy documents missing this BSON field deserialize to 1 — MongoDB.Driver 3.x
    /// preserves the C# property initializer value (= 1) when the field is absent.
    /// The CAS write filter in UpdateRecipeEndpoint handles this by also matching field-absent
    /// documents when the client echoes back Version = 1 (the value it receives for
    /// a legacy doc). After the first such write the field is stored and normal CAS applies.
    /// </summary>
    [BsonElement("version")]
    public int Version { get; set; } = 1;
}
