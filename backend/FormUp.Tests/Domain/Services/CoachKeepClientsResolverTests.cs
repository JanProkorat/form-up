using FluentAssertions;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Services;

namespace FormUp.Tests.Domain.Services;

/// <summary>
/// Which live clients a lapsed coach keeps: explicit choice first, then the longest-linked Connected clients.
/// </summary>
public class CoachKeepClientsResolverTests
{
    private static readonly DateTime Origin = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static KeepLinkRow Link(
        long id,
        long clientProfileId,
        int linkedDaysAfterOrigin,
        ConnectionState state = ConnectionState.Connected,
        bool legacy = false) => new(
        id,
        clientProfileId,
        state,
        legacy ? null : Origin.AddDays(linkedDaysAfterOrigin),
        legacy ? Origin.AddDays(linkedDaysAfterOrigin) : Origin.AddYears(5));

    [Fact]
    public void Resolve_NothingChosen_KeepsTheLongestLinkedClientsUpToTheCap()
    {
        var links = new[] { Link(1, 10, 30), Link(2, 20, 10), Link(3, 30, 20) };

        var state = CoachKeepClientsResolver.Resolve(links, 2);

        state.KeptClientProfileIds.Should().BeEquivalentTo([20L, 30L]);
        state.OverCapClientProfileIds.Should().Equal(10L);
        state.IsLapsed.Should().BeTrue();
    }

    [Fact]
    public void Resolve_OverCapClientStillConnected_ChoiceIsPending()
    {
        var links = new[] { Link(1, 10, 30), Link(2, 20, 10), Link(3, 30, 20) };

        CoachKeepClientsResolver.Resolve(links, 2).IsChoicePending.Should().BeTrue();
    }

    [Fact]
    public void FreeSlotCount_KeptClientLeftWhileRestrictedClientsRemain_IsTheEmptySlots()
    {
        var links = new[]
        {
            Link(1, 10, 30, ConnectionState.FreePlanKept),
            Link(2, 20, 10, ConnectionState.FreePlanReleased),
            Link(3, 30, 20, ConnectionState.FreePlanReleased),
        };

        CoachKeepClientsResolver.Resolve(links, 2).FreeSlotCount.Should().Be(1);
    }

    [Fact]
    public void FreeSlotCount_EverySlotTakenNobodyRestrictedOrUnlimited_IsZero()
    {
        var full = new[] { Link(1, 10, 30), Link(2, 20, 10), Link(3, 30, 20) };
        var within = new[] { Link(1, 10, 30, ConnectionState.FreePlanKept) };

        CoachKeepClientsResolver.Resolve(full, 2).FreeSlotCount.Should().Be(0);
        CoachKeepClientsResolver.Resolve(within, 2).FreeSlotCount.Should().Be(0);
        CoachKeepClientsResolver.Resolve(full, null).FreeSlotCount.Should().Be(0);
    }

    [Fact]
    public void Resolve_EveryLiveClientKeptOrReleased_ChoiceIsNotPending()
    {
        var links = new[]
        {
            Link(1, 10, 30, ConnectionState.FreePlanReleased),
            Link(2, 20, 10, ConnectionState.FreePlanKept),
            Link(3, 30, 20, ConnectionState.FreePlanKept),
        };

        var state = CoachKeepClientsResolver.Resolve(links, 2);

        state.OverCapClientProfileIds.Should().Equal(10L);
        state.IsChoicePending.Should().BeFalse();
    }

    [Fact]
    public void Resolve_LegacyLinkWithoutActivationTime_RanksByItsCreationTimeNotAsOldest()
    {
        // Client 10 has no activation time; its row was created on day 25, so it is the newest of the three.
        var links = new[] { Link(1, 10, 25, legacy: true), Link(2, 20, 10), Link(3, 30, 20) };

        var state = CoachKeepClientsResolver.Resolve(links, 2);

        state.KeptClientProfileIds.Should().BeEquivalentTo([20L, 30L]);
        state.OverCapClientProfileIds.Should().Equal(10L);
    }

    [Fact]
    public void Resolve_SameActivationTime_BreaksTheTieByLinkId()
    {
        var links = new[] { Link(7, 10, 5), Link(3, 20, 5), Link(5, 30, 5) };

        var state = CoachKeepClientsResolver.Resolve(links, 2);

        state.KeptClientProfileIds.Should().BeEquivalentTo([20L, 30L]);
    }

    [Fact]
    public void Resolve_ExplicitChoice_BeatsLinkAgeAndIsFilledUpFromTheOldest()
    {
        var links = new[]
        {
            Link(1, 10, 1),
            Link(2, 20, 2),
            Link(3, 30, 3, ConnectionState.FreePlanKept),
            Link(4, 40, 4),
        };

        var state = CoachKeepClientsResolver.Resolve(links, 2);

        state.KeptClientProfileIds.Should().BeEquivalentTo([30L, 10L]);
        state.OverCapClientProfileIds.Should().BeEquivalentTo([20L, 40L]);
    }

    [Fact]
    public void Resolve_MoreKeptThanTheCap_KeepsTheLongestLinkedKeptClients()
    {
        var links = new[]
        {
            Link(1, 10, 3, ConnectionState.FreePlanKept),
            Link(2, 20, 1, ConnectionState.FreePlanKept),
            Link(3, 30, 2, ConnectionState.FreePlanKept),
        };

        var state = CoachKeepClientsResolver.Resolve(links, 2);

        state.KeptClientProfileIds.Should().BeEquivalentTo([20L, 30L]);
        state.OverCapClientProfileIds.Should().Equal(10L);
    }

    [Fact]
    public void Resolve_ReleasedClient_IsNeverKeptAutomaticallyEvenWhenOldest()
    {
        var links = new[]
        {
            Link(1, 10, 1, ConnectionState.FreePlanReleased),
            Link(2, 20, 2),
            Link(3, 30, 3),
        };

        var state = CoachKeepClientsResolver.Resolve(links, 2);

        state.KeptClientProfileIds.Should().BeEquivalentTo([20L, 30L]);
        state.OverCapClientProfileIds.Should().Equal(10L);
    }

    [Fact]
    public void Resolve_ClientLinkedInBothRoles_CountsOnce()
    {
        var links = new[] { Link(1, 10, 1), Link(2, 10, 2), Link(3, 20, 3) };

        var state = CoachKeepClientsResolver.Resolve(links, 2);

        state.KeptClientProfileIds.Should().BeEquivalentTo([10L, 20L]);
        state.OverCapClientProfileIds.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_NoCap_KeepsEveryone()
    {
        var state = CoachKeepClientsResolver.Resolve([Link(1, 10, 1), Link(2, 20, 2)], null);

        state.KeptClientProfileIds.Should().BeEquivalentTo([10L, 20L]);
        state.OverCapClientProfileIds.Should().BeEmpty();
    }
}
