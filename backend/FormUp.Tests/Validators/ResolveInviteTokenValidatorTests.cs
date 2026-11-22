using FluentValidation.TestHelper;
using FormUp.Application.Features.Auth.ResolveInviteToken;

namespace FormUp.Tests.Validators;

public class ResolveInviteTokenValidatorTests
{
    private readonly ResolveInviteTokenValidator _validator = new();

    [Fact]
    public void Token_Valid_Passes()
    {
        _validator.TestValidate(new ResolveInviteTokenRequest { Token = new string('a', 100) })
            .ShouldNotHaveValidationErrorFor(x => x.Token);
    }

    [Fact]
    public void Token_Empty_Fails()
    {
        _validator.TestValidate(new ResolveInviteTokenRequest { Token = "" })
            .ShouldHaveValidationErrorFor(x => x.Token)
            .WithErrorCode("NotEmptyValidator");
    }

    [Fact]
    public void Token_OverLimit_Fails()
    {
        _validator.TestValidate(new ResolveInviteTokenRequest { Token = new string('a', 101) })
            .ShouldHaveValidationErrorFor(x => x.Token)
            .WithErrorCode("MaximumLengthValidator");
    }
}
