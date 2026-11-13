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

    [Fact]
    public void Resolve_PeriodEndInThePast_ReturnsNow() =>
        CoachAccountActiveUntilResolver.Resolve(
                Row(SubscriptionStatus.Active, new DateTimeOffset(Now.AddDays(-1))), Now)
            .Should().Be(Now);
}
