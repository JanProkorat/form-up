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
/// Integration tests for the link-liveness gate <c>StartConversationEndpoint</c> now enforces
/// (#1095): a first message requires a currently live link, but reopening an existing
/// conversation never checks link liveness — closes the pre-existing hole where any trainer
/// could open a thread with any client. Also covers the pending-join-request exception: a
/// Client caller with no live link may still start a conversation if they have a Pending
/// <see cref="ClientRequest"/> to that professional (mobile's join-then-message flow).
/// </summary>
[Collection(TestCollection.Name)]
public class StartConversationLinkTests(FitnessApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static string UniqueEmail(string tag) => $"{Guid.NewGuid():N}@start-conv-link-{tag}.com";
    private const string Password = "TestPass1!";

    [Fact]
    public async Task StartConversation_UnknownClientPublicId_Returns404()
    {
        var (trainerHttp, _) = await SetupTrainerAsync();

        var resp = await trainerHttp.PostAsJsonAsync(
            "/conversations", new { ParticipantId = Guid.NewGuid() }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task StartConversation_UnknownProfessionalPublicId_Returns404()
    {
        var (clientHttp, _) = await SetupUnlinkedClientAsync();

        var resp = await clientHttp.PostAsJsonAsync(
            "/conversations", new { ParticipantId = Guid.NewGuid() }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task StartConversation_ProfessionalCaller_NoConversationAndNoLiveLink_Returns404WithNotLinkedCode()
    {
        var (trainerHttp, _) = await SetupTrainerAsync();
        var (_, clientPublicId) = await SetupUnlinkedClientAsync();

        var resp = await trainerHttp.PostAsJsonAsync(
            "/conversations", new { ParticipantId = clientPublicId }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetailsDto>(
            JsonOptions, TestContext.Current.CancellationToken);
        problem!.ErrorCode.Should().Be("NOT_LINKED_TO_CLIENT");
    }

    [Fact]
    public async Task StartConversation_ClientCaller_NoConversationAndNoLiveLink_Returns404WithNotLinkedCode()
    {
        // Symmetric direction — the client is the caller, the professional is the participant.
        var (_, trainerUserId, trainerPublicId) = await SetupTrainerWithIdsAsync();
        _ = trainerUserId;
        var (clientHttp, _) = await SetupUnlinkedClientAsync();

        var resp = await clientHttp.PostAsJsonAsync(
            "/conversations", new { ParticipantId = trainerPublicId }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetailsDto>(
            JsonOptions, TestContext.Current.CancellationToken);
        problem!.ErrorCode.Should().Be("NOT_LINKED_TO_CLIENT");
    }

    [Fact]
    public async Task StartConversation_LiveLink_NoConversation_CreatesOne()
    {
        var (trainerHttp, trainerUserId) = await SetupTrainerAsync();
        var (_, clientPublicId) = await SetupLinkedClientAsync(trainerUserId);

        var resp = await trainerHttp.PostAsJsonAsync(
            "/conversations", new { ParticipantId = clientPublicId }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task StartConversation_ExistingConversation_FormerLink_StillReturnsExisting()
    {
        var (trainerHttp, trainerUserId) = await SetupTrainerAsync();
        var (_, clientPublicId) = await SetupLinkedClientAsync(trainerUserId);

        // First call, while the link is live, creates the conversation.
        var createResp = await trainerHttp.PostAsJsonAsync(
            "/conversations", new { ParticipantId = clientPublicId }, TestContext.Current.CancellationToken);
        createResp.StatusCode.Should().Be(HttpStatusCode.OK);

        // Deactivate the link — simulates a former collaboration.
        await SetLinkActiveAsync(clientPublicId, false);

        // Reopening the existing thread must succeed regardless of link liveness.
        var reopenResp = await trainerHttp.PostAsJsonAsync(
            "/conversations", new { ParticipantId = clientPublicId }, TestContext.Current.CancellationToken);
        reopenResp.StatusCode.Should().Be(HttpStatusCode.OK,
            "an existing conversation must always be reopenable, even after the link ends");
    }

    [Fact]
    public async Task StartConversation_ClientCaller_PendingJoinRequest_NoLiveLink_Returns200Creates()
    {
        var (_, trainerUserId, trainerPublicId) = await SetupTrainerWithIdsAsync();
        var (clientHttp, clientUserId, _) = await SetupUnlinkedClientWithIdsAsync();

        await SeedClientRequestAsync(clientUserId, trainerUserId, ClientRequestStatus.Pending);

        var resp = await clientHttp.PostAsJsonAsync(
            "/conversations", new { ParticipantId = trainerPublicId }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "a client with a pending join request may start the intro chat before the coach accepts (#1095)");
    }

    [Fact]
    public async Task StartConversation_ClientCaller_RejectedJoinRequest_NoLiveLink_Returns404()
    {
        var (_, trainerUserId, trainerPublicId) = await SetupTrainerWithIdsAsync();
        var (clientHttp, clientUserId, _) = await SetupUnlinkedClientWithIdsAsync();

        await SeedClientRequestAsync(clientUserId, trainerUserId, ClientRequestStatus.Rejected);

        var resp = await clientHttp.PostAsJsonAsync(
            "/conversations", new { ParticipantId = trainerPublicId }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a rejected join request does not grant the pending-request exception");
    }

    [Fact]
    public async Task StartConversation_ClientCaller_AcceptedThenEndedJoinRequest_NoLiveLink_Returns404()
    {
        var (_, trainerUserId, trainerPublicId) = await SetupTrainerWithIdsAsync();
        var (clientHttp, clientUserId, clientPublicId) = await SetupUnlinkedClientWithIdsAsync();

        await SeedClientRequestAsync(clientUserId, trainerUserId, ClientRequestStatus.Accepted);

        // The collaboration existed and has since ended — a formerly-active link, now
        // deactivated — and no conversation exists yet for this pair.
        await SeedFormerLinkAsync(clientPublicId, trainerPublicId);

        var resp = await clientHttp.PostAsJsonAsync(
            "/conversations", new { ParticipantId = trainerPublicId }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "an accepted-then-ended request does not grant the pending-request exception");
    }

    [Fact]
    public async Task StartConversation_ProfessionalCaller_PendingRequestFromClient_NoLiveLink_Returns404()
    {
        // Unchanged path: the pending-request exception only applies when the CALLER is the
        // Client. A professional caller gets no exception even if the same pending row exists.
        var (trainerHttp, trainerUserId) = await SetupTrainerAsync();
        var (_, clientUserId, clientPublicId) = await SetupUnlinkedClientWithIdsAsync();

        await SeedClientRequestAsync(clientUserId, trainerUserId, ClientRequestStatus.Pending);

        var resp = await trainerHttp.PostAsJsonAsync(
            "/conversations", new { ParticipantId = clientPublicId }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetailsDto>(
            JsonOptions, TestContext.Current.CancellationToken);
        problem!.ErrorCode.Should().Be("NOT_LINKED_TO_CLIENT");
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

    private async Task<(HttpClient Http, Guid UserId, Guid PublicId)> SetupTrainerWithIdsAsync()
    {
        var (http, userId) = await SetupTrainerAsync();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var profile = await db.ProfessionalProfiles.AsNoTracking()
            .FirstAsync(pp => pp.UserId == userId, TestContext.Current.CancellationToken);

        return (http, userId, profile.PublicId);
    }

    private async Task<(HttpClient Http, Guid PublicId)> SetupUnlinkedClientAsync()
    {
        var http = factory.CreateClient();
        var email = UniqueEmail("client");

        await TestHelpers.RegisterAsync(http, email, Password, "Test", "Client", "Client");
        var (token, _) = await TestHelpers.LoginAsync(http, email, Password);
        TestHelpers.SetBearerToken(http, token);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await db.Users.AsNoTracking()
            .FirstAsync(u => u.Email == email, TestContext.Current.CancellationToken);
        var profile = await db.ClientProfiles.AsNoTracking()
            .FirstAsync(cp => cp.UserId == user.Id, TestContext.Current.CancellationToken);

        return (http, profile.PublicId);
    }

    private async Task<(HttpClient Http, Guid UserId, Guid PublicId)> SetupUnlinkedClientWithIdsAsync()
    {
        var (http, publicId) = await SetupUnlinkedClientAsync();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var profile = await db.ClientProfiles.AsNoTracking()
            .FirstAsync(cp => cp.PublicId == publicId, TestContext.Current.CancellationToken);

        return (http, profile.UserId, publicId);
    }

    /// <summary>
    /// Seeds a <see cref="ClientRequest"/> row directly (bypassing the send/accept/reject
    /// endpoints) so a test can pin an arbitrary <see cref="ClientRequestStatus"/>.
    /// </summary>
    private async Task SeedClientRequestAsync(Guid clientUserId, Guid professionalUserId, ClientRequestStatus status)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var clientProfile = await db.ClientProfiles
            .FirstAsync(cp => cp.UserId == clientUserId, TestContext.Current.CancellationToken);
        var professionalProfile = await db.ProfessionalProfiles
            .FirstAsync(pp => pp.UserId == professionalUserId, TestContext.Current.CancellationToken);

        db.ClientRequests.Add(new ClientRequest
        {
            PublicId = Guid.NewGuid(),
            ClientProfileId = clientProfile.Id,
            ProfessionalProfileId = professionalProfile.Id,
            Status = status,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Seeds an already-deactivated <see cref="ClientProfessionalLink"/> — simulates a
    /// collaboration that existed and has since ended, without going through the live-link
    /// registration + deactivation round trip.
    /// </summary>
    private async Task SeedFormerLinkAsync(Guid clientPublicId, Guid professionalPublicId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var clientProfile = await db.ClientProfiles
            .FirstAsync(cp => cp.PublicId == clientPublicId, TestContext.Current.CancellationToken);
        var professionalProfile = await db.ProfessionalProfiles
            .FirstAsync(pp => pp.PublicId == professionalPublicId, TestContext.Current.CancellationToken);

        db.ClientProfessionalLinks.Add(new ClientProfessionalLink
        {
            PublicId = Guid.NewGuid(),
            ProfessionalProfileId = professionalProfile.Id,
            ClientProfileId = clientProfile.Id,
            ProfessionalRole = UserRole.Trainer,
            ConnectionState = ConnectionState.Ended,
            DateCreated = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<(Guid ClientUserId, Guid ClientPublicId)> SetupLinkedClientAsync(Guid trainerUserId)
    {
        var clientUserId = await TestHelpers.RegisterLinkedClientAsync(
            factory, trainerUserId, TestContext.Current.CancellationToken);

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

    // ── Local response DTOs (per slice rules — no cross-feature imports) ─────

    private sealed class ProblemDetailsDto
    {
        public string? ErrorCode { get; set; }
    }
}
