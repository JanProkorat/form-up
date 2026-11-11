using MongoDB.Bson.Serialization.Attributes;

namespace FormUp.Application.Domain.Documents;

/// <summary>
/// One preparation step of a recipe, with an optional title and an optional time in minutes.
/// </summary>
public class RecipeStep
{
    /// <summary>
    /// Optional short title of the step.
    /// </summary>
    [BsonElement("title")]
    [BsonIgnoreIfNull]
    public string? Title { get; set; }

    /// <summary>
    /// Instruction text of the step; empty when the step only has a title.
    /// </summary>
    [BsonElement("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Optional time the step takes, in whole minutes.
    /// </summary>
    [BsonElement("minutes")]
    [BsonIgnoreIfNull]
    public int? Minutes { get; set; }
}
