using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Messaging;

/// <summary>
/// Integration tests for <c>GET /conversations/filter-counts</c> (#1095) — the inbox dropdown's
/// chip counts, computed by the same <c>ConversationRosterLoader</c> +
/// <c>ClientRosterFilterClassifier</c> pipeline <c>GetConversationsEndpoint</c>'s <c>filter</c>
/// param uses.
/// </summary>
[Collection(TestCollection.Name)]
public class GetConversationFilterCountsEndpointTests(FitnessApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static string UniqueEmail(string tag) => $"{Guid.NewGuid():N}@conv-filter-counts-{tag}.com";
    private const string Password = "TestPass1!";

    [Fact]
    public async Task GetCounts_NoLinkedClients_AllZero()
    {
        var (http, _) = await SetupTrainerAsync();

        var response = await http.GetAsync("/conversations/filter-counts", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Deserialize(response);

        body!.All.Should().Be(0);
        body.UnreadMessages.Should().Be(0);
        body.NoMessages.Should().Be(0);
        body.NewCheckIns.Should().Be(0);
        body.MissingCheckIns.Should().Be(0);
        body.EndingSoon.Should().Be(0);
    }

    [Fact]
    public async Task GetCounts_MixedRoster_MatchesGetConversationsFilterMembership()
    {
        var (http, trainerId) = await SetupTrainerAsync();
        var (unreadClientId, _) = await SetupLinkedClientAsync(trainerId);
        var (noMessagesClientId, _) = await SetupLinkedClientAsync(trainerId);
        await SetupLinkedClientAsync(trainerId); // no conversation — must not be counted anywhere

        await SeedConversationWithMessageAsync(trainerId, unreadClientId, senderIsClient: true, isRead: false);
        await SeedEmptyConversationAsync(trainerId, noMessagesClientId);

        var response = await http.GetAsync("/conversations/filter-counts", TestContext.Current.CancellationToken);
        var body = await Deserialize(response);

        body!.All.Should().Be(2, "the linked client with no conversation is not an inbox row");
        body.UnreadMessages.Should().Be(1);
        body.NoMessages.Should().Be(1, "an existing zero-message conversation counts; no conversation does not");
    }

    [Fact]
    public async Task GetCounts_ArchivedConversation_StillCountsTowardUnreadMessages()
    {
        // Counts are independent of the archived display toggle — this endpoint has no
        // `archived` param at all, so an archived-but-unread conversation still counts.
        var (http, trainerId) = await SetupTrainerAsync();
        var (clientId, _) = await SetupLinkedClientAsync(trainerId);
        var conversationId = await SeedConversationWithMessageAsync(
            trainerId, clientId, senderIsClient: true, isRead: false);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var conversation = await db.Conversations
                .FirstAsync(c => c.PublicId == conversationId, TestContext.Current.CancellationToken);
            conversation.ArchivedByProfessionalAt = DateTime.UtcNow;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var response = await http.GetAsync("/conversations/filter-counts", TestContext.Current.CancellationToken);
        var body = await Deserialize(response);

        body!.UnreadMessages.Should().Be(1);
    }

    [Fact]
    public async Task GetCounts_PreLinkRequestWithTextMessage_CountsTowardUnreadMessages()
    {
        var (http, trainerId) = await SetupTrainerAsync();
        var clientId = await RegisterUnlinkedClientAsync();
        await SeedPreLinkConversationAsync(trainerId, clientId, ChatMessageKind.Text, isFormer: false);

        var response = await http.GetAsync("/conversations/filter-counts", TestContext.Current.CancellationToken);
        var body = await Deserialize(response);

        body!.UnreadMessages.Should().Be(1, "a pre-link conversation counts like a roster row");
        body.All.Should().Be(1);
    }

    [Fact]
    public async Task GetCounts_PreLinkRequestWithoutMessage_CountsZero()
    {
        var (http, trainerId) = await SetupTrainerAsync();
        var clientId = await RegisterUnlinkedClientAsync();
        await SeedPreLinkConversationAsync(trainerId, clientId, ChatMessageKind.Event, isFormer: false);

        var response = await http.GetAsync("/conversations/filter-counts", TestContext.Current.CancellationToken);
        var body = await Deserialize(response);

        body!.UnreadMessages.Should().Be(0, "event rows never count");
    }

    [Fact]
    public async Task GetCounts_FormerClientUnread_IsStillNotCounted()
    {
        var (http, trainerId) = await SetupTrainerAsync();
        var (formerClientId, formerPublicId) = await SetupLinkedClientAsync(trainerId);
        await SeedConversationWithMessageAsync(trainerId, formerClientId, senderIsClient: true, isRead: false);
        await DeactivateLinkAsync(formerPublicId);

        var flaggedClientId = await RegisterUnlinkedClientAsync();
        await SeedPreLinkConversationAsync(trainerId, flaggedClientId, ChatMessageKind.Text, isFormer: true);

        var response = await http.GetAsync("/conversations/filter-counts", TestContext.Current.CancellationToken);
        var body = await Deserialize(response);

        body!.UnreadMessages.Should().Be(0);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<Guid> RegisterUnlinkedClientAsync()
    {
        var http = factory.CreateClient();
        var email = UniqueEmail("client");
        await TestHelpers.RegisterAsync(http, email, Password, "Pre", "Link", "Client");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await db.Users.AsNoTracking()
            .FirstAsync(u => u.Email == email, TestContext.Current.CancellationToken);
        return user.Id;
    }

    private async Task SeedPreLinkConversationAsync(
        Guid professionalUserId, Guid clientUserId, ChatMessageKind kind, bool isFormer)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var conversation = new Conversation
        {
            PublicId = Guid.NewGuid(),
            ProfessionalUserId = professionalUserId,
            ClientUserId = clientUserId,
            IsFormer = isFormer
        };
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.ChatMessages.Add(new ChatMessage
        {
            PublicId = Guid.NewGuid(),
            ConversationId = conversation.Id,
            SenderUserId = clientUserId,
            Text = "hello",
            Kind = kind,
            IsRead = false
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task DeactivateLinkAsync(Guid clientPublicId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var clientProfile = await db.ClientProfiles
            .FirstAsync(cp => cp.PublicId == clientPublicId, TestContext.Current.CancellationToken);
        var link = await db.ClientProfessionalLinks
            .FirstAsync(l => l.ClientProfileId == clientProfile.Id, TestContext.Current.CancellationToken);
        link.ConnectionState = ConnectionState.Ended;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<(HttpClient Http, Guid UserId)> SetupTrainerAsync()
    {
        var http = factory.CreateClient();
        var email = UniqueEmail("trainer");

        await TestHelpers.RegisterAsync(http, email, Password, "Test", "Trainer", "Trainer");
        var (token, _) = await TestHelpers.LoginAsync(http, email, Password);
        TestHelpers.SetBearerToken(http, token);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await db.Users.AsNoTracking()
            .FirstAsync(u => u.Email == email, TestContext.Current.CancellationToken);

        return (http, user.Id);
    }

    private async Task<(Guid ClientUserId, Guid ClientPublicId)> SetupLinkedClientAsync(Guid professionalUserId)
    {
        var clientUserId = await TestHelpers.RegisterLinkedClientAsync(
            factory, professionalUserId, TestContext.Current.CancellationToken);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var clientProfile = await db.ClientProfiles.AsNoTracking()
            .FirstAsync(cp => cp.UserId == clientUserId, TestContext.Current.CancellationToken);

        return (clientUserId, clientProfile.PublicId);
    }

    private async Task<Guid> SeedConversationWithMessageAsync(
        Guid professionalUserId, Guid clientUserId, bool senderIsClient, bool isRead)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var conversation = new Conversation
        {
            PublicId = Guid.NewGuid(),
            ProfessionalUserId = professionalUserId,
            ClientUserId = clientUserId
        };
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.ChatMessages.Add(new ChatMessage
        {
            PublicId = Guid.NewGuid(),
            ConversationId = conversation.Id,
            SenderUserId = senderIsClient ? clientUserId : professionalUserId,
            Text = "hello",
            IsRead = isRead
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return conversation.PublicId;
    }

    private async Task SeedEmptyConversationAsync(Guid professionalUserId, Guid clientUserId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.Conversations.Add(new Conversation
        {
            PublicId = Guid.NewGuid(),
            ProfessionalUserId = professionalUserId,
            ClientUserId = clientUserId
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<CountsDto?> Deserialize(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<CountsDto>(JsonOptions, TestContext.Current.CancellationToken);

    // ── Local response DTO (per slice rules — no cross-feature imports) ──────

    private sealed class CountsDto
    {
        public int All { get; set; }
        public int UnreadMessages { get; set; }
        public int NoMessages { get; set; }
        public int NewCheckIns { get; set; }
        public int MissingCheckIns { get; set; }
        public int EndingSoon { get; set; }
    }
}
