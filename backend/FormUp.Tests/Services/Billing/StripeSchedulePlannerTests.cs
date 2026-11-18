using FormUp.Application.Infrastructure.Services.Billing;
using FluentAssertions;

namespace FormUp.Tests.Services.Billing;

/// <summary>
/// The schedule exists only to bill a different price from the next renewal, optionally ending the subscription there.
/// </summary>
public class StripeSchedulePlannerTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Renewal = Start.AddMonths(1);
    private static readonly string[] OldPrices = ["price_old"];
    private static readonly string[] NewPrices = ["price_new", "price_addon"];

    [Fact]
    public void Build_NoPriceChange_NeedsNoSchedule()
    {
        StripeSchedulePlanner.Build(Start, Renewal, OldPrices, new ScheduleIntent(null)).Should().BeEmpty();
    }

    [Fact]
    public void Build_PriceChange_SwitchesPricesAtRenewal()
    {
        var phases = StripeSchedulePlanner.Build(Start, Renewal, OldPrices, new ScheduleIntent(NewPrices));

        phases.Should().Equal(
            new SchedulePhasePlan(Start, Renewal, OldPrices),
            new SchedulePhasePlan(Renewal, null, NewPrices));
    }

    [Fact]
    public void Build_CancelledRenewalWithAPriceChange_EndsInTheCurrentPhase()
    {
        var phases = StripeSchedulePlanner.Build(Start, Renewal, OldPrices, new ScheduleIntent(NewPrices, CancelRenewal: true));

        phases.Should().Equal(new SchedulePhasePlan(Start, Renewal, OldPrices));
    }

    [Fact]
    public void Build_CancelledRenewalAndNoPriceChange_NeedsNoSchedule()
    {
        StripeSchedulePlanner.Build(Start, Renewal, OldPrices, new ScheduleIntent(null, CancelRenewal: true))
            .Should().BeEmpty("the subscription's own cancel flag covers it");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Read_AfterBuild_ReturnsTheSameIntent(bool cancelRenewal)
    {
        var intent = new ScheduleIntent(NewPrices, cancelRenewal);

        var phases = StripeSchedulePlanner.Build(Start, Renewal, OldPrices, intent);

        // A cancelling build drops the price change, so only the flag survives the round trip.
        StripeSchedulePlanner.Read(phases, cancelRenewal).Should().Be(
            cancelRenewal ? new ScheduleIntent(null, true) : intent, "a price change is read back from the later phase");
    }

    [Fact]
    public void Read_NoPhases_ReturnsAnEmptyIntent()
    {
        StripeSchedulePlanner.Read([]).Should().Be(new ScheduleIntent(null));
    }
}
