using FluentAssertions;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using Microsoft.Extensions.Logging;
using MockQueryable.NSubstitute;
using NSubstitute;

namespace FormUp.Tests.Domain.Services;

/// <summary>
/// Resolution rules of <see cref="EntitlementService"/> over mocked sets: no row, missing Free plan,
/// every status, <c>FreeUntil</c>, second-role state, distinct client counting and the capacity check.
/// </summary>
public class EntitlementServiceResolutionTests
{
    private const long ProfileId = 7;
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    private static SubscriptionPlan Plan(
        long id,
        string code,
        bool canSendQuestionnaires = false,
        bool canUsePayments = false,
        bool canUsePackages = false,
        int? maxActiveClients = null,
        bool bothRolesIncluded = true) => new()
    {
        Id = id,
        Code = code,
        NameCs = code,
        NameEn = code,
        NameDe = code,
        Currency = "CZK",
        CanCreatePlans = true,
        CanSendQuestionnaires = canSendQuestionnaires,
        CanUsePayments = canUsePayments,
        CanUsePackages = canUsePackages,
        MaxActiveClients = maxActiveClients,
        BothRolesIncluded = bothRolesIncluded,
    };

    private static CoachSubscription Subscription(
        SubscriptionPlan plan,
        SubscriptionStatus status,
        long profileId = ProfileId,
        DateTimeOffset? freeUntil = null,
        bool paysSecondRoleAddOn = false,
        DateTimeOffset? givenFreeAt = null) => new()
    {
        ProfessionalProfileId = profileId,
        SubscriptionPlanId = plan.Id,
        SubscriptionPlanPriceId = 1,
        SubscriptionPlan = plan,
        Status = status,
        FreeUntil = freeUntil,
        GivenFreeAt = givenFreeAt,
        PaysSecondRoleAddOn = paysSecondRoleAddOn,
    };

    private static ClientProfessionalLink Link(long clientProfileId, bool isActive = true, long profileId = ProfileId) => new()
    {
        ClientProfileId = clientProfileId,
        ProfessionalProfileId = profileId,
        ProfessionalRole = UserRole.Trainer,
        ConnectionState = isActive ? ConnectionState.Connected : ConnectionState.Ended,
    };

    private static EntitlementService CreateService(
        IEnumerable<SubscriptionPlan> plans,
        IEnumerable<CoachSubscription> subscriptions,
        IEnumerable<ClientProfessionalLink>? links = null,
        ILogger<EntitlementService>? logger = null)
    {
        var plansSet = plans.ToList().BuildMockDbSet();
        var subscriptionsSet = subscriptions.ToList().BuildMockDbSet();
        var linksSet = (links ?? []).ToList().BuildMockDbSet();

        var db = Substitute.For<IApplicationDbContext>();
        db.SubscriptionPlans.Returns(plansSet);
        db.CoachSubscriptions.Returns(subscriptionsSet);
        db.ClientProfessionalLinks.Returns(linksSet);

        return new EntitlementService(
            db, new FixedTimeProvider(Now), logger ?? Substitute.For<ILogger<EntitlementService>>());
    }

    [Fact]
    public async Task GetEntitlementsAsync_NoSubscriptionRow_ResolvesTheFreePlan()
    {
        var free = Plan(1, "free", canUsePayments: true, maxActiveClients: 2);
        var service = CreateService([free], []);

        var entitlements = await service.GetEntitlementsAsync(ProfileId, TestContext.Current.CancellationToken);

        entitlements.CanUsePayments.Should().BeTrue();
        entitlements.CanSendQuestionnaires.Should().BeFalse();
        entitlements.MaxActiveClients.Should().Be(2);
    }

    [Fact]
    public async Task GetEntitlementsAsync_FreePlanRowMissing_ResolvesLockedAndLogsAnError()
    {
        var logger = Substitute.For<ILogger<EntitlementService>>();
        var service = CreateService([Plan(2, "pro")], [], logger: logger);

        var entitlements = await service.GetEntitlementsAsync(ProfileId, TestContext.Current.CancellationToken);

        entitlements.Should().Be(CoachEntitlements.Locked);
        logger.Received(1).Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<Arg.AnyType>(),
            Arg.Any<Exception?>(),
            Arg.Any<Func<Arg.AnyType, Exception?, string>>());
    }

    [Theory]
    [InlineData(SubscriptionStatus.Trialing, true)]
    [InlineData(SubscriptionStatus.Active, true)]
    [InlineData(SubscriptionStatus.PastDue, true)]
    [InlineData(SubscriptionStatus.Canceled, false)]
    [InlineData(SubscriptionStatus.Incomplete, false)]
    public async Task GetEntitlementsAsync_Status_KeepsThePlanOrFallsBackToFree(SubscriptionStatus status, bool keepsPlan)
    {
        var free = Plan(1, "free");
        var pro = Plan(2, "pro", canSendQuestionnaires: true, maxActiveClients: 30);
        var service = CreateService([free, pro], [Subscription(pro, status)]);

        var entitlements = await service.GetEntitlementsAsync(ProfileId, TestContext.Current.CancellationToken);

        entitlements.CanSendQuestionnaires.Should().Be(keepsPlan);
        entitlements.MaxActiveClients.Should().Be(keepsPlan ? 30 : null);
        entitlements.Should().NotBe(CoachEntitlements.Locked, "a canceled or incomplete subscription falls back to Free, not to Locked");
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(-1, false)]
    public async Task GetEntitlementsAsync_CanceledWithFreeUntil_KeepsThePlanOnlyBeforeTheDeadline(int daysFromNow, bool keepsPlan)
    {
        var free = Plan(1, "free");
        var pro = Plan(2, "pro", canSendQuestionnaires: true);
        var subscription = Subscription(pro, SubscriptionStatus.Canceled, freeUntil: Now.AddDays(daysFromNow));
        var service = CreateService([free, pro], [subscription]);

        var entitlements = await service.GetEntitlementsAsync(ProfileId, TestContext.Current.CancellationToken);

        entitlements.CanSendQuestionnaires.Should().Be(keepsPlan);
    }

    [Theory]
    [InlineData(null, SubscriptionStatus.Active, true)]
    [InlineData(null, SubscriptionStatus.Canceled, true)]
    [InlineData(1, SubscriptionStatus.Active, true)]
    [InlineData(-1, SubscriptionStatus.Active, true)]
    [InlineData(-1, SubscriptionStatus.PastDue, true)]
    public async Task GetEntitlementsAsync_GivenFreePlan_KeepsItsFlagsForReadsWhileAPlanChoiceIsPending(
        int? daysFromNow, SubscriptionStatus status, bool keepsPlan)
    {
        var free = Plan(1, "free");
        var pro = Plan(2, "pro", canSendQuestionnaires: true, maxActiveClients: 30);
        var gift = Subscription(
            pro, status, freeUntil: daysFromNow is { } days ? Now.AddDays(days) : null, givenFreeAt: Now.AddDays(-10));
        var service = CreateService([free, pro], [gift]);

        var entitlements = await service.GetEntitlementsAsync(ProfileId, TestContext.Current.CancellationToken);

        entitlements.CanSendQuestionnaires.Should().Be(keepsPlan);
        entitlements.MaxActiveClients.Should().Be(keepsPlan ? 30 : null);
    }

    [Theory]
    [InlineData(true, false, SubscriptionStatus.Active, SecondRoleState.Included)]
    [InlineData(false, true, SubscriptionStatus.Active, SecondRoleState.PaidAddOn)]
    [InlineData(false, false, SubscriptionStatus.Active, SecondRoleState.NotAvailable)]
    [InlineData(false, true, SubscriptionStatus.Canceled, SecondRoleState.Included)]
    public async Task GetSecondRoleStateAsync_ResolvesFromThePlanOrTheFreeFallback(
        bool planIncludesBothRoles, bool paysAddOn, SubscriptionStatus status, SecondRoleState expected)
    {
        var free = Plan(1, "free", bothRolesIncluded: true);
        var start = Plan(2, "start", bothRolesIncluded: planIncludesBothRoles);
        var service = CreateService([free, start], [Subscription(start, status, paysSecondRoleAddOn: paysAddOn)]);

        var state = await service.GetSecondRoleStateAsync(ProfileId, TestContext.Current.CancellationToken);

        state.Should().Be(expected);
    }

    [Fact]
    public async Task GetSecondRoleStateAsync_NoSubscriptionRow_FollowsTheFreePlan()
    {
        var service = CreateService([Plan(1, "free", bothRolesIncluded: true)], []);

        var state = await service.GetSecondRoleStateAsync(ProfileId, TestContext.Current.CancellationToken);

        state.Should().Be(SecondRoleState.Included);
    }

    [Fact]
    public async Task GetPackagesEntitledProfileIdsAsync_UsesTheSameResolutionAsTheSingleLookup()
    {
        var free = Plan(1, "free", canUsePackages: true);
        var noPackages = Plan(2, "no-packages", canUsePackages: false);
        var subscriptions = new[]
        {
            Subscription(noPackages, SubscriptionStatus.Active, profileId: 11),
            Subscription(noPackages, SubscriptionStatus.Canceled, profileId: 12),
        };
        var service = CreateService([free, noPackages], subscriptions);

        var entitled = await service.GetPackagesEntitledProfileIdsAsync([10, 11, 12], TestContext.Current.CancellationToken);

        entitled.Should().BeEquivalentTo([10L, 12L], "10 has no row (Free), 11 keeps a plan without packages, 12 falls back to Free");
    }

    [Fact]
    public async Task GetActiveClientCountAsync_ClientLinkedTwice_CountsOnceAndSkipsInactiveLinks()
    {
        var links = new[]
        {
            Link(clientProfileId: 1),
            Link(clientProfileId: 1),
            Link(clientProfileId: 2),
            Link(clientProfileId: 3, isActive: false),
            Link(clientProfileId: 4, profileId: 99),
        };
        var service = CreateService([Plan(1, "free")], [], links);

        var count = await service.GetActiveClientCountAsync(ProfileId, TestContext.Current.CancellationToken);

        count.Should().Be(2);
    }

    [Theory]
    [InlineData(5L, false, true)]
    [InlineData(6L, false, false)]
    [InlineData(null, false, false)]
    [InlineData(6L, true, true)]
    public async Task HasCapacityForClientAsync_AtTheCap_OnlyAClientAlreadyLinkedTakesNoSlot(
        long? clientProfileId, bool belowCap, bool expected)
    {
        var free = Plan(1, "free", maxActiveClients: belowCap ? 3 : 2);
        var links = new[] { Link(clientProfileId: 5), Link(clientProfileId: 8) };
        var service = CreateService([free], [], links);

        var hasCapacity = await service.HasCapacityForClientAsync(ProfileId, clientProfileId, TestContext.Current.CancellationToken);

        hasCapacity.Should().Be(expected);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
