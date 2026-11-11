namespace FormUp.Application.Features.Recipes.Shared;

/// <summary>
/// Wire shape of one recipe preparation step.
/// </summary>
public class RecipeStepDto
{
    /// <summary>
    /// Instruction text of the step (at most 2000 characters).
    /// </summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Optional time the step takes, in whole minutes (1 to 180).
    /// </summary>
    public int? Minutes { get; set; }
}
