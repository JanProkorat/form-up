using FluentAssertions;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Services;

namespace FormUp.Tests.Services;

/// <summary>
/// Unit tests for <see cref="PromotionPricing"/>: the display-only discounted prices. A promotion that covers a plan
/// discounts that plan's second-role add-on as well.
/// </summary>
public class PromotionPricingTests
{
    private static Promotion Make(
        PromotionKind kind, long value, string plan = "pro", string? currency = null) => new()
    {
        Name = "Promotion",
        Kind = kind,
        Value = value,
        Currency = currency,
        PlanCodes = [plan],
        StripeCouponId = "coupon",
    };

    [Fact]
    public void Apply_Percent_DiscountsBothLines()
    {
        var (plan, addOn) = PromotionPricing.Apply(Make(PromotionKind.Percent, 30), 29900, 9900);

        plan.Should().Be(20930);
        addOn.Should().Be(6930);
    }

    [Fact]
    public void Apply_HundredPercent_IsFree()
    {
        var (plan, addOn) = PromotionPricing.Apply(Make(PromotionKind.Percent, 100), 29900, 9900);

        plan.Should().Be(0);
        addOn.Should().Be(0);
    }

    [Fact]
    public void Apply_PlanWithoutAnAddOn_ReturnsNullForTheAddOn()
    {
        var (_, addOn) = PromotionPricing.Apply(Make(PromotionKind.Percent, 10), 29900, null);

        addOn.Should().BeNull();
    }

    [Fact]
    public void Apply_PercentRoundsHalfAwayFromZero()
    {
        var (plan, _) = PromotionPricing.Apply(Make(PromotionKind.Percent, 15), 1010, null);

        plan.Should().Be(858, "15 % of 1010 is 151.5, which rounds up to 152 off");
    }

    [Fact]
    public void Apply_FixedAmountComesOffThePlanFirst_ThenTheAddOn()
    {
        var (plan, addOn) = PromotionPricing.Apply(Make(PromotionKind.FixedAmount, 30000), 29900, 9900);

        plan.Should().Be(0);
        addOn.Should().Be(9800);
    }

    [Fact]
    public void Apply_FixedAmountSmallerThanThePlanLine_LeavesTheAddOnAtFullPrice()
    {
        var (plan, addOn) = PromotionPricing.Apply(Make(PromotionKind.FixedAmount, 5000), 29900, 9900);

        plan.Should().Be(24900);
        addOn.Should().Be(9900, "the fixed amount is used up by the plan line");
    }

    [Fact]
    public void Apply_FixedAmountNeverGoesBelowZero()
    {
        var (plan, addOn) = PromotionPricing.Apply(Make(PromotionKind.FixedAmount, 90000), 29900, 9900);

        plan.Should().Be(0);
        addOn.Should().Be(0);
    }

    [Fact]
    public void ForSubscription_PercentOnACoveredPlan_DiscountsPlanAndAddOn()
    {
        var (discount, total) = PromotionPricing.ForSubscription(Make(PromotionKind.Percent, 30), "pro", "CZK", 29900, 9900);

        discount.Should().Be(11940);
        total.Should().Be(27860);
    }

    [Fact]
    public void ForSubscription_APromotionLimitedToSomePlans_StillDiscountsTheAddOnOfACoveredPlan()
    {
        var promotion = Make(PromotionKind.Percent, 30);
        promotion.PlanCodes = ["start", "pro"];

        var (discount, total) = PromotionPricing.ForSubscription(promotion, "pro", "CZK", 29900, 9900);

        discount.Should().Be(11940, "30 % of the plan line and 30 % of the add-on line");
        total.Should().Be(27860);
    }

    [Fact]
    public void ForSubscription_WithoutAnAddOn_UsesThePlanLineOnly()
    {
        var (discount, total) = PromotionPricing.ForSubscription(Make(PromotionKind.Percent, 30), "pro", "CZK", 29900, null);

        discount.Should().Be(8970);
        total.Should().Be(20930);
    }

    [Fact]
    public void ForSubscription_APlanThePromotionDoesNotCover_TakesNothingOffEitherLine()
    {
        var (discount, total) = PromotionPricing.ForSubscription(
            Make(PromotionKind.Percent, 30, plan: "start"), "pro", "CZK", 29900, 9900);

        discount.Should().Be(0);
        total.Should().Be(39800);
    }

    [Fact]
    public void ForSubscription_FixedAmountComesOffPlanThenAddOn_AndNeverBelowZero()
    {
        var promotion = Make(PromotionKind.FixedAmount, 30000, currency: "CZK");

        var (discount, total) = PromotionPricing.ForSubscription(promotion, "pro", "czk", 29900, 9900);

        discount.Should().Be(30000);
        total.Should().Be(9800);
    }

    [Fact]
    public void ForSubscription_FixedAmountInAnotherCurrency_TakesNothingOff()
    {
        var promotion = Make(PromotionKind.FixedAmount, 30000, currency: "CZK");

        var (discount, total) = PromotionPricing.ForSubscription(promotion, "pro", "EUR", 1900, null);

        discount.Should().Be(0);
        total.Should().Be(1900);
    }
}
