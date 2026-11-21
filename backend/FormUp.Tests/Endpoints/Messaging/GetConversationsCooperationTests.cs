using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Messaging;

/// <summary>
/// Seeds invites and client requests together with the chat events that point at them, and reads
/// <c>GET /conversations</c> back with string-enum options. Shared by the conversation-list tests.
/// </summary>
internal static class ConversationListSeed
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    internal record CooperationResult(
        CooperationType Type,
        CooperationDirection Direction,
        CooperationCardStatus Status,
        Guid SourceId,
        LinkCapabilityScope? RequestedScope);

    internal record ParticipantResult(Guid Id, List<string> Roles);

    internal record ConversationResult(
        Guid? Id,
        ParticipantResult Participant,
        int UnreadCount,
        bool IsFormer,
        DateTime? EndedAt,
        CooperationResult? Cooperation);

    internal static async Task<List<ConversationResult>> GetAsync(HttpClient http, string? query = null)
    {
        var response = await http.GetAsync("/conversations" + query, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<List<ConversationResult>>(
            JsonOptions, TestContext.Current.CancellationToken))!;
    }

    /// <summary>Inserts an invite row and the Invited chat event that points at it.</summary>
    internal static async Task<Guid> SeedInviteAsync(
        FitnessApiFactory factory,
        Actor coach,
        Actor client,
        PendingInviteStatus status = PendingInviteStatus.Pending,
        LinkCapabilityScope? scope = null,
        DateTime? sentAt = null,
        string? message = null,
        bool appendEvent = true)
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope1 = factory.Services.CreateScope();
        var db = scope1.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var invite = new PendingInvite
        {
            ProfessionalProfileId = coach.ProfileId,
            Email = client.Email,
            Status = status,
            RequestedScope = scope,
            SentAt = sentAt ?? DateTime.UtcNow,
        };
        db.PendingInvites.Add(invite);
        await db.SaveChangesAsync(ct);

        if (appendEvent)
        {
            await AppendEventAsync(scope1, coach, client, coach.UserId, ChatEventType.Invited, invite.PublicId, message);
        }

        return invite.PublicId;
    }

    /// <summary>Inserts a client request row and the Requested chat event that points at it.</summary>
    internal static async Task<Guid> SeedRequestAsync(
        FitnessApiFactory factory,
        Actor coach,
        Actor client,
        ClientRequestStatus status = ClientRequestStatus.Pending,
        string? message = null)
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var request = new ClientRequest
        {
            ClientProfileId = client.ProfileId,
            ProfessionalProfileId = coach.ProfileId,
            Status = status,
        };
        db.ClientRequests.Add(request);
        await db.SaveChangesAsync(ct);

        await AppendEventAsync(scope, coach, client, client.UserId, ChatEventType.Requested, request.PublicId, message);
        return request.PublicId;
    }

    internal static Task AppendEventAsync(
        IServiceScope scope, Actor coach, Actor client, Guid actorUserId, ChatEventType type, Guid? sourceId, string? message) =>
        scope.ServiceProvider.GetRequiredService<IConversationSeedService>().AppendCooperationEventAsync(
            coach.UserId, client.UserId, actorUserId, type, sourceId, message,
            createConversationIfMissing: true, TestContext.Current.CancellationToken);
}

/// <summary>
/// <c>GET /conversations</c> carries the newest invite or request of each conversation as
/// <c>cooperation</c>, for both the client and the coach caller.
/// </summary>
[Collection(TestCollection.Shard3)]
public class GetConversationsCooperationTests(FitnessApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NoInviteOrRequest_CooperationIsNull()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).CreateAsync(Ct);
        using (var scope = factory.Services.CreateScope())
        {
            await ConversationListSeed.AppendEventAsync(
                scope, coach, client, coach.UserId, ChatEventType.Accepted, Guid.NewGuid(), null);
        }

        var rows = await ConversationListSeed.GetAsync(coach.Http);

        rows.Should().ContainSingle().Which.Cooperation.Should().BeNull();
    }

    [Fact]
    public async Task Invite_IsReceivedByClient_AndSentByCoach_WithScope()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var inviteId = await ConversationListSeed.SeedInviteAsync(
            factory, coach, client, scope: LinkCapabilityScope.NutritionOnly);

        var asClient = (await ConversationListSeed.GetAsync(client.Http)).Should().ContainSingle().Subject.Cooperation;
        var asCoach = (await ConversationListSeed.GetAsync(coach.Http)).Should().ContainSingle().Subject.Cooperation;

        asClient!.Type.Should().Be(CooperationType.Invite);
        asClient.Direction.Should().Be(CooperationDirection.Received);
        asClient.Status.Should().Be(CooperationCardStatus.Pending);
        asClient.SourceId.Should().Be(inviteId);
        asClient.RequestedScope.Should().Be(LinkCapabilityScope.NutritionOnly);
        asCoach!.Direction.Should().Be(CooperationDirection.Sent);
    }

    [Fact]
    public async Task Request_IsSentByClient_AndReceivedByCoach_WithoutScope()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var requestId = await ConversationListSeed.SeedRequestAsync(factory, coach, client, ClientRequestStatus.Rejected);

        var asClient = (await ConversationListSeed.GetAsync(client.Http)).Should().ContainSingle().Subject.Cooperation;
        var asCoach = (await ConversationListSeed.GetAsync(coach.Http)).Should().ContainSingle().Subject.Cooperation;

        asClient!.Type.Should().Be(CooperationType.Request);
        asClient.Direction.Should().Be(CooperationDirection.Sent);
        asClient.SourceId.Should().Be(requestId);
        asClient.RequestedScope.Should().BeNull();
        asClient.Status.Should().Be(CooperationCardStatus.Declined, "a rejected request reads Declined");
        asCoach!.Direction.Should().Be(CooperationDirection.Received);
    }

    [Fact]
    public async Task PendingInviteOlderThanSevenDays_IsExpired()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await ConversationListSeed.SeedInviteAsync(factory, coach, client, sentAt: DateTime.UtcNow.AddDays(-8));

        (await ConversationListSeed.GetAsync(client.Http)).Single().Cooperation!.Status
            .Should().Be(CooperationCardStatus.Expired);
    }

    [Fact]
    public async Task NewestProposalWins()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await ConversationListSeed.SeedInviteAsync(factory, coach, client, PendingInviteStatus.Declined);
        var requestId = await ConversationListSeed.SeedRequestAsync(factory, coach, client);

        var cooperation = (await ConversationListSeed.GetAsync(client.Http)).Should().ContainSingle().Subject.Cooperation;

        cooperation!.Type.Should().Be(CooperationType.Request);
        cooperation.SourceId.Should().Be(requestId);
    }

    [Fact]
    public async Task MissingSourceRow_CooperationIsNull_NotServerError()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        using var scope = factory.Services.CreateScope();
        await ConversationListSeed.AppendEventAsync(
            scope, coach, client, coach.UserId, ChatEventType.Invited, Guid.NewGuid(), null);

        var rows = await ConversationListSeed.GetAsync(client.Http);

        rows.Should().ContainSingle().Which.Cooperation.Should().BeNull();
    }

    [Fact]
    public async Task CoachWithLinkedClient_ProposalAppearsOnRosterPath()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).CreateAsync(Ct);
        var requestId = await ConversationListSeed.SeedRequestAsync(
            factory, coach, client, ClientRequestStatus.Accepted);

        var rows = await ConversationListSeed.GetAsync(coach.Http, "?filter=All");

        rows.Should().ContainSingle().Which.Cooperation!.SourceId.Should().Be(requestId);
    }

    [Fact]
    public async Task ThirdParty_NeverSeesOrAppearsInCooperation()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var otherClient = await TestActors.Client(factory).CreateAsync(Ct);
        await ConversationListSeed.SeedInviteAsync(factory, coach, client);
        var otherRequestId = await ConversationListSeed.SeedRequestAsync(factory, coach, otherClient);

        var clientRows = await ConversationListSeed.GetAsync(client.Http);
        var otherRows = await ConversationListSeed.GetAsync(otherClient.Http);

        clientRows.Should().ContainSingle().Which.Cooperation!.Type.Should().Be(CooperationType.Invite);
        otherRows.Should().ContainSingle().Which.Cooperation!.SourceId.Should().Be(otherRequestId);

        var bystander = await TestActors.Client(factory).CreateAsync(Ct);
        (await ConversationListSeed.GetAsync(bystander.Http)).Should().BeEmpty();
    }
}
