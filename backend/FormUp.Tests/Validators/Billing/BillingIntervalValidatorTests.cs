using FluentAssertions;
using FluentValidation.TestHelper;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.Billing.CreateCheckoutSession;
using FormUp.Application.Features.Billing.PreviewPlanChange;

namespace FormUp.Tests.Validators.Billing;

/// <summary>
/// Unit tests for the <c>BillingInterval</c> rule of the checkout and plan-change preview validators; an omitted interval is Monthly.
/// </summary>
public class BillingIntervalValidatorTests
{
    [Theory]
    [InlineData(BillingInterval.Monthly, true)]
    [InlineData(BillingInterval.Annual, true)]
    [InlineData((BillingInterval)99, false)]
    public void CheckoutValidator_BillingInterval_MustBeAKnownValue(BillingInterval interval, bool valid)
    {
        var result = new CreateCheckoutSessionValidator().TestValidate(
            new CreateCheckoutSessionRequest { PlanCode = "pro", BillingInterval = interval });

        result.IsValid.Should().Be(valid);
    }

    [Theory]
    [InlineData(BillingInterval.Monthly, true)]
    [InlineData(BillingInterval.Annual, true)]
    [InlineData((BillingInterval)99, false)]
    public void PreviewValidator_BillingInterval_MustBeAKnownValue(BillingInterval interval, bool valid)
    {
        var result = new PreviewPlanChangeValidator().TestValidate(
            new PreviewPlanChangeRequest { PlanCode = "pro", BillingInterval = interval });

        result.IsValid.Should().Be(valid);
    }

    [Fact]
    public void Requests_WithoutAnInterval_DefaultToMonthly()
    {
        new CreateCheckoutSessionRequest().BillingInterval.Should().Be(BillingInterval.Monthly);
        new PreviewPlanChangeRequest().BillingInterval.Should().Be(BillingInterval.Monthly);
    }
}
