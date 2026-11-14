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
    [InlineData("start", true)]
    [InlineData("PRO", true)]
    [InlineData("studio", false)]
    public void CoversPlan_ComparesPlanCodesIgnoringCase(string planCode, bool expected) =>
        PromotionRules.CoversPlan(Make(Now), planCode).Should().Be(expected);
}
