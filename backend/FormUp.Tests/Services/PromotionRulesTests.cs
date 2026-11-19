using FluentAssertions;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Services;

namespace FormUp.Tests.Services;

/// <summary>
/// Unit tests for <see cref="PromotionRules"/>: the derived status and plan coverage.
/// </summary>
public class PromotionRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    private static Promotion Make(DateTimeOffset claimFrom, DateTimeOffset? claimUntil = null, int? maxUses = null) => new()
    {
        Name = "Promotion",
        ClaimFrom = claimFrom,
        ClaimUntil = claimUntil,
        MaxUses = maxUses,
        PlanCodes = ["Start", "pro"],
        StripeCouponId = "coupon",
    };

    [Fact]
    public void StatusAt_BeforeTheClaimWindow_IsScheduled() =>
        PromotionRules.StatusAt(Make(Now.AddDays(1)), 0, Now).Should().Be(PromotionStatus.Scheduled);

    [Fact]
    public void StatusAt_InsideTheWindowWithUsesLeft_IsActive() =>
        PromotionRules.StatusAt(Make(Now.AddDays(-1), Now.AddDays(1), maxUses: 5), 4, Now).Should().Be(PromotionStatus.Active);

    [Fact]
    public void StatusAt_WithNoEndDate_StaysActive() =>
        PromotionRules.StatusAt(Make(Now.AddYears(-1)), 1000, Now).Should().Be(PromotionStatus.Active);

    [Fact]
    public void StatusAt_AfterTheClaimWindow_IsEnded() =>
        PromotionRules.StatusAt(Make(Now.AddDays(-10), Now.AddSeconds(-1)), 0, Now).Should().Be(PromotionStatus.Ended);

    [Fact]
    public void StatusAt_WhenMaxUsesAreReached_IsEnded() =>
        PromotionRules.StatusAt(Make(Now.AddDays(-1), maxUses: 5), 5, Now).Should().Be(PromotionStatus.Ended);

    [Fact]
    public void StatusAt_ScheduledPromotionAlreadyUsedUp_IsEnded() =>
        PromotionRules.StatusAt(Make(Now.AddDays(1), maxUses: 1), 1, Now).Should().Be(PromotionStatus.Ended);

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void StatusAt_WhenAnAdminEndedIt_IsEndedWhateverTheWindow(int claimFromDays)
    {
        var promotion = Make(Now.AddDays(claimFromDays));
        promotion.EndedAt = Now.AddHours(-1);

        PromotionRules.StatusAt(promotion, 0, Now).Should().Be(PromotionStatus.Ended);
    }

    [Theory]
    [InlineData("start", true)]
    [InlineData("PRO", true)]
    [InlineData("studio", false)]
    public void CoversPlan_ComparesPlanCodesIgnoringCase(string planCode, bool expected) =>
        PromotionRules.CoversPlan(Make(Now), planCode).Should().Be(expected);

    [Theory]
    [InlineData(PromotionBillingPeriod.Both, BillingInterval.Monthly, true)]
    [InlineData(PromotionBillingPeriod.Both, BillingInterval.Annual, true)]
    [InlineData(PromotionBillingPeriod.Monthly, BillingInterval.Monthly, true)]
    [InlineData(PromotionBillingPeriod.Monthly, BillingInterval.Annual, false)]
    [InlineData(PromotionBillingPeriod.Annual, BillingInterval.Monthly, false)]
    [InlineData(PromotionBillingPeriod.Annual, BillingInterval.Annual, true)]
    public void CoversInterval_FollowsTheBillingPeriod(
        PromotionBillingPeriod period, BillingInterval interval, bool expected)
    {
        var promotion = Make(Now);
        promotion.BillingPeriod = period;

        PromotionRules.CoversInterval(promotion, interval).Should().Be(expected);
    }

    [Fact]
    public void BillingPeriod_DefaultsToBoth_SoARowFromBeforeTheColumnStillCoversEverything() =>
        Make(Now).BillingPeriod.Should().Be(PromotionBillingPeriod.Both);

    [Fact]
    public void CouponIdFor_BothWithAnAnnualCoupon_UsesTheAnnualCouponForAnnualAndTheOtherOneForMonthly()
    {
        var promotion = Make(Now);
        promotion.StripeAnnualCouponId = "coupon_annual";

        PromotionRules.CouponIdFor(promotion, BillingInterval.Annual).Should().Be("coupon_annual");
        PromotionRules.CouponIdFor(promotion, BillingInterval.Monthly).Should().Be("coupon");
    }

    [Fact]
    public void CouponIdFor_BothWithOneCoupon_UsesItForEveryInterval()
    {
        var promotion = Make(Now);

        PromotionRules.CouponIdFor(promotion, BillingInterval.Annual).Should().Be("coupon");
        PromotionRules.CouponIdFor(promotion, BillingInterval.Monthly).Should().Be("coupon");
    }

    [Fact]
    public void CouponIdFor_AnIntervalThePromotionDoesNotCover_IsNull_SoNoCouponIsEverAttached()
    {
        var monthlyOnly = Make(Now);
        monthlyOnly.BillingPeriod = PromotionBillingPeriod.Monthly;
        var annualOnly = Make(Now);
        annualOnly.BillingPeriod = PromotionBillingPeriod.Annual;

        PromotionRules.CouponIdFor(monthlyOnly, BillingInterval.Annual).Should().BeNull();
        PromotionRules.CouponIdFor(annualOnly, BillingInterval.Monthly).Should().BeNull();
        PromotionRules.CouponIdFor(annualOnly, BillingInterval.Annual).Should().Be("coupon", "an annual-only promotion keeps its annual-shaped coupon in the single slot");
    }

    [Theory]
    [InlineData(PromotionBillingPeriod.Both, PromotionDuration.FirstMonth, true)]
    [InlineData(PromotionBillingPeriod.Both, PromotionDuration.Months, true)]
    [InlineData(PromotionBillingPeriod.Both, PromotionDuration.Forever, false)]
    [InlineData(PromotionBillingPeriod.Monthly, PromotionDuration.Months, false)]
    [InlineData(PromotionBillingPeriod.Annual, PromotionDuration.FirstMonth, false)]
    public void NeedsAnnualCoupon_OnlyWhenBothPeriodsAreCoveredAndTheDiscountIsNotForever(
        PromotionBillingPeriod period, PromotionDuration duration, bool expected) =>
        PromotionRules.NeedsAnnualCoupon(period, duration).Should().Be(expected);
}
