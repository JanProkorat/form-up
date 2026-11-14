using FluentAssertions;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Services;

namespace FormUp.Tests.Services;

/// <summary>
/// Unit tests for <see cref="PromotionPricing"/>: the display-only discounted prices.
/// </summary>
public class PromotionPricingTests
{
    private static Promotion Make(PromotionKind kind, long value, bool coversAddOn) => new()
    {
        Name = "Promotion",
        Kind = kind,
        Value = value,
        CoversSecondRoleAddOn = coversAddOn,
        StripeCouponId = "coupon",
    };

    [Fact]
    public void Apply_PercentCoveringTheAddOn_DiscountsBothLines()
    {
        var (plan, addOn) = PromotionPricing.Apply(Make(PromotionKind.Percent, 30, coversAddOn: true), 29900, 9900);

        plan.Should().Be(20930);
        addOn.Should().Be(6930);
    }

    [Fact]
    public void Apply_PercentNotCoveringTheAddOn_LeavesTheAddOnAtFullPrice()
    {
        var (plan, addOn) = PromotionPricing.Apply(Make(PromotionKind.Percent, 30, coversAddOn: false), 29900, 9900);

        plan.Should().Be(20930);
        addOn.Should().Be(9900);
    }

    [Fact]
    public void Apply_HundredPercent_IsFree()
    {
        var (plan, addOn) = PromotionPricing.Apply(Make(PromotionKind.Percent, 100, coversAddOn: true), 29900, 9900);

        plan.Should().Be(0);
        addOn.Should().Be(0);
    }

    [Fact]
    public void Apply_PlanWithoutAnAddOn_ReturnsNullForTheAddOn()
    {
        var (_, addOn) = PromotionPricing.Apply(Make(PromotionKind.Percent, 10, coversAddOn: true), 29900, null);

        addOn.Should().BeNull();
    }

    [Fact]
    public void Apply_PercentRoundsHalfAwayFromZero()
    {
        var (plan, _) = PromotionPricing.Apply(Make(PromotionKind.Percent, 15, coversAddOn: false), 1010, null);

        plan.Should().Be(858, "15 % of 1010 is 151.5, which rounds up to 152 off");
    }

    [Fact]
    public void Apply_FixedAmountComesOffThePlanFirst_ThenTheAddOn()
    {
        var (plan, addOn) = PromotionPricing.Apply(Make(PromotionKind.FixedAmount, 30000, coversAddOn: true), 29900, 9900);

        plan.Should().Be(0);
        addOn.Should().Be(9800);
    }

    [Fact]
    public void Apply_FixedAmountNeverGoesBelowZero()
    {
        var (plan, addOn) = PromotionPricing.Apply(Make(PromotionKind.FixedAmount, 90000, coversAddOn: true), 29900, 9900);

        plan.Should().Be(0);
        addOn.Should().Be(0);
    }

    private static Promotion Covering(PromotionKind kind, long value, bool coversAddOn, string plan = "pro", string? currency = null) => new()
    {
        Name = "Promotion",
        Kind = kind,
        Value = value,
        Currency = currency,
        CoversSecondRoleAddOn = coversAddOn,
        PlanCodes = [plan],
        StripeCouponId = "coupon",
    };

    [Fact]
    public void ForSubscription_PercentOnAnUnrestrictedCoupon_DiscountsPlanAndAddOn()
    {
        var (discount, total) = PromotionPricing.ForSubscription(
            Covering(PromotionKind.Percent, 30, coversAddOn: true), "pro", "CZK", 29900, 9900);

        discount.Should().Be(11940);
        total.Should().Be(27860);
    }

    [Fact]
    public void ForSubscription_PercentOnARestrictedCoupon_DiscountsOnlyThePlanLine()
    {
        var (discount, total) = PromotionPricing.ForSubscription(
            Covering(PromotionKind.Percent, 30, coversAddOn: false), "pro", "CZK", 29900, 9900);

        discount.Should().Be(8970);
        total.Should().Be(30830);
    }

    [Fact]
    public void ForSubscription_WithoutAnAddOn_UsesThePlanLineOnly()
    {
        var (discount, total) = PromotionPricing.ForSubscription(
            Covering(PromotionKind.Percent, 30, coversAddOn: false), "pro", "CZK", 29900, null);

        discount.Should().Be(8970);
        total.Should().Be(20930);
    }

    [Fact]
    public void ForSubscription_ARestrictedCouponThatDoesNotCoverThePlan_TakesNothingOff()
    {
        var (discount, total) = PromotionPricing.ForSubscription(
            Covering(PromotionKind.Percent, 30, coversAddOn: false, plan: "start"), "pro", "CZK", 29900, 9900);

        discount.Should().Be(0);
        total.Should().Be(39800);
    }

    [Fact]
    public void ForSubscription_AnUnrestrictedCouponDiscountsAPlanThatIsNotListed()
    {
        var (discount, _) = PromotionPricing.ForSubscription(
            Covering(PromotionKind.Percent, 30, coversAddOn: true, plan: "start"), "pro", "CZK", 29900, null);

        discount.Should().Be(8970, "an unrestricted coupon discounts whatever the coach buys");
    }

    [Fact]
    public void ForSubscription_FixedAmountComesOffPlanThenAddOn_AndNeverBelowZero()
    {
        var promotion = Covering(PromotionKind.FixedAmount, 30000, coversAddOn: true, currency: "CZK");

        var (discount, total) = PromotionPricing.ForSubscription(promotion, "pro", "czk", 29900, 9900);

        discount.Should().Be(30000);
        total.Should().Be(9800);
    }

    [Fact]
    public void ForSubscription_FixedAmountInAnotherCurrency_TakesNothingOff()
    {
        var promotion = Covering(PromotionKind.FixedAmount, 30000, coversAddOn: true, currency: "CZK");

        var (discount, total) = PromotionPricing.ForSubscription(promotion, "pro", "EUR", 1900, null);

        discount.Should().Be(0);
        total.Should().Be(1900);
    }

    [Fact]
    public void Apply_FixedAmountNotCoveringTheAddOn_StaysOnThePlanLine()
    {
        var (plan, addOn) = PromotionPricing.Apply(Make(PromotionKind.FixedAmount, 30000, coversAddOn: false), 29900, 9900);

        plan.Should().Be(0);
        addOn.Should().Be(9900);
    }
}
