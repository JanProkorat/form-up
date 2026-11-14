using FormUp.Application.Infrastructure.Services.Billing;
using FluentAssertions;

namespace FormUp.Tests.Services.Billing;

/// <summary>
/// A given-free end date and a price change at renewal share one schedule, so their phases must merge: neither
/// may overwrite the other.
/// </summary>
public class StripeSchedulePlannerTests
{
    private const string Coupon = "formup-gift-100";

    private static readonly DateTimeOffset Start = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Renewal = Start.AddMonths(1);
    private static readonly string[] OldPrices = ["price_old"];
    private static readonly string[] NewPrices = ["price_new", "price_addon"];

    [Fact]
    public void Build_GiftWithoutEndAndNoPriceChange_NeedsNoSchedule()
    {
        var phases = StripeSchedulePlanner.Build(Start, Renewal, OldPrices, new ScheduleIntent(null, Coupon, null));

        phases.Should().BeEmpty("a plain subscription discount covers a gift that never ends");
    }

    [Fact]
    public void Build_PriceChangeOnly_SwitchesPricesAtRenewal()
    {
        var phases = StripeSchedulePlanner.Build(Start, Renewal, OldPrices, new ScheduleIntent(NewPrices, null, null));

        phases.Should().Equal(
            new SchedulePhasePlan(Start, Renewal, OldPrices, null),
            new SchedulePhasePlan(Renewal, null, NewPrices, null));
    }

    [Fact]
    public void Build_GiftWithEndOnly_ClearsTheDiscountOnTheEndDate()
    {
        var giftEnd = Start.AddMonths(3).AddDays(4);

        var phases = StripeSchedulePlanner.Build(Start, Renewal, OldPrices, new ScheduleIntent(null, Coupon, giftEnd));

        phases.Should().Equal(
            new SchedulePhasePlan(Start, giftEnd, OldPrices, Coupon),
            new SchedulePhasePlan(giftEnd, null, OldPrices, null));
    }

    [Fact]
    public void Build_GiftEndsBeforeRenewalAndPriceChanges_MergesIntoThreePhases()
    {
        var giftEnd = Start.AddDays(10);

        var phases = StripeSchedulePlanner.Build(Start, Renewal, OldPrices, new ScheduleIntent(NewPrices, Coupon, giftEnd));

        phases.Should().Equal(
            new SchedulePhasePlan(Start, giftEnd, OldPrices, Coupon),
            new SchedulePhasePlan(giftEnd, Renewal, OldPrices, null),
            new SchedulePhasePlan(Renewal, null, NewPrices, null));
    }

    [Fact]
    public void Build_GiftEndsAfterRenewalAndPriceChanges_KeepsTheGiftOnTheNewPrice()
    {
        var giftEnd = Renewal.AddMonths(2);

        var phases = StripeSchedulePlanner.Build(Start, Renewal, OldPrices, new ScheduleIntent(NewPrices, Coupon, giftEnd));

        phases.Should().Equal(
            new SchedulePhasePlan(Start, Renewal, OldPrices, Coupon),
            new SchedulePhasePlan(Renewal, giftEnd, NewPrices, Coupon),
            new SchedulePhasePlan(giftEnd, null, NewPrices, null));
    }

    [Fact]
    public void Build_GiftEndsOnRenewalAndPriceChanges_MergesIntoTwoPhases()
    {
        var phases = StripeSchedulePlanner.Build(Start, Renewal, OldPrices, new ScheduleIntent(NewPrices, Coupon, Renewal));

        phases.Should().Equal(
            new SchedulePhasePlan(Start, Renewal, OldPrices, Coupon),
            new SchedulePhasePlan(Renewal, null, NewPrices, null));
    }

    [Fact]
    public void Build_GiftWithoutEndAndPriceChange_CarriesTheCouponThroughBothPhases()
    {
        var phases = StripeSchedulePlanner.Build(Start, Renewal, OldPrices, new ScheduleIntent(NewPrices, Coupon, null));

        phases.Should().Equal(
            new SchedulePhasePlan(Start, Renewal, OldPrices, Coupon),
            new SchedulePhasePlan(Renewal, null, NewPrices, Coupon));
    }

    [Fact]
    public void Build_GiftAlreadyEnded_IsIgnored()
    {
        var phases = StripeSchedulePlanner.Build(
            Start, Renewal, OldPrices, new ScheduleIntent(NewPrices, Coupon, Start.AddDays(-1)));

        phases.Should().Equal(
            new SchedulePhasePlan(Start, Renewal, OldPrices, null),
            new SchedulePhasePlan(Renewal, null, NewPrices, null));
    }

    [Theory]
    [InlineData(10, true)]
    [InlineData(10, false)]
    [InlineData(90, true)]
    [InlineData(90, false)]
    [InlineData(null, true)]
    public void Read_AfterBuild_ReturnsTheSameIntent(int? giftEndDays, bool withPriceChange)
    {
        DateTimeOffset? giftEnd = giftEndDays is null ? null : Start.AddDays(giftEndDays.Value);
        var intent = new ScheduleIntent(withPriceChange ? NewPrices : null, Coupon, giftEnd);

        var phases = StripeSchedulePlanner.Build(Start, Renewal, OldPrices, intent);

        StripeSchedulePlanner.Read(phases).Should().BeEquivalentTo(intent);
    }

    [Fact]
    public void Read_NoPhases_ReturnsAnEmptyIntent()
    {
        StripeSchedulePlanner.Read([]).Should().Be(new ScheduleIntent(null, null, null));
    }
}
