using System.Text.Json.Serialization;
using FormUp.Application.Domain.Enums;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace FormUp.Application.Domain.Documents;

/// <summary>
/// MongoDB document representing a food item with nutritional data.
/// </summary>
[BsonIgnoreExtraElements]
public class Food
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
    /// Name of the food item.
    /// </summary>
    [BsonElement("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Localized food names (en, cs, de) for multi-language support.
    /// Null for system/custom foods that were created without translations.
    /// </summary>
    [BsonElement("localizedNames")]
    [BsonIgnoreIfNull]
    public LocalizedNames? LocalizedNames { get; set; }

    /// <summary>
    /// Nutritional values per 100 grams.
    /// </summary>
    [BsonElement("nutrientValue")]
    public NutrientValue NutrientValue { get; set; } = new();

    /// <summary>
    /// Food category (e.g. Fruit, Meat, Dairy).
    /// </summary>
    [BsonElement("category")]
    [BsonRepresentation(BsonType.String)]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public FoodCategory Category { get; set; } = FoodCategory.Other;

    /// <summary>
    /// List of allergen identifiers (e.g. "gluten", "milk"). Stores <see cref="Enums.Allergen"/>
    /// names as plain strings — see <see cref="Services.FoodEnumListMapping"/> for
    /// the tolerant read/write mapping.
    /// </summary>
    [BsonElement("allergens")]
    public List<string> Allergens { get; set; } = [];

    /// <summary>
    /// List of dietary preference identifiers (e.g. "Vegan", "Keto"). Stores
    /// <see cref="Enums.DietaryPreference"/> names as plain strings — same mapping as
    /// <see cref="Allergens"/>.
    /// </summary>
    [BsonElement("dietaryPreferences")]
    public List<string> DietaryPreferences { get; set; } = [];

    /// <summary>
    /// Common serving sizes for quick selection. The first entry is the default serving.
    /// </summary>
    [BsonElement("commonServings")]
    public List<ServingSize> CommonServings { get; set; } = [];

    /// <summary>
    /// The nutritionist who created this custom food, if applicable.
    /// </summary>
    [BsonElement("nutritionistId")]
    public Guid? NutritionistId { get; set; }

    /// <summary>
    /// Visibility of the food. Public foods are visible to every nutritionist;
    /// private foods are visible only to their creator. Only the creator can
    /// edit, regardless of visibility.
    /// </summary>
    [BsonElement("visibility")]
    [BsonRepresentation(BsonType.String)]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public FoodVisibility Visibility { get; set; } = FoodVisibility.Public;

    /// <summary>
    /// Optional user note for this food item.
    /// </summary>
    [BsonElement("note")]
    [BsonIgnoreIfNull]
    public string? Note { get; set; }

    /// <summary>
    /// URL of the food image in blob storage (e.g. <c>foods/{foodId}.jpg</c>).
    /// Null until the nutritionist uploads an image and confirms it.
    /// </summary>
    [BsonElement("imageUrl")]
    [BsonIgnoreIfNull]
    public string? ImageUrl { get; set; }

    /// <summary>
    /// URLs of the food's gallery images in blob storage.
    /// Each entry looks like <c>foods/{foodId}/gallery-{n}.{ext}</c>.
    /// Capped at 6 entries. Append-only via the confirm endpoint.
    /// </summary>
    [BsonElement("galleryImageUrls")]
    public List<string> GalleryImageUrls { get; set; } = [];

    /// <summary>
    /// Soft-delete flag.
    /// </summary>
    [BsonElement("isDeleted")]
    public bool IsDeleted { get; set; }

    /// <summary>
    /// Nutritionists who hid this food from their own library. Never exposed in a response — it
    /// would reveal who hid what. Absent on older documents.
    /// </summary>
    [BsonElement("hiddenByNutritionistIds")]
    public List<Guid> HiddenByNutritionistIds { get; set; } = [];

    /// <summary>
    /// When this document was created.
    /// </summary>
    [BsonElement("dateCreated")]
    public DateTime DateCreated { get; set; }

    /// <summary>
    /// When this document was last updated.
    /// </summary>
    [BsonElement("dateUpdated")]
    public DateTime? DateUpdated { get; set; }
}
