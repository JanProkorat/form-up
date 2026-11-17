using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Services;
using FluentAssertions;

namespace FormUp.Tests.Domain.Services;

public class CoachAccountActiveUntilResolverTests
{
    private static readonly DateTime Now = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

    private static CoachSubscription Row(
        SubscriptionStatus status, DateTimeOffset? periodEnd, DateTimeOffset? trialEnd = null) => new()
    {
        ProfessionalProfileId = 1,
        SubscriptionPlanId = 1,
        SubscriptionPlanPriceId = 1,
        Status = status,
        CurrentPeriodEndsAt = periodEnd,
        TrialEndsAt = trialEnd,
    };

    [Fact]
    public void Resolve_NoRow_ReturnsNow() =>
        CoachAccountActiveUntilResolver.Resolve(null, Now).Should().Be(Now);

    [Fact]
    public void Resolve_PaidRowWithFuturePeriodEnd_ReturnsPeriodEnd()
    {
        var periodEnd = new DateTimeOffset(Now.AddDays(10));

        CoachAccountActiveUntilResolver.Resolve(Row(SubscriptionStatus.Active, periodEnd), Now)
            .Should().Be(periodEnd.UtcDateTime);
    }

    [Fact]
    public void Resolve_TrialingRow_ReturnsTrialEndNotPeriodEnd()
    {
        var trialEnd = new DateTimeOffset(Now.AddDays(3));

        CoachAccountActiveUntilResolver.Resolve(
                Row(SubscriptionStatus.Trialing, new DateTimeOffset(Now.AddDays(30)), trialEnd), Now)
            .Should().Be(trialEnd.UtcDateTime);
    }

    [Fact]
    public void Resolve_ActiveRowWithoutPeriodEnd_ReturnsNow() =>
        CoachAccountActiveUntilResolver.Resolve(Row(SubscriptionStatus.Active, null), Now).Should().Be(Now);

    private static CoachSubscription GiftRow(
        SubscriptionStatus status,
        int? freeUntilDays,
        int? periodEndDays,
        bool liveSubscription,
        int? trialEndDays = null)
    {
        var row = Row(
            status,
            periodEndDays is { } period ? new DateTimeOffset(Now.AddDays(period)) : null,
            trialEndDays is { } trial ? new DateTimeOffset(Now.AddDays(trial)) : null);
        row.GivenFreeAt = new DateTimeOffset(Now.AddDays(-30));
        row.FreeUntil = freeUntilDays is { } free ? new DateTimeOffset(Now.AddDays(free)) : null;
        row.ExternalSubscriptionId = liveSubscription ? "sub_test" : null;
        return row;
    }

    [Theory]
    [InlineData(SubscriptionStatus.Active, 20, null, false, 20)] // rule 3: FreeUntil
    [InlineData(SubscriptionStatus.Active, null, null, false, 0)] // rule 4: no FreeUntil, nothing paid -> now
    [InlineData(SubscriptionStatus.Active, 20, 10, true, 20)] // rule 5: FreeUntil later than paid end
    [InlineData(SubscriptionStatus.Active, 10, 20, true, 20)] // rule 5: paid end later than FreeUntil
    [InlineData(SubscriptionStatus.Active, null, 15, true, 15)] // rule 5: no FreeUntil, live paid period
    [InlineData(SubscriptionStatus.Active, -5, null, false, 0)] // past FreeUntil -> now
    [InlineData(SubscriptionStatus.Active, -5, -2, true, 0)] // both in the past -> now
    [InlineData(SubscriptionStatus.Active, 10, 20, false, 10)] // paid end ignored without a live subscription
    [InlineData(SubscriptionStatus.Canceled, 12, 25, false, 12)] // stale period end on a lapsed, gifted row
    [InlineData(SubscriptionStatus.Canceled, null, 25, false, 0)] // stale period end, no FreeUntil -> now
    public void Resolve_GiftRow_ReturnsLaterOfFreeUntilAndLivePaidEnd(
        SubscriptionStatus status, int? freeUntilDays, int? periodEndDays, bool liveSubscription, int expectedDays)
    {
        var row = GiftRow(status, freeUntilDays, periodEndDays, liveSubscription);

        CoachAccountActiveUntilResolver.Resolve(row, Now).Should().Be(Now.AddDays(expectedDays));
    }

    [Fact]
    public void Resolve_GiftedTrialingRow_UsesStoredPeriodEndNotTrialEnd() =>
        CoachAccountActiveUntilResolver.Resolve(
                GiftRow(SubscriptionStatus.Trialing, freeUntilDays: null, periodEndDays: 30, liveSubscription: true, trialEndDays: 3),
                Now)
            .Should().Be(Now.AddDays(30));

    [Fact]
    public void Resolve_PeriodEndInThePast_ReturnsNow() =>
        CoachAccountActiveUntilResolver.Resolve(
                Row(SubscriptionStatus.Active, new DateTimeOffset(Now.AddDays(-1))), Now)
            .Should().Be(Now);
}
