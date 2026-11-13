using FluentAssertions;
using FluentValidation.TestHelper;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Features.AdminCoaches.CancelCoachRenewal;
using FormUp.Application.Features.AdminCoaches.DisableCoachAccountByAdmin;
using FormUp.Application.Features.AdminCoaches.EndCoachPlan;
using FormUp.Application.Features.AdminCoaches.ListAdminCoaches;
using FormUp.Application.Domain.Enums;

namespace FormUp.Tests.Validators.AdminCoaches;

/// <summary>
/// Rules of the validators of the cancel-renewal, disable and list actions.
/// </summary>
public class AdminCoachReasonValidatorTests
{
    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("customer asked", true)]
    public void CancelRenewal_Reason_MustBePresent(string reason, bool valid)
    {
        var result = new CancelCoachRenewalValidator().TestValidate(
            new CancelCoachRenewalRequest { CoachId = Guid.NewGuid(), Reason = reason });

        result.IsValid.Should().Be(valid);
    }

    [Fact]
    public void CancelRenewal_ReasonOverTheLimit_Fails()
    {
        var result = new CancelCoachRenewalValidator().TestValidate(new CancelCoachRenewalRequest
        {
            CoachId = Guid.NewGuid(),
            Reason = new string('x', CancelCoachRenewalValidator.MaxReasonLength + 1),
        });

        result.Errors.Should().Contain(error => error.ErrorCode == ErrorCodes.OutOfRange);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("customer asked", true)]
    public void EndPlan_Reason_MustBePresent(string reason, bool valid)
    {
        var result = new EndCoachPlanValidator().TestValidate(
            new EndCoachPlanRequest { CoachId = Guid.NewGuid(), Reason = reason });

        result.IsValid.Should().Be(valid);
    }

    [Fact]
    public void EndPlan_ReasonOverTheLimit_Fails()
    {
        var result = new EndCoachPlanValidator().TestValidate(new EndCoachPlanRequest
        {
            CoachId = Guid.NewGuid(),
            Reason = new string('x', EndCoachPlanValidator.MaxReasonLength + 1),
        });

        result.Errors.Should().Contain(error => error.ErrorCode == ErrorCodes.OutOfRange);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("abuse report", true)]
    public void Disable_Reason_MustBePresent(string reason, bool valid)
    {
        var result = new DisableCoachAccountByAdminValidator().TestValidate(
            new DisableCoachAccountByAdminRequest { CoachId = Guid.NewGuid(), Reason = reason });

        result.IsValid.Should().Be(valid);
    }

    [Fact]
    public void Disable_ReasonOverTheLimit_Fails()
    {
        var result = new DisableCoachAccountByAdminValidator().TestValidate(new DisableCoachAccountByAdminRequest
        {
            CoachId = Guid.NewGuid(),
            Reason = new string('x', DisableCoachAccountByAdminValidator.MaxReasonLength + 1),
        });

        result.Errors.Should().Contain(error => error.ErrorCode == ErrorCodes.OutOfRange);
    }

    [Theory]
    [InlineData(1, 20, true)]
    [InlineData(1, 100, true)]
    [InlineData(0, 20, false)]
    [InlineData(1, 0, false)]
    [InlineData(1, 101, false)]
    public void List_Paging_MustBeInRange(int page, int pageSize, bool valid)
    {
        var result = new ListAdminCoachesValidator().TestValidate(
            new ListAdminCoachesRequest { Page = page, PageSize = pageSize });

        result.IsValid.Should().Be(valid);
    }

    [Fact]
    public void List_UnknownStatusFilter_Fails()
    {
        var result = new ListAdminCoachesValidator().TestValidate(
            new ListAdminCoachesRequest { Status = (AdminCoachStatusFilter)99 });

        result.Errors.Should().Contain(error => error.ErrorCode == ErrorCodes.OutOfRange);
    }
}
