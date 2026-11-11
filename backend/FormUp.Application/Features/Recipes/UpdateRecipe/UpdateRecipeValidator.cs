using FastEndpoints;
using FluentValidation;

namespace FormUp.Application.Features.Recipes.UpdateRecipe;

/// <summary>
/// Validates the <see cref="UpdateRecipeRequest"/>.
/// </summary>
public class UpdateRecipeValidator : Validator<UpdateRecipeRequest>
{
    /// <summary>
    /// Initializes validation rules for recipe update.
    /// </summary>
    public UpdateRecipeValidator()
    {
        RuleFor(x => x.RecipeId)
            .NotEmpty();

        RuleFor(x => x.Version)
            .GreaterThanOrEqualTo(1);

        RuleFor(x => x.Name)
            .NotEmpty()
            .MaximumLength(200);

        RuleFor(x => x.Description)
            .MaximumLength(5000);

        RuleFor(x => x.Servings)
            .GreaterThan(0)
            .WithMessage("Servings must be greater than zero.");

        RuleFor(x => x.CookTimeMinutes)
            .GreaterThanOrEqualTo(0)
            .When(x => x.CookTimeMinutes.HasValue);

        RuleFor(x => x.Difficulty)
            .IsInEnum()
            .When(x => x.Difficulty.HasValue);

        RuleFor(x => x.MealTypes)
            .NotEmpty()
            .WithMessage("A recipe must have at least one meal type.");

        RuleForEach(x => x.MealTypes).IsInEnum();

        RuleForEach(x => x.DietaryPreferences).IsInEnum();

        RuleFor(x => x.Steps)
            .Must(steps => steps is null || steps.Count <= 50)
            .WithMessage("A recipe can have at most 50 steps.");

        RuleForEach(x => x.Steps)
            .Must(step => step is null || (step.Description ?? string.Empty).Length <= 2000)
            .WithMessage("A step description can be at most 2000 characters.");

        RuleForEach(x => x.Steps)
            .Must(step => step is null || (step.Title ?? string.Empty).Length <= 120)
            .WithMessage("A step title can be at most 120 characters.");

        RuleForEach(x => x.Steps)
            .Must(step => step is null || step.Minutes is null or >= 1 and <= 180)
            .WithMessage("Step minutes must be between 1 and 180.");

        RuleFor(x => x.Foods)
            .NotEmpty()
            .WithMessage("A recipe must contain at least one food item.");

        RuleForEach(x => x.Foods).ChildRules(food =>
        {
            food.RuleFor(f => f.FoodExternalId)
                .NotEmpty();

            food.RuleFor(f => f.AmountGrams)
                .GreaterThan(0);
        });

        RuleFor(x => x.Visibility)
            .Must(v => !v.HasValue || Enum.IsDefined(v.Value))
            .WithMessage("Visibility must be a valid enum value.");
    }
}
