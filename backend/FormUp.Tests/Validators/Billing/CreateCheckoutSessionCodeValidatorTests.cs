using FluentAssertions;
using FluentValidation.TestHelper;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Features.Billing.CreateCheckoutSession;

namespace FormUp.Tests.Validators.Billing;

/// <summary>
/// Unit tests for the optional promotion <c>Code</c> of <see cref="CreateCheckoutSessionValidator"/>: absent or empty means
/// no code; otherwise the same shape the apply-code route accepts.
/// </summary>
public class CreateCheckoutSessionCodeValidatorTests
{
    private static TestValidationResult<CreateCheckoutSessionRequest> Validate(string? code) =>
        new CreateCheckoutSessionValidator().TestValidate(new CreateCheckoutSessionRequest { PlanCode = "pro", Code = code });

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("COACH20")]
    [InlineData("coach_20-b")]
    public void Validate_AbsentEmptyOrWellFormedCode_Passes(string? code) => Validate(code).IsValid.Should().BeTrue();

    [Theory]
    [InlineData("bad code")]
    [InlineData("code!")]
    [InlineData("kód")]
    public void Validate_CodeWithInvalidCharacters_FailsWithTheCodeError(string code) =>
        Validate(code).Errors.Should().Contain(error => error.ErrorCode == ErrorCodes.PromotionCodeInvalid);

    [Fact]
    public void Validate_CodeOverTheLimit_FailsWithTheCodeError() =>
        Validate(new string('a', CreateCheckoutSessionValidator.MaxCodeLength + 1)).Errors
            .Should().Contain(error => error.ErrorCode == ErrorCodes.PromotionCodeInvalid);
}
