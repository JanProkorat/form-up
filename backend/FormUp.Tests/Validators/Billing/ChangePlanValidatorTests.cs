using FluentAssertions;
using FluentValidation.TestHelper;
using FormUp.Application.Features.Billing.ChangePlan;

namespace FormUp.Tests.Validators.Billing;

/// <summary>
/// Unit tests for <c>ChangePlanValidator</c>: a plan code and an attempt id, which keys the charge, are required.
/// </summary>
public class ChangePlanValidatorTests
{
    private static TestValidationResult<ChangePlanRequest> Validate(string planCode, Guid attemptId) =>
        new ChangePlanValidator().TestValidate(new ChangePlanRequest { PlanCode = planCode, AttemptId = attemptId });

    [Fact]
    public void Validate_PlanCodeAndAttemptId_Passes()
    {
        Validate("pro", Guid.NewGuid()).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_EmptyAttemptId_Fails()
    {
        var result = Validate("pro", Guid.Empty);

        result.ShouldHaveValidationErrorFor(x => x.AttemptId);
        result.Errors.Should().ContainSingle();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Validate_BlankPlanCode_Fails(string planCode)
    {
        Validate(planCode, Guid.NewGuid()).ShouldHaveValidationErrorFor(x => x.PlanCode);
    }

    [Fact]
    public void Validate_TooLongPlanCode_Fails()
    {
        Validate(new string('p', 101), Guid.NewGuid()).ShouldHaveValidationErrorFor(x => x.PlanCode);
    }
}
