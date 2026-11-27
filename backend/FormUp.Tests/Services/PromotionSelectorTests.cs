using FluentAssertions;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Services;

namespace FormUp.Tests.Services;

/// <summary>
/// Unit tests for <see cref="PromotionSelector.Pick"/>: which automatic promotion wins when several overlap.
/// </summary>
public class PromotionSelectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    private static readonly IReadOnlySet<long> NoRedemptions = new HashSet<long>();

    private static Promotion Automatic(long id, DateTimeOffset claimFrom, Action<Promotion>? configure = null)
    {
        var promotion = new Promotion
        {
            Id = id,
            Name = $"P{id}",
            Mode = PromotionMode.Automatic,
            ClaimFrom = claimFrom,
            PlanCodes = ["pro"],
            StripeCouponId = $"coupon_{id}",
        };
        configure?.Invoke(promotion);

        return promotion;
    }

    private static Promotion? Pick(IEnumerable<(Promotion, int)> candidates, IReadOnlySet<long>? redeemed = null, bool isNewCoach = true) =>
        PromotionSelector.Pick(candidates, redeemed ?? NoRedemptions, isNewCoach, Now);

    [Fact]
    public void Pick_WhenSeveralOverlap_TheLatestClaimFromWins()
    {
        var launch = Automatic(1, Now.AddDays(-20));
        var blackFriday = Automatic(2, Now.AddDays(-2));

        Pick([(launch, 0), (blackFriday, 0)]).Should().BeSameAs(blackFriday);
    }

    [Fact]
    public void Pick_WhenClaimFromTies_TheLowestIdWins()
    {
        var first = Automatic(5, Now.AddDays(-3));
        var second = Automatic(9, Now.AddDays(-3));

        Pick([(second, 0), (first, 0)]).Should().BeSameAs(first);
    }

    [Fact]
    public void Pick_SkipsScheduledEndedAndUsedUpPromotions()
    {
        var scheduled = Automatic(1, Now.AddDays(1));
        var closed = Automatic(2, Now.AddDays(-9), p => p.ClaimUntil = Now.AddDays(-1));
        var usedUp = Automatic(3, Now.AddDays(-8), p => p.MaxUses = 10);
        var open = Automatic(4, Now.AddDays(-30));

        Pick([(scheduled, 0), (closed, 0), (usedUp, 10), (open, 0)]).Should().BeSameAs(open);
    }

    [Fact]
    public void Pick_SkipsAPromotionAnAdminEnded_SoTheNextWinnerApplies()
    {
        var ended = Automatic(1, Now.AddDays(-1), p => p.EndedAt = Now.AddHours(-1));
        var open = Automatic(2, Now.AddDays(-30));

        Pick([(ended, 0), (open, 0)]).Should().BeSameAs(open);
    }

    [Fact]
    public void Pick_SkipsCodePromotions()
    {
        var code = Automatic(1, Now.AddDays(-1), p => p.Mode = PromotionMode.Code);

        Pick([(code, 0)]).Should().BeNull();
    }

    [Fact]
    public void Pick_NewCoachesOnly_IsSkippedForACoachWhoPaidBefore_SoTheNextWinnerApplies()
    {
        var newOnly = Automatic(1, Now.AddDays(-1), p => p.NewCoachesOnly = true);
        var everyone = Automatic(2, Now.AddDays(-10));

        Pick([(newOnly, 0), (everyone, 0)], isNewCoach: false).Should().BeSameAs(everyone);
        Pick([(newOnly, 0), (everyone, 0)], isNewCoach: true).Should().BeSameAs(newOnly);
    }

    [Fact]
    public void Pick_OneUsePerCoach_IsSkippedOnceTheCoachRedeemedIt()
    {
        var once = Automatic(1, Now.AddDays(-1), p => p.OneUsePerCoach = true);
        var everyone = Automatic(2, Now.AddDays(-10));

        Pick([(once, 1), (everyone, 0)], redeemed: new HashSet<long> { 1 }).Should().BeSameAs(everyone);
    }

    [Fact]
    public void Pick_WithoutCandidates_ReturnsNull() => Pick([]).Should().BeNull();
}
