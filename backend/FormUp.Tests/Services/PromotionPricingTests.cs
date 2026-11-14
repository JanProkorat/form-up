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

    [Fact]
    public void Apply_FixedAmountNotCoveringTheAddOn_StaysOnThePlanLine()
    {
        var (plan, addOn) = PromotionPricing.Apply(Make(PromotionKind.FixedAmount, 30000, coversAddOn: false), 29900, 9900);

        plan.Should().Be(0);
        addOn.Should().Be(9900);
    }
}
