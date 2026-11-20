using System.Security.Claims;
using System.Collections.Concurrent;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Hubs;
using FormUp.Application.Infrastructure.Services;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace FormUp.Tests.Infrastructure.Hubs;

/// <summary>
/// Tests for <see cref="NotificationHub.SendTyping"/>, in particular the
/// <c>Guid.TryParse</c> guard added for #663 — a malformed
/// <c>conversationId</c> must fail soft (matching the hub's existing style,
/// which already returns early on a null/unresolved userId) instead of
/// letting <c>Guid.Parse</c> throw a <see cref="FormatException"/>.
/// </summary>
public class NotificationHubTests
{
    private static NotificationHub CreateHub(Guid userId, IServiceScopeFactory scopeFactory)
    {
        var hub = new NotificationHub(new PresenceTracker(), scopeFactory);

        var context = Substitute.For<HubCallerContext>();
        context.User.Returns(new ClaimsPrincipal(
            new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(userId, AppRoles.Client))));

        hub.Context = context;
        hub.Clients = Substitute.For<IHubCallerClients>();
        hub.Groups = Substitute.For<IGroupManager>();

        return hub;
    }

    [Fact]
    public async Task SendTyping_MalformedConversationId_ReturnsWithoutThrowing_AndNeverOpensDbScope()
    {
        // Arrange
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var hub = CreateHub(Guid.NewGuid(), scopeFactory);

        // Act
        var act = () => hub.SendTyping("not-a-guid");

        // Assert — no FormatException, and the malformed id short-circuits before
        // a DB scope is ever created (the old code parsed conversationId inline
        // inside the EF query, one line after the scope was opened).
        await act.Should().NotThrowAsync();
        scopeFactory.DidNotReceive().CreateScope();
    }

    [Fact]
    public async Task SendTyping_NullUserId_ReturnsWithoutThrowing_AndNeverOpensDbScope()
    {
        // Arrange — no UserId claim on the caller's ClaimsPrincipal.
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var hub = new NotificationHub(new PresenceTracker(), scopeFactory)
        {
            Context = Substitute.For<HubCallerContext>(),
            Clients = Substitute.For<IHubCallerClients>(),
            Groups = Substitute.For<IGroupManager>()
        };
        hub.Context.User.Returns(new ClaimsPrincipal(new ClaimsIdentity()));

        // Act
        var act = () => hub.SendTyping(Guid.NewGuid().ToString());

        // Assert
        await act.Should().NotThrowAsync();
        scopeFactory.DidNotReceive().CreateScope();
    }

    // ── Invite-only threads: no presence, no typing for the inviter ──────────

    public enum Relation { InviteOnly, LiveLink, DeclinedRequest }

    private sealed record Scenario(
        Guid TrainerId, Guid ClientId, Guid ConversationId, IServiceScopeFactory ScopeFactory);

    private static Scenario BuildScenario(Relation relation)
    {
        var trainerUser = EntityBuilder.User.WithId(Guid.NewGuid()).WithEmail("t@hub.test").Build();
        var clientUser = EntityBuilder.User.WithId(Guid.NewGuid()).WithEmail("c@hub.test").Build();
        var professional = EntityBuilder.ProfessionalProfile.WithId(1).WithUser(trainerUser).Build();
        var clientProfile = EntityBuilder.ClientProfile.WithId(1).WithUser(clientUser).Build();
        var conversation = new Conversation
        {
            PublicId = Guid.NewGuid(),
            ProfessionalUserId = trainerUser.Id,
            ClientUserId = clientUser.Id,
        };

        var builder = new MockDbBuilder()
            .With(trainerUser).With(clientUser).With(professional).With(clientProfile).With(conversation);

        if (relation == Relation.LiveLink)
        {
            builder.With(EntityBuilder.ClientProfessionalLink
                .WithClientProfile(clientProfile).WithProfessionalProfile(professional).Build());
        }
        else if (relation == Relation.DeclinedRequest)
        {
            builder.With(new ClientRequest
            {
                ClientProfileId = clientProfile.Id,
                ProfessionalProfileId = professional.Id,
                ClientProfile = clientProfile,
                ProfessionalProfile = professional,
                Status = ClientRequestStatus.Rejected,
            });
        }

        var services = new ServiceCollection();
        services.AddSingleton<IApplicationDbContext>(builder.Build());
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        return new Scenario(trainerUser.Id, clientUser.Id, conversation.PublicId, scopeFactory);
    }

    private static (NotificationHub Hub, ConcurrentDictionary<string, IClientProxy> Proxies) CreateHubFor(
        Guid callerId, string role, IServiceScopeFactory scopeFactory)
    {
        var hub = new NotificationHub(new PresenceTracker(), scopeFactory);
        var proxies = new ConcurrentDictionary<string, IClientProxy>();

        var context = Substitute.For<HubCallerContext>();
        context.User.Returns(new ClaimsPrincipal(new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(callerId, role))));
        context.ConnectionId.Returns("conn-1");

        var clients = Substitute.For<IHubCallerClients>();
        clients.Group(Arg.Any<string>()).Returns(call => proxies.GetOrAdd(call.Arg<string>(), _ => Substitute.For<IClientProxy>()));

        hub.Context = context;
        hub.Clients = clients;
        hub.Groups = Substitute.For<IGroupManager>();

        return (hub, proxies);
    }

    private static bool Received(ConcurrentDictionary<string, IClientProxy> proxies, Guid userId, string method) =>
        proxies.TryGetValue(userId.ToString(), out var proxy) &&
        proxy.ReceivedCalls().Any(call => call.GetMethodInfo().Name == "SendCoreAsync" && (string)call.GetArguments()[0]! == method);

    [Fact]
    public async Task SendTyping_ProfessionalInInviteOnlyThread_IsNotDelivered()
    {
        var scenario = BuildScenario(Relation.InviteOnly);
        var (hub, proxies) = CreateHubFor(scenario.TrainerId, AppRoles.Trainer, scenario.ScopeFactory);

        await hub.SendTyping(scenario.ConversationId.ToString());

        Received(proxies, scenario.ClientId, "typing").Should().BeFalse();
    }

    [Fact]
    public async Task SendTyping_InviteeInInviteOnlyThread_IsNotDeliveredToProfessional()
    {
        var scenario = BuildScenario(Relation.InviteOnly);
        var (hub, proxies) = CreateHubFor(scenario.ClientId, AppRoles.Client, scenario.ScopeFactory);

        await hub.SendTyping(scenario.ConversationId.ToString());

        Received(proxies, scenario.TrainerId, "typing").Should().BeFalse();
    }

    [Theory]
    [InlineData(Relation.LiveLink)]
    [InlineData(Relation.DeclinedRequest)]
    public async Task SendTyping_ThreadWithLinkOrRequest_StillDelivered(Relation relation)
    {
        var scenario = BuildScenario(relation);
        var (hub, proxies) = CreateHubFor(scenario.ClientId, AppRoles.Client, scenario.ScopeFactory);

        await hub.SendTyping(scenario.ConversationId.ToString());

        Received(proxies, scenario.TrainerId, "typing").Should().BeTrue();
    }

    [Fact]
    public async Task Connect_InviteeInInviteOnlyThread_PresenceIsNotPushedToProfessional()
    {
        var scenario = BuildScenario(Relation.InviteOnly);
        var (hub, proxies) = CreateHubFor(scenario.ClientId, AppRoles.Client, scenario.ScopeFactory);

        await hub.OnConnectedAsync();

        Received(proxies, scenario.TrainerId, "userPresence").Should().BeFalse();
    }

    [Theory]
    [InlineData(Relation.LiveLink)]
    [InlineData(Relation.DeclinedRequest)]
    public async Task Connect_InviteeWithLinkOrRequest_PresenceIsPushedToProfessional(Relation relation)
    {
        var scenario = BuildScenario(relation);
        var (hub, proxies) = CreateHubFor(scenario.ClientId, AppRoles.Client, scenario.ScopeFactory);

        await hub.OnConnectedAsync();

        Received(proxies, scenario.TrainerId, "userPresence").Should().BeTrue();
    }

    [Theory]
    [InlineData(AppRoles.Admin, true)]
    [InlineData(AppRoles.Trainer, false)]
    public async Task Connect_JoinsTheAdminsGroupOnlyForAdmins_AndLeavesItOnDisconnect(string role, bool isAdmin)
    {
        var scenario = BuildScenario(Relation.LiveLink);
        var (hub, _) = CreateHubFor(scenario.TrainerId, role, scenario.ScopeFactory);

        await hub.OnConnectedAsync();
        await hub.OnDisconnectedAsync(null);

        var joined = hub.Groups.ReceivedCalls().Count(call =>
            call.GetMethodInfo().Name == nameof(IGroupManager.AddToGroupAsync) && (string)call.GetArguments()[1]! == "admins");
        var left = hub.Groups.ReceivedCalls().Count(call =>
            call.GetMethodInfo().Name == nameof(IGroupManager.RemoveFromGroupAsync) && (string)call.GetArguments()[1]! == "admins");

        joined.Should().Be(isAdmin ? 1 : 0);
        left.Should().Be(isAdmin ? 1 : 0);
    }

    [Fact]
    public async Task Connect_ProfessionalInInviteOnlyThread_PresenceStillReachesInvitee()
    {
        var scenario = BuildScenario(Relation.InviteOnly);
        var (hub, proxies) = CreateHubFor(scenario.TrainerId, AppRoles.Trainer, scenario.ScopeFactory);

        await hub.OnConnectedAsync();

        Received(proxies, scenario.ClientId, "userPresence").Should().BeTrue();
    }
}
