using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Messaging;

/// <summary>
/// Integration tests for <c>GET /conversations</c>'s <c>filter</c> param (#1095) — the
/// roster-driven path shared with <c>GET /conversations/filter-counts</c> via
/// <c>ConversationRosterLoader</c>. Uses the Testcontainers-backed <see cref="FitnessApiFactory"/>
/// host — never the mock-Mongo harness, which ignores <c>FilterDefinition</c> and would make the
/// EndingSoon assertions unfalsifiable.
/// </summary>
[Collection(TestCollection.Name)]
public class GetConversationsFilterTests(FitnessApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static string UniqueEmail(string tag) => $"{Guid.NewGuid():N}@get-conv-filter-{tag}.com";
    private const string Password = "TestPass1!";

    // ── filter chips ─────────────────────────────────────────────────────────

    [Fact]
    public async Task List_UnreadMessagesFilter_ReturnsOnlyClientsWithUnreadFromClient()
    {
        var (http, trainerId) = await SetupTrainerAsync();
        var (unreadClientId, _) = await SetupLinkedClientAsync(trainerId);
        var (readClientId, _) = await SetupLinkedClientAsync(trainerId);

        await SeedConversationWithMessageAsync(trainerId, unreadClientId, senderIsClient: true, isRead: false);
        await SeedConversationWithMessageAsync(trainerId, readClientId, senderIsClient: true, isRead: true);

        var response = await http.GetAsync("/conversations?filter=UnreadMessages", TestContext.Current.CancellationToken);
        var body = await Deserialize(response);

        body!.Select(c => c.Participant.Id).Should().BeEquivalentTo([unreadClientId]);
    }

    [Fact]
    public async Task List_NoMessagesFilter_ListsEmptyConversation_NotClientWithoutConversation()
    {
        var (http, trainerId) = await SetupTrainerAsync();
        var (noConvClientId, _) = await SetupLinkedClientAsync(trainerId);
        var (emptyConvClientId, _) = await SetupLinkedClientAsync(trainerId);
        var (hasMsgClientId, _) = await SetupLinkedClientAsync(trainerId);

        await SeedEmptyConversationAsync(trainerId, emptyConvClientId);
        await SeedConversationWithMessageAsync(trainerId, hasMsgClientId, senderIsClient: true, isRead: true);

        var response = await http.GetAsync("/conversations?filter=NoMessages", TestContext.Current.CancellationToken);
        var body = await Deserialize(response);

        body!.Select(c => c.Participant.Id).Should().BeEquivalentTo([emptyConvClientId],
            "an existing zero-message conversation stays listed; a client with no conversation is not an inbox row");
        body!.Should().NotContain(c => c.Participant.Id == noConvClientId);
        body!.Should().OnlyContain(c => c.Id != null);
    }

    [Fact]
    public async Task List_AllFilter_LinkedClientWithNoConversation_IsAbsentAndCountsMatch()
    {
        var (http, trainerId) = await SetupTrainerAsync();
        var (noConvClientId, _) = await SetupLinkedClientAsync(trainerId);
        var (hasMsgClientId, _) = await SetupLinkedClientAsync(trainerId);
        var (emptyConvClientId, _) = await SetupLinkedClientAsync(trainerId);

        await SeedConversationWithMessageAsync(trainerId, hasMsgClientId, senderIsClient: true, isRead: true);
        await SeedEmptyConversationAsync(trainerId, emptyConvClientId);

        var response = await http.GetAsync("/conversations?filter=All", TestContext.Current.CancellationToken);
        var body = await Deserialize(response);

        body!.Select(c => c.Participant.Id).Should().BeEquivalentTo([hasMsgClientId, emptyConvClientId]);
        body!.Should().NotContain(c => c.Participant.Id == noConvClientId);

        var countsResponse = await http.GetAsync("/conversations/filter-counts", TestContext.Current.CancellationToken);
        var counts = await countsResponse.Content.ReadFromJsonAsync<FilterCountsResponse>(
            JsonOptions, TestContext.Current.CancellationToken);

        counts!.All.Should().Be(body!.Count,
            "the chip count must equal the rows the list shows — the chat-less client counts nowhere");
        counts.NoMessages.Should().Be(1, "only the empty existing conversation; the chat-less client is excluded");
    }

    [Fact]
    public async Task List_AllFilter_AsClient_ReturnsOnlyRealConversations_NoNullIdRows()
    {
        var http = factory.CreateClient();
        var clientEmail = UniqueEmail("client-all-real");
        await TestHelpers.RegisterAsync(http, clientEmail, Password, "Test", "Client", "Client");
        var (clientToken, _) = await TestHelpers.LoginAsync(http, clientEmail, Password);
        TestHelpers.SetBearerToken(http, clientToken);

        var trainerHttp = factory.CreateClient();
        var trainerEmail = UniqueEmail("client-all-real-trainer");
        await TestHelpers.RegisterAsync(trainerHttp, trainerEmail, Password, "Test", "Trainer", "Trainer");
        var (trainerToken, _) = await TestHelpers.LoginAsync(trainerHttp, trainerEmail, Password);
        TestHelpers.SetBearerToken(trainerHttp, trainerToken);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var trainerUserId = (await db.Users.AsNoTracking()
                .FirstAsync(u => u.Email == trainerEmail, TestContext.Current.CancellationToken)).Id;
            var clientUserId = (await db.Users.AsNoTracking()
                .FirstAsync(u => u.Email == clientEmail, TestContext.Current.CancellationToken)).Id;

            db.Conversations.Add(new Conversation
            {
                PublicId = Guid.NewGuid(),
                ProfessionalUserId = trainerUserId,
                ClientUserId = clientUserId
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var response = await http.GetAsync("/conversations?filter=All", TestContext.Current.CancellationToken);
        var body = await Deserialize(response);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body!.Should().ContainSingle();
        body!.Should().OnlyContain(c => c.Id != null,
            "a Client caller never gets a live-roster placeholder row — only its real conversations");
    }

    [Fact]
    public async Task List_NoMessagesFilter_ArchivedTrue_NullConversationRowNeverAppears()
    {
        var (http, trainerId) = await SetupTrainerAsync();
        await SetupLinkedClientAsync(trainerId); // no conversation — not an inbox row at all

        var response = await http.GetAsync(
            "/conversations?filter=NoMessages&archived=true", TestContext.Current.CancellationToken);
        var body = await Deserialize(response);

        body!.Should().BeEmpty("a client without a conversation is never listed, archived view included");
    }

    [Fact]
    public async Task List_AllFilter_ArchivedTrue_NullConversationRowNeverAppears()
    {
        var (http, trainerId) = await SetupTrainerAsync();
        await SetupLinkedClientAsync(trainerId); // no conversation — not an inbox row at all

        var response = await http.GetAsync(
            "/conversations?filter=All&archived=true", TestContext.Current.CancellationToken);
        var body = await Deserialize(response);

        body!.Should().BeEmpty("a client without a conversation is never listed, even under All");
    }

    [Fact]
    public async Task List_CheckInFilters_SplitNewSubmissionsFromPastDueUnanswered()
    {
        var (http, trainerId) = await SetupTrainerAsync();
        var (missingClientId, _) = await SetupLinkedClientAsync(trainerId);
        var (newClientId, _) = await SetupLinkedClientAsync(trainerId);
        await SeedEmptyConversationAsync(trainerId, missingClientId);
        await SeedEmptyConversationAsync(trainerId, newClientId);

        await CheckInFixtures.AddForUsersAsync(factory, trainerId, missingClientId, submitted: false);
        await CheckInFixtures.AddForUsersAsync(factory, trainerId, newClientId, submitted: true);

        var missing = await http.GetAsync("/conversations?filter=MissingCheckIns", TestContext.Current.CancellationToken);
        (await Deserialize(missing))!.Select(c => c.Participant.Id).Should().BeEquivalentTo([missingClientId]);

        var fresh = await http.GetAsync("/conversations?filter=NewCheckIns", TestContext.Current.CancellationToken);
        (await Deserialize(fresh))!.Select(c => c.Participant.Id).Should().BeEquivalentTo([newClientId]);
    }

    [Fact]
    public async Task List_EndingSoonFilter_ReturnsActivePlanEndingWithinFourteenDays()
    {
        var (http, trainerId) = await SetupTrainerAsync();
        var (endingSoonClientId, _) = await SetupLinkedClientAsync(trainerId);
        var (notEndingSoonClientId, _) = await SetupLinkedClientAsync(trainerId);
        await SeedEmptyConversationAsync(trainerId, endingSoonClientId);
        await SeedEmptyConversationAsync(trainerId, notEndingSoonClientId);

        // 1-week plan starting 6 days ago ends 1 day from now — within the 14-day window.
        await SeedActiveTrainingPlanAsync(trainerId, endingSoonClientId, DateTime.UtcNow.AddDays(-6), weekCount: 1);
        // 10-week plan starting today ends far beyond 14 days.
        await SeedActiveTrainingPlanAsync(trainerId, notEndingSoonClientId, DateTime.UtcNow, weekCount: 10);

        var response = await http.GetAsync("/conversations?filter=EndingSoon", TestContext.Current.CancellationToken);
        (await Deserialize(response))!.Select(c => c.Participant.Id).Should().BeEquivalentTo([endingSoonClientId]);
    }

    // ── population contract ─────────────────────────────────────────────────

    [Fact]
    public async Task List_NonAllFilter_ExcludesClientWithDeactivatedLink_EvenWithUnreadMessages()
    {
        var (http, trainerId) = await SetupTrainerAsync();
        var (formerClientId, formerPublicId) = await SetupLinkedClientAsync(trainerId);
        await SeedConversationWithMessageAsync(trainerId, formerClientId, senderIsClient: true, isRead: false);
        await SetLinkActiveAsync(formerPublicId, false);

        var allResponse = await http.GetAsync("/conversations", TestContext.Current.CancellationToken);
        (await Deserialize(allResponse))!.Should().ContainSingle(c => c.Participant.Id == formerClientId,
            "filter=All (the default) keeps today's plain, link-state-agnostic query");

        var unreadResponse = await http.GetAsync(
            "/conversations?filter=UnreadMessages", TestContext.Current.CancellationToken);
        (await Deserialize(unreadResponse))!.Should().BeEmpty(
            "the roster-driven filter path only includes live links — a former client is excluded " +
            "regardless of its conversation's unread count");
    }

    [Fact]
    public async Task List_UnreadMessagesFilter_IncludesPreLinkConversation_ExcludesFormerFlagged()
    {
        var (http, trainerId) = await SetupTrainerAsync();
        var preLinkClientId = await RegisterUnlinkedClientAsync();
        await SeedPreLinkConversationAsync(trainerId, preLinkClientId, isFormer: false);
        var formerClientId = await RegisterUnlinkedClientAsync();
        await SeedPreLinkConversationAsync(trainerId, formerClientId, isFormer: true);

        var response = await http.GetAsync(
            "/conversations?filter=UnreadMessages", TestContext.Current.CancellationToken);
        var body = await Deserialize(response);

        body!.Should().ContainSingle(c => c.Participant.Id == preLinkClientId);
        body.Should().NotContain(c => c.Participant.Id == formerClientId);
    }

    [Fact]
    public async Task List_ArchivedIsAppliedAfterTheFilterChip()
    {
        var (http, trainerId) = await SetupTrainerAsync();
        var (clientId, _) = await SetupLinkedClientAsync(trainerId);
        var conversationId = await SeedConversationWithMessageAsync(
            trainerId, clientId, senderIsClient: true, isRead: false);
        await ArchiveByProfessionalAsync(conversationId);

        var activeResponse = await http.GetAsync(
            "/conversations?filter=UnreadMessages", TestContext.Current.CancellationToken);
        (await Deserialize(activeResponse))!.Should().BeEmpty("archived conversations are excluded from the active view");

        var archivedResponse = await http.GetAsync(
            "/conversations?filter=UnreadMessages&archived=true", TestContext.Current.CancellationToken);
        (await Deserialize(archivedResponse))!.Should().ContainSingle(c => c.Participant.Id == clientId);
    }

    // ── authorization ────────────────────────────────────────────────────────

    [Fact]
    public async Task List_ClientCaller_NonAllFilter_Returns400()
    {
        var http = factory.CreateClient();
        var email = UniqueEmail("client-role");
        await TestHelpers.RegisterAsync(http, email, Password, "Test", "Client", "Client");
        var (token, _) = await TestHelpers.LoginAsync(http, email, Password);
        TestHelpers.SetBearerToken(http, token);

        var response = await http.GetAsync("/conversations?filter=UnreadMessages", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task List_ClientCaller_FilterAll_Returns200()
    {
        var http = factory.CreateClient();
        var email = UniqueEmail("client-all");
        await TestHelpers.RegisterAsync(http, email, Password, "Test", "Client", "Client");
        var (token, _) = await TestHelpers.LoginAsync(http, email, Password);
        TestHelpers.SetBearerToken(http, token);

        var response = await http.GetAsync("/conversations?filter=All", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

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

    private async Task<(Guid ClientUserId, Guid ClientPublicId)> SetupLinkedClientAsync(
        Guid professionalUserId, bool canViewNutritionPlans = true, bool canViewTrainingPlans = true)
    {
        var clientUserId = await TestHelpers.RegisterLinkedClientAsync(
            factory, professionalUserId, TestContext.Current.CancellationToken,
            canViewNutritionPlans, canViewTrainingPlans);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var clientProfile = await db.ClientProfiles.AsNoTracking()
            .FirstAsync(cp => cp.UserId == clientUserId, TestContext.Current.CancellationToken);

        return (clientUserId, clientProfile.PublicId);
    }

    private async Task SetLinkActiveAsync(Guid clientPublicId, bool isActive)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var clientProfile = await db.ClientProfiles
            .FirstAsync(cp => cp.PublicId == clientPublicId, TestContext.Current.CancellationToken);
        var link = await db.ClientProfessionalLinks
            .FirstAsync(l => l.ClientProfileId == clientProfile.Id, TestContext.Current.CancellationToken);
        link.ConnectionState = isActive ? ConnectionState.Connected : ConnectionState.Ended;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

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

    private async Task SeedPreLinkConversationAsync(Guid professionalUserId, Guid clientUserId, bool isFormer)
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
            IsRead = false
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
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

    private async Task ArchiveByProfessionalAsync(Guid conversationPublicId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var conversation = await db.Conversations
            .FirstAsync(c => c.PublicId == conversationPublicId, TestContext.Current.CancellationToken);
        conversation.ArchivedByProfessionalAt = DateTime.UtcNow;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task SeedActiveTrainingPlanAsync(
        Guid authorUserId, Guid clientUserId, DateTime? startDate, int weekCount = 4, string name = "Training Plan")
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        await mongo.TrainingPlans.InsertOneAsync(new TrainingPlan
        {
            ExternalId = Guid.NewGuid(),
            ClientId = clientUserId,
            TrainerId = authorUserId,
            Name = name,
            Status = TrainingPlanStatus.Active,
            StartDate = startDate,
            Weeks = Enumerable.Range(1, weekCount).Select(n => new TrainingWeek { WeekNumber = n }).ToList()
        }, cancellationToken: TestContext.Current.CancellationToken);
    }

    private static async Task<List<ConversationDto>?> Deserialize(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<List<ConversationDto>>(
            JsonOptions, TestContext.Current.CancellationToken);

    // ── Local response DTOs (per slice rules — no cross-feature imports) ─────

    private sealed class ConversationDto
    {
        public Guid? Id { get; set; }
        public ParticipantDto Participant { get; set; } = null!;
        public int UnreadCount { get; set; }
        public bool IsFormer { get; set; }
    }

    private sealed class ParticipantDto
    {
        public Guid Id { get; set; }
        public Guid? ClientPublicId { get; set; }
    }

    private sealed class FilterCountsResponse
    {
        public int All { get; set; }
        public int NoMessages { get; set; }
    }
}
