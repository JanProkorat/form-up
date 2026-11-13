using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.SubscriptionPlans.Shared;
using FormUp.Application.Features.SubscriptionPlans.UpdateSubscriptionPlan;
using FluentAssertions;
using FluentValidation.TestHelper;

namespace FormUp.Tests.Validators;

/// <summary>
/// Tests for <see cref="UpdateSubscriptionPlanValidator"/>.
/// </summary>
public class UpdateSubscriptionPlanValidatorTests
{
    private readonly UpdateSubscriptionPlanValidator _validator = new();

    private static UpdateSubscriptionPlanRequest ValidRequest() => new()
    {
        Code = "small",
        NameCs = "Malý",
        NameEn = "Small",
        NameDe = "Klein",
        BothRolesIncluded = true,
        CanCreatePlans = true,
        CanMessage = true,
        CanSendQuestionnaires = true,
        CanUseWeeklyCheckIns = true,
        CanUsePerClientCheckInConfig = true,
        CanUsePayments = true,
        Currency = "CZK",
        PriceMinorUnits = 29900,
        BillingInterval = BillingInterval.Monthly,
        MaxActiveClients = new OptionalField<int?>(10),
        IsActive = true,
    };

    [Fact]
    public void ValidRequest_PassesValidation()
    {
        var result = _validator.TestValidate(ValidRequest());
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void BothRolesIncluded_Omitted_FailsWithRequiredCode()
    {
        var req = ValidRequest();
        req.BothRolesIncluded = null;

        var result = _validator.TestValidate(req);
        result.ShouldHaveValidationErrorFor(x => x.BothRolesIncluded).WithErrorCode(ErrorCodes.Required);
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

    [Fact]
    public void SecondRoleAddOnPrice_Zero_FailsWithOutOfRangeCode()
    {
        var req = ValidRequest();
        req.BothRolesIncluded = false;
        req.SecondRoleAddOnPriceMinorUnits = 0;

        var result = _validator.TestValidate(req);

        result.ShouldHaveValidationErrorFor(x => x.SecondRoleAddOnPriceMinorUnits)
            .WithErrorCode(ErrorCodes.OutOfRange);
    }

    [Fact]
    public void SecondRole_PaidAddOnWithoutBothRolesIncluded_PassesValidation()
    {
        var req = ValidRequest();
        req.BothRolesIncluded = false;
        req.SecondRoleAddOnPriceMinorUnits = 14900;

        _validator.TestValidate(req).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Currency_NotThreeUppercaseLetters_FailsWithOutOfRangeCode()
    {
        var req = ValidRequest();
        req.Currency = "eur";

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
        req.MaxActiveClients = new OptionalField<int?>(0);

        var result = _validator.TestValidate(req);
        result.ShouldHaveValidationErrorFor(x => x.MaxActiveClients.Value).WithErrorCode(ErrorCodes.OutOfRange);
    }

    [Fact]
    public void MaxActiveClients_NotSet_FailsWithRequiredCode()
    {
        var req = ValidRequest();
        req.MaxActiveClients = default;

        var result = _validator.TestValidate(req);
        result.ShouldHaveValidationErrorFor(x => x.MaxActiveClients).WithErrorCode(ErrorCodes.Required);
    }

    [Fact]
    public void MaxActiveClients_ExplicitNull_PassesValidation()
    {
        var req = ValidRequest();
        req.MaxActiveClients = new OptionalField<int?>(null);

        var result = _validator.TestValidate(req);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void NameCs_Empty_FailsWithRequiredCode()
    {
        var req = ValidRequest();
        req.NameCs = "";

        var result = _validator.TestValidate(req);
        result.ShouldHaveValidationErrorFor(x => x.NameCs).WithErrorCode(ErrorCodes.Required);
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
    public void Code_UppercaseLetters_FailsWithOutOfRangeCode()
    {
        var req = ValidRequest();
        req.Code = "Small";

        var result = _validator.TestValidate(req);
        result.ShouldHaveValidationErrorFor(x => x.Code).WithErrorCode(ErrorCodes.OutOfRange);
    }

    [Fact]
    public void CanCreatePlans_NotSet_FailsWithRequiredCode()
    {
        var req = ValidRequest();
        req.CanCreatePlans = null;

        var result = _validator.TestValidate(req);
        result.ShouldHaveValidationErrorFor(x => x.CanCreatePlans).WithErrorCode(ErrorCodes.Required);
    }

    [Fact]
    public void CanUsePayments_NotSet_FailsWithRequiredCode()
    {
        var req = ValidRequest();
        req.CanUsePayments = null;

        var result = _validator.TestValidate(req);
        result.ShouldHaveValidationErrorFor(x => x.CanUsePayments).WithErrorCode(ErrorCodes.Required);
    }

    [Fact]
    public void IsActive_NotSet_FailsWithRequiredCode()
    {
        var req = ValidRequest();
        req.IsActive = null;

        var result = _validator.TestValidate(req);
        result.ShouldHaveValidationErrorFor(x => x.IsActive).WithErrorCode(ErrorCodes.Required);
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
