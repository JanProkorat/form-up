using FluentAssertions;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Services;

namespace FormUp.Tests.Domain.Services;

/// <summary>
/// The status buckets of <see cref="CoachSubscriptionStatusClassifier"/>, evaluated in memory from the same expressions
/// SQL runs: every coach lands in exactly one of Paying, Free and GivenFree, and the single status is the most
/// specific bucket. The SQL translation is covered by the list endpoint tests.
/// </summary>
public class CoachSubscriptionStatusClassifierTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 12, 0, 0, TimeSpan.Zero);

    private static ProfessionalProfile Coach(
        string? planCode,
        SubscriptionStatus status = SubscriptionStatus.Active,
        int? givenFreeDaysAgo = null,
        int? freeUntilDays = null,
        bool cancelAtPeriodEnd = false)
    {
        var profile = new ProfessionalProfile { Id = 1 };

        if (planCode is not null)
        {
            profile.CoachSubscription = new CoachSubscription
            {
                ProfessionalProfileId = 1,
                SubscriptionPlanId = 1,
                SubscriptionPlanPriceId = 1,
                SubscriptionPlan = new SubscriptionPlan
                {
                    Code = planCode,
                    NameCs = planCode,
                    NameEn = planCode,
                    NameDe = planCode,
                    Currency = "CZK",
                },
                Status = status,
                GivenFreeAt = givenFreeDaysAgo is { } ago ? Now.AddDays(-ago) : null,
                FreeUntil = freeUntilDays is { } days ? Now.AddDays(days) : null,
                CancelAtPeriodEnd = cancelAtPeriodEnd,
            };
        }

        return profile;
    }

    public static TheoryData<string, ProfessionalProfile, AdminCoachStatus> Cases => new()
    {
        { "no subscription row", Coach(null), AdminCoachStatus.Free },
        { "Free plan, Active", Coach("free"), AdminCoachStatus.Free },
        { "paid plan, Active", Coach("pro"), AdminCoachStatus.Paying },
        { "paid plan, Trialing", Coach("pro", SubscriptionStatus.Trialing), AdminCoachStatus.Paying },
        { "paid plan, PastDue", Coach("pro", SubscriptionStatus.PastDue), AdminCoachStatus.PaymentFailed },
        { "paid plan, PastDue and renewal cancelled", Coach("pro", SubscriptionStatus.PastDue, cancelAtPeriodEnd: true), AdminCoachStatus.PaymentFailed },
        { "paid plan, Active and renewal cancelled", Coach("pro", cancelAtPeriodEnd: true), AdminCoachStatus.Cancelling },
        { "paid plan, Canceled", Coach("pro", SubscriptionStatus.Canceled), AdminCoachStatus.Free },
        { "paid plan, Incomplete", Coach("pro", SubscriptionStatus.Incomplete), AdminCoachStatus.Free },
        { "gift without an end", Coach("pro", givenFreeDaysAgo: 3), AdminCoachStatus.GivenFree },
        { "gift ending in the future", Coach("pro", givenFreeDaysAgo: 3, freeUntilDays: 30), AdminCoachStatus.GivenFree },
        { "gift that ended, status still Active", Coach("pro", givenFreeDaysAgo: 30, freeUntilDays: -1), AdminCoachStatus.Free },
        { "gift in force, status PastDue", Coach("pro", SubscriptionStatus.PastDue, givenFreeDaysAgo: 3, freeUntilDays: 30), AdminCoachStatus.GivenFree },
        { "gift in force, renewal cancelled", Coach("pro", givenFreeDaysAgo: 3, cancelAtPeriodEnd: true), AdminCoachStatus.GivenFree },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void ProjectStatus_ClassifiesEveryCoachIntoTheMostSpecificBucket(
        string scenario, ProfessionalProfile coach, AdminCoachStatus expected)
    {
        var projected = CoachSubscriptionStatusClassifier.ProjectStatus(Now).Compile()(coach);

        projected.Status.Should().Be(expected, scenario);
        projected.ProfessionalProfileId.Should().Be(1);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Matches_EachFilterAgreesWithTheSingleStatus(string scenario, ProfessionalProfile coach, AdminCoachStatus expected)
    {
        bool Matches(AdminCoachStatusFilter filter) =>
            CoachSubscriptionStatusClassifier.Matches(filter, Now).Compile()(coach);

        Matches(AdminCoachStatusFilter.All).Should().BeTrue(scenario);
        Matches(AdminCoachStatusFilter.Paying).Should().Be(
            expected is AdminCoachStatus.Paying or AdminCoachStatus.PaymentFailed or AdminCoachStatus.Cancelling, scenario);
        Matches(AdminCoachStatusFilter.PaymentFailed).Should().Be(expected == AdminCoachStatus.PaymentFailed, scenario);
        Matches(AdminCoachStatusFilter.Cancelling).Should().Be(expected == AdminCoachStatus.Cancelling, scenario);
        Matches(AdminCoachStatusFilter.Free).Should().Be(expected == AdminCoachStatus.Free, scenario);
        Matches(AdminCoachStatusFilter.GivenFree).Should().Be(expected == AdminCoachStatus.GivenFree, scenario);

        var exclusiveBuckets = new[] { AdminCoachStatusFilter.Paying, AdminCoachStatusFilter.Free, AdminCoachStatusFilter.GivenFree }
            .Count(Matches);
        exclusiveBuckets.Should().Be(1, "Paying, Free and Given free split every coach exactly once: " + scenario);
    }

    [Theory]
    [InlineData(SubscriptionStatus.Active, null, null, true)]
    [InlineData(SubscriptionStatus.PastDue, null, null, true)]
    [InlineData(SubscriptionStatus.Canceled, null, null, false)]
    [InlineData(SubscriptionStatus.Canceled, 1, null, true)]
    [InlineData(SubscriptionStatus.Active, null, 3, true)]
    [InlineData(SubscriptionStatus.Active, -1, 3, false)]
    [InlineData(SubscriptionStatus.Canceled, null, 3, true)]
    public void PlanApplies_FollowsTheStatusUnlessTheGiftSetsTheEnd(
        SubscriptionStatus status, int? freeUntilDays, int? givenFreeDaysAgo, bool expected)
    {
        var freeUntil = freeUntilDays is { } days ? Now.AddDays(days) : (DateTimeOffset?)null;
        var givenFreeAt = givenFreeDaysAgo is { } ago ? Now.AddDays(-ago) : (DateTimeOffset?)null;

        CoachSubscriptionStatusClassifier.PlanApplies(status, freeUntil, givenFreeAt, Now).Should().Be(expected);
    }
}
