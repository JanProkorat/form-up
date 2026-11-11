using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.Recipes.CreateRecipe;
using FormUp.Application.Features.Recipes.Shared;
using FluentAssertions;
using FluentValidation.TestHelper;

namespace FormUp.Tests.Validators;

/// <summary>
/// Tests for <see cref="CreateRecipeValidator"/>.
/// </summary>
public class CreateRecipeValidatorTests
{
    private readonly CreateRecipeValidator _validator = new();

    private static CreateRecipeRequest ValidRequest() => new()
    {
        Name = "Chicken Salad",
        MealTypes = [RecipeMealType.Lunch],
        Foods = [new RecipeFoodDto { FoodExternalId = Guid.NewGuid(), AmountGrams = 100 }]
    };

    [Fact]
    public void BaselineRequest_IsValid_AndDefaultsToPrivateWithOneServing()
    {
        var req = ValidRequest();

        _validator.TestValidate(req).IsValid.Should().BeTrue();
        req.Visibility.Should().Be(RecipeVisibility.Private);
        req.Servings.Should().Be(1);
    }

    [Fact]
    public void Servings_Zero_FailsValidation()
    {
        var req = ValidRequest();
        req.Servings = 0;

        _validator.TestValidate(req).ShouldHaveValidationErrorFor(x => x.Servings);
    }

    [Fact]
    public void MealTypes_Empty_FailsValidation()
    {
        var req = ValidRequest();
        req.MealTypes = [];

        _validator.TestValidate(req).ShouldHaveValidationErrorFor(x => x.MealTypes);
    }

    [Fact]
    public void Foods_Empty_FailsValidation()
    {
        var req = ValidRequest();
        req.Foods = [];

        _validator.TestValidate(req).ShouldHaveValidationErrorFor(x => x.Foods);
    }

    [Fact]
    public void Steps_OverCountOrLength_FailsValidation()
    {
        var tooMany = ValidRequest();
        tooMany.Steps = Enumerable.Range(0, 51).Select(_ => new RecipeStepDto { Description = "step" }).ToList();
        var tooLong = ValidRequest();
        tooLong.Steps = [new RecipeStepDto { Description = new string('x', 2001) }];

        _validator.TestValidate(tooMany).IsValid.Should().BeFalse();
        _validator.TestValidate(tooLong).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(180, true)]
    [InlineData(181, false)]
    public void StepMinutes_Bounds_AreOneToOneEighty(int? minutes, bool expectedValid)
    {
        var req = ValidRequest();
        req.Steps = [new RecipeStepDto { Description = "Cook", Minutes = minutes }];

        _validator.TestValidate(req).IsValid.Should().Be(expectedValid);
    }

    [Theory]
    [InlineData(120, true)]
    [InlineData(121, false)]
    public void StepTitle_Length_IsAtMostOneTwenty(int length, bool expectedValid)
    {
        var req = ValidRequest();
        req.Steps = [new RecipeStepDto { Title = new string('t', length), Description = "Cook" }];

        _validator.TestValidate(req).IsValid.Should().Be(expectedValid);
    }

    [Fact]
    public void Steps_TitleOnlyAndDescriptionOnly_AreValid()
    {
        var req = ValidRequest();
        req.Steps = [new RecipeStepDto { Title = "Prep" }, new RecipeStepDto { Description = "Cook" }];

        _validator.TestValidate(req).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Steps_NullElement_DoesNotThrow()
    {
        var req = ValidRequest();
        req.Steps = [null!, new RecipeStepDto { Description = "Cook" }];

        var act = () => _validator.TestValidate(req);

        act.Should().NotThrow();
        _validator.TestValidate(req).IsValid.Should().BeTrue();
    }

    [Fact]
    public void NegativeCookTime_FailsValidation()
    {
        var req = ValidRequest();
        req.CookTimeMinutes = -1;

        _validator.TestValidate(req).ShouldHaveValidationErrorFor(x => x.CookTimeMinutes);
    }
}
