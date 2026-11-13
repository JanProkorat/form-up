using FluentAssertions;
using FluentValidation.TestHelper;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Features.AdminCoaches.GiveCoachPlanFree;

namespace FormUp.Tests.Validators.AdminCoaches;

/// <summary>
/// Rules of <see cref="GiveCoachPlanFreeValidator"/>.
/// </summary>
public class GiveCoachPlanFreeValidatorTests
{
    private readonly GiveCoachPlanFreeValidator _validator = new(TimeProvider.System);

    private static GiveCoachPlanFreeRequest Valid() => new()
    {
        CoachId = Guid.NewGuid(),
        PlanCode = "pro",
        FreeUntil = DateTimeOffset.UtcNow.AddDays(30),
        Reason = "beta tester",
    };

    [Fact]
    public void Validate_ValidRequest_Passes()
    {
        _validator.TestValidate(Valid()).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_NoEnd_Passes()
    {
        var request = Valid();
        request.FreeUntil = null;

        _validator.TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData("", ErrorCodes.Required)]
    [InlineData("   ", ErrorCodes.Required)]
    public void Validate_BlankReason_Fails(string reason, string errorCode)
    {
        var request = Valid();
        request.Reason = reason;

        _validator.TestValidate(request).Errors.Should().Contain(error => error.ErrorCode == errorCode);
    }

    [Fact]
    public void Validate_ReasonAtTheLimit_PassesAndOneOverFails()
    {
        var request = Valid();
        request.Reason = new string('x', GiveCoachPlanFreeValidator.MaxReasonLength);
        _validator.TestValidate(request).ShouldNotHaveAnyValidationErrors();

        request.Reason += "x";
        _validator.TestValidate(request).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_EmptyPlanCode_Fails()
    {
        var request = Valid();
        request.PlanCode = string.Empty;

        _validator.TestValidate(request).Errors.Should().Contain(error => error.ErrorCode == ErrorCodes.Required);
    }

    [Fact]
    public void Validate_FreePlanCode_Fails()
    {
        var request = Valid();
        request.PlanCode = SubscriptionPlanCodes.Free;

        _validator.TestValidate(request).Errors.Should().Contain(error => error.ErrorCode == ErrorCodes.OutOfRange);
    }

    [Fact]
    public void Validate_EndInThePast_Fails()
    {
        var request = Valid();
        request.FreeUntil = DateTimeOffset.UtcNow.AddMinutes(-1);

        _validator.TestValidate(request).Errors.Should().Contain(error => error.ErrorCode == ErrorCodes.OutOfRange);
    }
}
