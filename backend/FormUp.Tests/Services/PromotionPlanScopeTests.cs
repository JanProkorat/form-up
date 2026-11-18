using FluentAssertions;
using FormUp.Application.Domain.Services;

namespace FormUp.Tests.Services;

/// <summary>
/// Unit tests for <see cref="PromotionPlanScope.Resolve"/>: which plans a promotion covers and whether the coupon may stay
/// unrestricted. The plan set is passed in, so nothing depends on other tests' data.
/// </summary>
public class PromotionPlanScopeTests
{
    private static readonly PromotionPlanOption Start = new("start", "price_start");

    private static readonly PromotionPlanOption Pro = new("pro", "price_pro");

    private static readonly PromotionPlanOption Studio = new("studio", "price_studio");

    private static readonly PromotionPlanOption Unpriced = new("legacy", null);

    private static readonly PromotionPlanOption EmptyPrice = new("blank", "");

    [Fact]
    public void Resolve_EveryPurchasablePlanSelected_LeavesTheCouponUnrestricted()
    {
        var result = PromotionPlanScope.Resolve([Start, Pro, Studio], ["start", "pro", "studio"]);

        result.Should().NotBeNull();
        result!.Value.CoversEveryPurchasablePlan.Should().BeTrue();
        result.Value.Selected.Should().BeEquivalentTo([Start, Pro, Studio]);
    }

    [Fact]
    public void Resolve_OnlySomePlansSelected_RestrictsTheCouponToThem()
    {
        var result = PromotionPlanScope.Resolve([Start, Pro, Studio], ["pro", "studio"]);

        result!.Value.CoversEveryPurchasablePlan.Should().BeFalse();
        result.Value.Selected.Should().BeEquivalentTo([Pro, Studio]);
    }

    [Fact]
    public void Resolve_APlanWithoutAProviderPrice_DoesNotCountAsPurchasable_SoItNeedNotBeSelected()
    {
        var result = PromotionPlanScope.Resolve([Start, Pro, Unpriced, EmptyPrice], ["start", "pro"]);

        result!.Value.CoversEveryPurchasablePlan.Should().BeTrue("the unpriced plans cannot be bought at Checkout");
    }

    [Fact]
    public void Resolve_AnUnpricedPlanTogetherWithEveryPurchasableOne_IsAccepted()
    {
        var result = PromotionPlanScope.Resolve([Start, Pro, Unpriced], ["start", "pro", "legacy"]);

        result!.Value.CoversEveryPurchasablePlan.Should().BeTrue();
        result.Value.Selected.Should().HaveCount(3);
    }

    [Fact]
    public void Resolve_AnUnpricedPlanInASubset_IsRejected_BecauseTheCouponCannotBeRestrictedToIt()
    {
        PromotionPlanScope.Resolve([Start, Pro, Unpriced], ["start", "legacy"]).Should().BeNull();
    }

    [Fact]
    public void Resolve_WhenNothingIsPurchasable_AnUnpricedSelectionIsRejected()
    {
        PromotionPlanScope.Resolve([Unpriced], ["legacy"]).Should().BeNull("an empty purchasable set must not count as covered");
    }

    [Fact]
    public void Resolve_AnUnknownCode_IsRejected()
    {
        PromotionPlanScope.Resolve([Start, Pro], ["start", "nope"]).Should().BeNull();
    }

    [Fact]
    public void Resolve_IgnoresCaseAndWhitespace_AndCollapsesDuplicates()
    {
        var result = PromotionPlanScope.Resolve([Start, Pro], [" START ", "start", "Pro"]);

        result!.Value.Selected.Should().BeEquivalentTo([Start, Pro]);
        result.Value.CoversEveryPurchasablePlan.Should().BeTrue();
    }

    [Fact]
    public void Resolve_AnOverlongCodeInTheCatalogue_DoesNotAffectTheDecision()
    {
        var overlong = new PromotionPlanOption($"plan-{new string('x', 60)}", null);

        var result = PromotionPlanScope.Resolve([Start, Pro, overlong], ["start", "pro"]);

        result!.Value.CoversEveryPurchasablePlan.Should().BeTrue();
    }
}
