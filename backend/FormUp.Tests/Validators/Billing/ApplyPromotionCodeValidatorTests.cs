using FluentAssertions;
using FluentValidation.TestHelper;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Features.Billing.ApplyPromotionCode;

namespace FormUp.Tests.Validators.Billing;

/// <summary>
/// Unit tests for <see cref="ApplyPromotionCodeValidator"/>: a code is short and made of plain characters only.
/// </summary>
public class ApplyPromotionCodeValidatorTests
{
    private static TestValidationResult<ApplyPromotionCodeRequest> Validate(string code) =>
        new ApplyPromotionCodeValidator().TestValidate(new ApplyPromotionCodeRequest { Code = code });

    [Theory]
    [InlineData("WELCOME10")]
    [InlineData("spring_sale-2026")]
    [InlineData("A")]
    public void Validate_PlainCode_Passes(string code)
    {
        Validate(code).ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("two words")]
    [InlineData("semi;colon")]
    [InlineData("quote'")]
    [InlineData("příliš")]
    [InlineData("new\nline")]
    [InlineData("TRAILING\n")]
    public void Validate_EmptyOrInvalidCharacters_FailsWithTheInvalidCode(string code)
    {
        var result = Validate(code);

        result.Errors.Should().NotBeEmpty();
        result.Errors.Should().OnlyContain(error => error.ErrorCode == ErrorCodes.PromotionCodeInvalid);
    }

    [Fact]
    public void Validate_CodeAtTheLimit_PassesAndOneOverFails()
    {
        Validate(new string('A', ApplyPromotionCodeValidator.MaxCodeLength)).ShouldNotHaveAnyValidationErrors();

        Validate(new string('A', ApplyPromotionCodeValidator.MaxCodeLength + 1)).Errors
            .Should().ContainSingle().Which.ErrorCode.Should().Be(ErrorCodes.PromotionCodeInvalid);
    }
}
