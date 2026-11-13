using FluentAssertions;
using FluentValidation.TestHelper;
using FormUp.Application.Features.CoachSubscriptions.SetKeepClients;

namespace FormUp.Tests.Validators;

/// <summary>
/// Shape rules of <see cref="SetKeepClientsValidator"/>; the count rules need the cap and live in the endpoint.
/// </summary>
public class SetKeepClientsValidatorTests
{
    private readonly SetKeepClientsValidator _validator = new();

    [Fact]
    public void Validate_IdsPresent_Passes()
    {
        _validator.TestValidate(new SetKeepClientsRequest { ClientIds = [Guid.NewGuid()] }).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_NoIdsAtAll_Passes()
    {
        _validator.TestValidate(new SetKeepClientsRequest { ClientIds = [] }).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_NullList_Fails()
    {
        _validator.TestValidate(new SetKeepClientsRequest { ClientIds = null! }).ShouldHaveValidationErrorFor(x => x.ClientIds);
    }

    [Fact]
    public void Validate_EmptyGuid_Fails()
    {
        _validator.TestValidate(new SetKeepClientsRequest { ClientIds = [Guid.Empty] }).IsValid.Should().BeFalse();
    }
}
