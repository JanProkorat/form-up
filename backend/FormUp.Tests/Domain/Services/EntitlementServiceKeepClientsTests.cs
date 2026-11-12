using FluentAssertions;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Services;
using FormUp.Tests.Builders;
using FormUp.Tests.Endpoints;
using Microsoft.Extensions.Logging.Abstractions;

namespace FormUp.Tests.Domain.Services;

/// <summary>
/// The lapse rule and the kept / over-cap split of <see cref="EntitlementService.GetKeepClientsStateAsync"/>
/// and <see cref="EntitlementService.IsClientOverCapAsync(Guid, Guid, CancellationToken)"/>.
/// </summary>
public class EntitlementServiceKeepClientsTests
{
    private const long CoachProfileId = 7;
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTime LinkedSince = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly Guid _coachUserId = Guid.NewGuid();

    private static CoachSubscription Subscription(
        SubscriptionStatus status, DateTimeOffset? freeUntil = null, DateTimeOffset? givenFreeAt = null) => new()
    {
        ProfessionalProfileId = CoachProfileId,
        SubscriptionPlanId = 9,
        SubscriptionPlanPriceId = 1,
        SubscriptionPlan = new SubscriptionPlan
        {
            Id = 9,
            Code = "pro",
            NameCs = "Pro",
            NameEn = "Pro",
            NameDe = "Pro",
            Currency = "CZK",
            BothRolesIncluded = true,
        },
        Status = status,
        FreeUntil = freeUntil,
        GivenFreeAt = givenFreeAt,
    };

    private static CoachSubscription PaidSubscription(int? maxActiveClients, SubscriptionStatus status = SubscriptionStatus.Active)
    {
        var subscription = Subscription(status);
        subscription.SubscriptionPlan.MaxActiveClients = maxActiveClients;

        return subscription;
    }

    private static SubscriptionPlan FreePlan(int? maxActiveClients) => new()
    {
        Id = 1,
        Code = "free",
        NameCs = "Zdarma",
        NameEn = "Free",
        NameDe = "Kostenlos",
        Currency = "CZK",
        MaxActiveClients = maxActiveClients,
    };

    /// <summary>
    /// A coach with three Connected clients (profile ids 1, 2, 3) linked on successive days, and the given subscription.
    /// </summary>
    private EntitlementService CreateService(
        CoachSubscription? subscription, int? freeCap = 2, ConnectionState firstClientState = ConnectionState.Connected)
    {
        var builder = new MockDbBuilder()
            .With(EntityBuilder.ProfessionalProfile.WithId(CoachProfileId).WithUserId(_coachUserId).Build())
            .With(FreePlan(freeCap));

        for (var clientId = 1; clientId <= 3; clientId++)
        {
            builder
                .With(EntityBuilder.ClientProfile.WithId(clientId).WithUserId(ClientUserId(clientId)).Build())
                .With(EntityBuilder.ClientProfessionalLink
                    .WithId(clientId)
                    .WithClientProfileId(clientId)
                    .WithProfessionalProfileId(CoachProfileId)
                    .WithActivatedAtUtc(LinkedSince.AddDays(clientId))
                    .WithConnectionState(clientId == 1 ? firstClientState : ConnectionState.Connected)
                    .Build());
        }

        if (subscription is not null)
        {
            builder.With(subscription);
        }

        return new EntitlementService(builder.Build(), new FixedTimeProvider(Now), NullLogger<EntitlementService>.Instance);
    }

    private static Guid ClientUserId(int clientProfileId) => new(clientProfileId, 0, 0, [0, 0, 0, 0, 0, 0, 0, 1]);

    [Theory]
    [InlineData(SubscriptionStatus.Canceled)]
    [InlineData(SubscriptionStatus.Incomplete)]
    public async Task GetKeepClientsState_EndedPlan_IsLapsedAndKeepsTheLongestLinked(SubscriptionStatus status)
    {
        var service = CreateService(Subscription(status));

        var state = await service.GetKeepClientsStateAsync(CoachProfileId, TestContext.Current.CancellationToken);

        state.IsLapsed.Should().BeTrue();
        state.Cap.Should().Be(2);
        state.KeptClientProfileIds.Should().BeEquivalentTo([1L, 2L]);
        state.OverCapClientProfileIds.Should().Equal(3L);
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(1, false)]
    [InlineData(null, false)]
    public async Task GetKeepClientsState_GivenFreePlan_IsAPlanChoiceOnceTheGiftEnded_NeverALapse(int? daysFromNow, bool planChoice)
    {
        var gift = Subscription(
            SubscriptionStatus.Active,
            freeUntil: daysFromNow is { } days ? Now.AddDays(days) : null,
            givenFreeAt: Now.AddDays(-10));
        var service = CreateService(gift);

        var state = await service.GetKeepClientsStateAsync(CoachProfileId, TestContext.Current.CancellationToken);

        state.IsLapsed.Should().BeFalse();
        state.IsPlanChoiceRequired.Should().Be(planChoice);
        state.OverCapClientProfileIds.Should().BeEmpty("nobody is over the cap while a plan choice is pending");

        if (planChoice)
        {
            state.Cap.Should().Be(2, "the picker needs the Free plan's cap");
        }
    }

    [Fact]
    public async Task GetKeepClientsState_CapComesFromTheFreePlanRowNotALiteral()
    {
        var service = CreateService(Subscription(SubscriptionStatus.Canceled), freeCap: 3);

        var state = await service.GetKeepClientsStateAsync(CoachProfileId, TestContext.Current.CancellationToken);

        state.Cap.Should().Be(3);
        state.OverCapClientProfileIds.Should().BeEmpty();
    }

    [Theory]
    [InlineData(SubscriptionStatus.Active)]
    [InlineData(SubscriptionStatus.Trialing)]
    [InlineData(SubscriptionStatus.PastDue)]
    public async Task GetKeepClientsState_PaidStatus_KeepsThePaidPlan(SubscriptionStatus status)
    {
        var service = CreateService(Subscription(status));

        var state = await service.GetKeepClientsStateAsync(CoachProfileId, TestContext.Current.CancellationToken);

        state.IsLapsed.Should().BeFalse();
        state.OverCapClientProfileIds.Should().BeEmpty();
    }

    [Theory]
    [InlineData(SubscriptionStatus.Active)]
    [InlineData(SubscriptionStatus.Trialing)]
    [InlineData(SubscriptionStatus.PastDue)]
    public async Task GetKeepClientsState_SmallerPaidPlan_SplitsAgainstThePlansOwnCapAndIsNotALapse(SubscriptionStatus status)
    {
        var service = CreateService(PaidSubscription(maxActiveClients: 2, status), freeCap: 1);

        var state = await service.GetKeepClientsStateAsync(CoachProfileId, TestContext.Current.CancellationToken);

        state.IsLapsed.Should().BeFalse();
        state.Cap.Should().Be(2, "the cap is the paid plan's limit, not Free's");
        state.KeptClientProfileIds.Should().BeEquivalentTo([1L, 2L]);
        state.OverCapClientProfileIds.Should().Equal(3L);
        state.CanChooseKeptClients.Should().BeTrue();
        state.IsChoicePending.Should().BeTrue();
    }

    [Theory]
    [InlineData(3)]
    [InlineData(10)]
    [InlineData(null)]
    public async Task GetKeepClientsState_PaidPlanWithinItsLimitOrUnlimited_NobodyIsOverCap(int? maxActiveClients)
    {
        var service = CreateService(PaidSubscription(maxActiveClients), freeCap: 1);

        var state = await service.GetKeepClientsStateAsync(CoachProfileId, TestContext.Current.CancellationToken);

        state.OverCapClientProfileIds.Should().BeEmpty();
        state.CanChooseKeptClients.Should().BeFalse();
        state.IsChoicePending.Should().BeFalse();
    }

    [Fact]
    public async Task GetKeepClientsState_SmallerPaidPlanAfterTheChoice_KeepsTheOverCapListButTheChoiceIsNoLongerPending()
    {
        var service = CreateService(PaidSubscription(maxActiveClients: 2), firstClientState: ConnectionState.FreePlanReleased);

        var state = await service.GetKeepClientsStateAsync(CoachProfileId, TestContext.Current.CancellationToken);

        state.OverCapClientProfileIds.Should().Equal(1L);
        state.IsChoicePending.Should().BeFalse();
    }

    [Fact]
    public async Task IsClientOverCap_SmallerPaidPlan_IsTrueForTheOverCapClient()
    {
        var service = CreateService(PaidSubscription(maxActiveClients: 2));

        (await service.IsClientOverCapAsync(_coachUserId, ClientUserId(3), TestContext.Current.CancellationToken))
            .Should().BeTrue();
    }

    [Fact]
    public async Task GetKeepClientsState_NoSubscriptionRow_IsNotLapsed()
    {
        var service = CreateService(null);

        var state = await service.GetKeepClientsStateAsync(CoachProfileId, TestContext.Current.CancellationToken);

        state.IsLapsed.Should().BeFalse();
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(-1, true)]
    public async Task GetKeepClientsState_CanceledWithFreeUntil_IsLapsedOnlyOnceTheDeadlineHasPassed(
        int freeUntilDaysFromNow, bool expectedLapsed)
    {
        var service = CreateService(Subscription(SubscriptionStatus.Canceled, Now.AddDays(freeUntilDaysFromNow)));

        var state = await service.GetKeepClientsStateAsync(CoachProfileId, TestContext.Current.CancellationToken);

        state.IsLapsed.Should().Be(expectedLapsed);
    }

    [Fact]
    public async Task GetKeepClientsState_ReleasedClient_IsNeverAutoKeptEvenWhenTheLongestLinked()
    {
        // Client 1 is the longest linked but was released, so the Free plan fills up with 2 and 3.
        var service = CreateService(Subscription(SubscriptionStatus.Canceled), firstClientState: ConnectionState.FreePlanReleased);

        var state = await service.GetKeepClientsStateAsync(CoachProfileId, TestContext.Current.CancellationToken);

        state.KeptClientProfileIds.Should().BeEquivalentTo([2L, 3L]);
        state.OverCapClientProfileIds.Should().Equal(1L);
    }

    [Fact]
    public async Task IsClientOverCap_MapsTheMongoClientUserIdToTheLinkProfile()
    {
        var service = CreateService(Subscription(SubscriptionStatus.Canceled));
        var ct = TestContext.Current.CancellationToken;

        (await service.IsClientOverCapAsync(_coachUserId, ClientUserId(3), ct)).Should().BeTrue();
        (await service.IsClientOverCapAsync(_coachUserId, ClientUserId(1), ct)).Should().BeFalse();
        (await service.IsClientOverCapAsync(_coachUserId, Guid.NewGuid(), ct)).Should().BeFalse();
        (await service.IsClientOverCapAsync(Guid.NewGuid(), ClientUserId(3), ct)).Should().BeFalse();
    }

    [Fact]
    public async Task IsClientOverCap_PaidPlan_IsFalseForEveryClient()
    {
        var service = CreateService(Subscription(SubscriptionStatus.PastDue));

        (await service.IsClientOverCapAsync(_coachUserId, ClientUserId(3), TestContext.Current.CancellationToken))
            .Should().BeFalse();
    }
}
