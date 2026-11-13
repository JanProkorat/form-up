using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.SubscriptionPlans.CreateSubscriptionPlan;
using FluentAssertions;
using FluentValidation.TestHelper;

namespace FormUp.Tests.Validators;

/// <summary>
/// Tests for <see cref="CreateSubscriptionPlanValidator"/>.
/// </summary>
public class CreateSubscriptionPlanValidatorTests
{
    private readonly CreateSubscriptionPlanValidator _validator = new();

    private static CreateSubscriptionPlanRequest ValidRequest() => new()
    {
        Code = "small",
        NameCs = "Malý",
        NameEn = "Small",
        NameDe = "Klein",
        BothRolesIncluded = true,
        Currency = "CZK",
        PriceMinorUnits = 29900,
        BillingInterval = BillingInterval.Monthly,
        MaxActiveClients = 10,
    };

    [Fact]
    public void ValidRequest_PassesValidation()
    {
        var result = _validator.TestValidate(ValidRequest());
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Code_Empty_FailsWithRequiredCode()
    {
        var req = ValidRequest();
        req.Code = "";

        var result = _validator.TestValidate(req);
        result.ShouldHaveValidationErrorFor(x => x.Code).WithErrorCode(ErrorCodes.Required);
    }

    [Fact]
    public void Code_ContainsUppercase_FailsWithOutOfRangeCode()
    {
        var req = ValidRequest();
        req.Code = "Small-Tier";

        var result = _validator.TestValidate(req);
        result.ShouldHaveValidationErrorFor(x => x.Code).WithErrorCode(ErrorCodes.OutOfRange);
    }

    [Fact]
    public void Currency_NotThreeUppercaseLetters_FailsWithOutOfRangeCode()
    {
        var req = ValidRequest();
        req.Currency = "czk";

        var result = _validator.TestValidate(req);
        result.ShouldHaveValidationErrorFor(x => x.Currency).WithErrorCode(ErrorCodes.OutOfRange);
    }

    [Fact]
    public void Currency_NotInSupportedAllowlist_FailsWithUnsupportedCurrencyCode()
    {
        var req = ValidRequest();
        req.Currency = "GBP";

        var result = _validator.TestValidate(req);
        result.ShouldHaveValidationErrorFor(x => x.Currency).WithErrorCode(ErrorCodes.UnsupportedCurrency);
    }

    [Fact]
    public void PriceMinorUnits_Negative_FailsWithOutOfRangeCode()
    {
        var req = ValidRequest();
        req.PriceMinorUnits = -1;

        var result = _validator.TestValidate(req);
        result.ShouldHaveValidationErrorFor(x => x.PriceMinorUnits).WithErrorCode(ErrorCodes.OutOfRange);
    }

    [Fact]
    public void MaxActiveClients_Zero_FailsWithOutOfRangeCode()
    {
        var req = ValidRequest();
        req.MaxActiveClients = 0;

        var result = _validator.TestValidate(req);
        result.ShouldHaveValidationErrorFor(x => x.MaxActiveClients).WithErrorCode(ErrorCodes.OutOfRange);
    }

    [Fact]
    public void MaxActiveClients_Null_PassesValidation()
    {
        var req = ValidRequest();
        req.MaxActiveClients = null;

        var result = _validator.TestValidate(req);
        result.ShouldNotHaveValidationErrorFor(x => x.MaxActiveClients);
    }

    [Fact]
    public void SecondRole_PaidAddOnWithoutBothRolesIncluded_PassesValidation()
    {
        var req = ValidRequest();
        req.BothRolesIncluded = false;
        req.SecondRoleAddOnPriceMinorUnits = 14900;

        _validator.TestValidate(req).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(true, 14900L)]
    [InlineData(false, null)]
    public void SecondRole_BothOrNeitherSet_FailsWithSecondRoleOptionInvalidCode(
        bool bothRolesIncluded, long? addOnPrice)
    {
        var req = ValidRequest();
        req.BothRolesIncluded = bothRolesIncluded;
        req.SecondRoleAddOnPriceMinorUnits = addOnPrice;

        var result = _validator.TestValidate(req);

        result.Errors.Should().Contain(e => e.ErrorCode == ErrorCodes.SecondRoleOptionInvalid);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void SecondRoleAddOnPrice_ZeroOrNegative_FailsWithOutOfRangeCode(long addOnPrice)
    {
        var req = ValidRequest();
        req.BothRolesIncluded = false;
        req.SecondRoleAddOnPriceMinorUnits = addOnPrice;

        var result = _validator.TestValidate(req);

        result.ShouldHaveValidationErrorFor(x => x.SecondRoleAddOnPriceMinorUnits)
            .WithErrorCode(ErrorCodes.OutOfRange);
    }

    [Fact]
    public void BillingInterval_OutOfEnum_FailsWithOutOfRangeCode()
    {
        var req = ValidRequest();
        req.BillingInterval = (BillingInterval)99;

        var result = _validator.TestValidate(req);
        result.ShouldHaveValidationErrorFor(x => x.BillingInterval).WithErrorCode(ErrorCodes.OutOfRange);
    }

    [Fact]
    public void ExternalPriceId_TooLong_FailsWithOutOfRangeCode()
    {
        var req = ValidRequest();
        req.ExternalPriceId = new string('a', 201);

        var result = _validator.TestValidate(req);
        result.ShouldHaveValidationErrorFor(x => x.ExternalPriceId).WithErrorCode(ErrorCodes.OutOfRange);
    }
}
