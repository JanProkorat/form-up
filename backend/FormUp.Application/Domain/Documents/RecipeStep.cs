using MongoDB.Bson.Serialization.Attributes;

namespace FormUp.Application.Domain.Documents;

/// <summary>
/// One preparation step of a recipe, with an optional time in minutes.
/// </summary>
public class RecipeStep
{
    /// <summary>
    /// Instruction text of the step.
    /// </summary>
    [BsonElement("text")]
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Optional time the step takes, in whole minutes.
    /// </summary>
    [BsonElement("minutes")]
    [BsonIgnoreIfNull]
    public int? Minutes { get; set; }
}
