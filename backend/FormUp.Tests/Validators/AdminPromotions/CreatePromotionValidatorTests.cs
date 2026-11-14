using FluentAssertions;
using FluentValidation.TestHelper;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.AdminPromotions.CreatePromotion;

namespace FormUp.Tests.Validators.AdminPromotions;

/// <summary>
/// Unit tests for <see cref="CreatePromotionValidator"/>: every input rule of the admin create drawer.
/// </summary>
public class CreatePromotionValidatorTests
{
    private static CreatePromotionRequest Valid() => new()
    {
        Name = "Partner code",
        Kind = PromotionKind.Percent,
        Value = 20,
        Mode = PromotionMode.Code,
        Code = "COACH20",
        Duration = PromotionDuration.Forever,
        ClaimFrom = DateTimeOffset.UtcNow,
        PlanCodes = ["pro"],
    };

    private static TestValidationResult<CreatePromotionRequest> Validate(CreatePromotionRequest request) =>
        new CreatePromotionValidator().TestValidate(request);

    private static void ShouldFailWith(CreatePromotionRequest request, string errorCode) =>
        Validate(request).Errors.Should().Contain(error => error.ErrorCode == errorCode);

    [Fact]
    public void Validate_AValidCodePromotion_Passes() => Validate(Valid()).IsValid.Should().BeTrue();

    [Fact]
    public void Validate_AValidAutomaticPromotionWithMonths_Passes()
    {
        var request = Valid();
        request.Mode = PromotionMode.Automatic;
        request.Code = null;
        request.Duration = PromotionDuration.Months;
        request.DurationMonths = 3;
        request.ClaimUntil = request.ClaimFrom.AddDays(30).AddYears(1);
        request.MaxUses = 50;

        Validate(request).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_EmptyName_Fails(string name)
    {
        var request = Valid();
        request.Name = name;

        Validate(request).ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Fact]
    public void Validate_NameOverTheLimit_Fails()
    {
        var request = Valid();
        request.Name = new string('n', CreatePromotionValidator.MaxNameLength + 1);

        Validate(request).ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Theory]
    [InlineData(PromotionKind.Percent, 0, null)]
    [InlineData(PromotionKind.Percent, 101, null)]
    [InlineData(PromotionKind.FixedAmount, 0, "CZK")]
    [InlineData(PromotionKind.FixedAmount, 5000, null)]
    [InlineData(PromotionKind.FixedAmount, 5000, "EUR")]
    public void Validate_ValueOutOfRangeOrFixedAmountWithoutCzk_FailsWithValueInvalid(PromotionKind kind, long value, string? currency)
    {
        var request = Valid();
        request.Kind = kind;
        request.Value = value;
        request.Currency = currency;

        ShouldFailWith(request, ErrorCodes.PromotionValueInvalid);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public void Validate_PercentBoundaries_Pass(long value)
    {
        var request = Valid();
        request.Value = value;

        Validate(request).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_FixedAmountInCzkInAnyCase_Passes()
    {
        var request = Valid();
        request.Kind = PromotionKind.FixedAmount;
        request.Value = 50000;
        request.Currency = "czk";

        Validate(request).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Validate_CodeModeWithoutACode_FailsWithModeInvalid(string? code)
    {
        var request = Valid();
        request.Code = code;

        ShouldFailWith(request, ErrorCodes.PromotionModeInvalid);
    }

    [Fact]
    public void Validate_AutomaticModeWithACode_FailsWithModeInvalid()
    {
        var request = Valid();
        request.Mode = PromotionMode.Automatic;

        ShouldFailWith(request, ErrorCodes.PromotionModeInvalid);
    }

    [Theory]
    [InlineData("COACH 20")]
    [InlineData("COACH-20")]
    [InlineData("COACH_20")]
    [InlineData("KÓD20")]
    public void Validate_CodeWithCharactersThatAreNotLettersAndDigits_FailsWithCodeInvalid(string code)
    {
        var request = Valid();
        request.Code = code;

        ShouldFailWith(request, ErrorCodes.PromotionCodeInvalid);
    }

    [Fact]
    public void Validate_CodeOverTheLimit_FailsWithCodeInvalid()
    {
        var request = Valid();
        request.Code = new string('A', CreatePromotionValidator.MaxCodeLength + 1);

        ShouldFailWith(request, ErrorCodes.PromotionCodeInvalid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(37)]
    public void Validate_SeveralMonthsWithoutAValidCount_FailsWithMonthsInvalid(int? months)
    {
        var request = Valid();
        request.Duration = PromotionDuration.Months;
        request.DurationMonths = months;

        ShouldFailWith(request, ErrorCodes.PromotionMonthsInvalid);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(36)]
    public void Validate_SeveralMonthsBoundaries_Pass(int months)
    {
        var request = Valid();
        request.Duration = PromotionDuration.Months;
        request.DurationMonths = months;

        Validate(request).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(PromotionDuration.FirstMonth)]
    [InlineData(PromotionDuration.Forever)]
    public void Validate_AMonthCountWithAnotherDuration_FailsWithMonthsInvalid(PromotionDuration duration)
    {
        var request = Valid();
        request.Duration = duration;
        request.DurationMonths = 3;

        ShouldFailWith(request, ErrorCodes.PromotionMonthsInvalid);
    }

    [Fact]
    public void Validate_ClaimUntilBeforeClaimFrom_FailsWithWindowInvalid()
    {
        var request = Valid();
        request.ClaimFrom = DateTimeOffset.UtcNow.AddDays(10);
        request.ClaimUntil = DateTimeOffset.UtcNow.AddDays(5);

        ShouldFailWith(request, ErrorCodes.PromotionWindowInvalid);
    }

    [Fact]
    public void Validate_ClaimUntilInThePast_FailsWithWindowInvalid()
    {
        var request = Valid();
        request.ClaimFrom = DateTimeOffset.UtcNow.AddDays(-10);
        request.ClaimUntil = DateTimeOffset.UtcNow.AddDays(-1);

        ShouldFailWith(request, ErrorCodes.PromotionWindowInvalid);
    }

    [Fact]
    public void Validate_NoPlans_FailsWithPlansInvalid()
    {
        var request = Valid();
        request.PlanCodes = [];

        ShouldFailWith(request, ErrorCodes.PromotionPlansInvalid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Validate_MaxUsesBelowOne_FailsWithMaxUsesInvalid(int maxUses)
    {
        var request = Valid();
        request.MaxUses = maxUses;

        ShouldFailWith(request, ErrorCodes.PromotionMaxUsesInvalid);
    }
}
