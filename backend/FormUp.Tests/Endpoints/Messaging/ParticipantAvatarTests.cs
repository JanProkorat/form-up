using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Messaging;

/// <summary>
/// Integration tests verifying that <c>ParticipantDto.AvatarBlobUrl</c> is
/// correctly projected by <c>GET /conversations</c> and <c>POST /conversations</c>
/// using the two-tier fallback pattern:
///   professional participant → ProfessionalProfile.AvatarBlobUrl ?? User.AvatarBlobUrl
///   client participant       → User.AvatarBlobUrl
/// </summary>
[Collection(TestCollection.Name)]
public class ParticipantAvatarTests(FitnessApiFactory factory)
{
    private static string UniqueEmail() => $"{Guid.NewGuid():N}@msg-avatar-test.com";
    private const string Password = "TestPass1!";

    /// <summary>
    /// Requests a real, identity-scoped avatar blobUrl via the given upload-url route —
    /// exactly what a legitimate client does before calling the paired confirm endpoint.
    /// Since #658, both confirm endpoints reject any blobUrl that isn't the caller's own
    /// presigned key, so tests can no longer PUT a hand-picked string.
    /// </summary>
    private static async Task<string> GetOwnUploadBlobUrlAsync(HttpClient client, string uploadUrlRoute)
    {
        var resp = await client.PostAsJsonAsync(
            uploadUrlRoute,
            new { ContentType = "image/jpeg", SizeBytes = 1024 },
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<UploadUrlResponse>(
            cancellationToken: TestContext.Current.CancellationToken);

        body.Should().NotBeNull();
        return body!.BlobUrl;
    }

    /// <summary>
    /// Creates a live <c>ClientProfessionalLink</c> between the two registered users so
    /// <c>POST /conversations</c> can start a NEW conversation — a first message requires a
    /// currently live link (StartConversationEndpoint, #1095).
    /// </summary>
    private async Task LinkAsync(string trainerEmail, string clientEmail)
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider
            .GetRequiredService<FormUp.Application.Infrastructure.Data.ApplicationDbContext>();

        var trainerUserId = (await db.Users.FirstAsync(u => u.Email == trainerEmail, ct)).Id;
        var clientUserId = (await db.Users.FirstAsync(u => u.Email == clientEmail, ct)).Id;
        var clientProfile = await db.ClientProfiles.FirstAsync(cp => cp.UserId == clientUserId, ct);
        var professionalProfile = await db.ProfessionalProfiles.FirstAsync(pp => pp.UserId == trainerUserId, ct);

        db.ClientProfessionalLinks.Add(new ClientProfessionalLink
        {
            PublicId = Guid.NewGuid(),
            ProfessionalProfileId = professionalProfile.Id,
            ClientProfileId = clientProfile.Id,
            ProfessionalRole = UserRole.Trainer,
            ConnectionState = ConnectionState.Connected,
            CanViewNutritionPlans = true,
            CanViewTrainingPlans = true,
            DateCreated = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }

    // ── POST /conversations ──────────────────────────────────────────────────

    /// <summary>
    /// When the professional-profile avatar has been uploaded, StartConversation
    /// returns that URL as the participant's avatar for the client caller.
    /// </summary>
    [Fact]
    public async Task StartConversation_AsClient_ParticipantAvatarIsFromProfessionalProfile()
    {
        var http = factory.CreateClient();

        // Register trainer and set a professional-profile avatar
        var trainerEmail = UniqueEmail();
        await TestHelpers.RegisterAsync(http, trainerEmail, Password, "Alice", "Trainer", "Trainer");
        var (trainerToken, _) = await TestHelpers.LoginAsync(http, trainerEmail, Password);
        TestHelpers.SetBearerToken(http, trainerToken);

        var profAvatarUrl = await GetOwnUploadBlobUrlAsync(http, "/professionals/me/avatar/upload-url");
        await http.PutAsJsonAsync(
            "/professionals/me/avatar",
            new { BlobUrl = profAvatarUrl },
            TestContext.Current.CancellationToken);

        // Resolve trainer's ProfessionalProfile.PublicId
        Guid profPublicId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider
                .GetRequiredService<FormUp.Application.Infrastructure.Data.ApplicationDbContext>();
            var userId = (await db.Users.FirstAsync(
                u => u.Email == trainerEmail,
                TestContext.Current.CancellationToken)).Id;
            var profile = await db.ProfessionalProfiles.FirstAsync(
                p => p.UserId == userId,
                TestContext.Current.CancellationToken);
            profPublicId = profile.PublicId;
        }

        // Register client and start the conversation
        var clientHttp = factory.CreateClient();
        var clientEmail = UniqueEmail();
        await TestHelpers.RegisterAsync(clientHttp, clientEmail, Password, "Bob", "Client", "Client");
        var (clientToken, _) = await TestHelpers.LoginAsync(clientHttp, clientEmail, Password);
        TestHelpers.SetBearerToken(clientHttp, clientToken);

        await LinkAsync(trainerEmail, clientEmail);

        var resp = await clientHttp.PostAsJsonAsync(
            "/conversations",
            new { ParticipantId = profPublicId },
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<ConversationResponse>(
            cancellationToken: TestContext.Current.CancellationToken);

        body.Should().NotBeNull();
        body!.Participant.AvatarBlobUrl.Should().Be(profAvatarUrl);
    }

    /// <summary>
    /// When the professional-profile avatar is null but the user has a user-level
    /// avatar, StartConversation returns the user-level URL (fallback tier).
    /// </summary>
    [Fact]
    public async Task StartConversation_AsClient_ProfProfileAvatarNull_FallsBackToUserAvatar()
    {
        var http = factory.CreateClient();

        // Register trainer with only a user-level avatar (no professional-profile avatar)
        var trainerEmail = UniqueEmail();
        await TestHelpers.RegisterAsync(http, trainerEmail, Password, "Carol", "Trainer", "Trainer");
        var (trainerToken, _) = await TestHelpers.LoginAsync(http, trainerEmail, Password);
        TestHelpers.SetBearerToken(http, trainerToken);

        var userAvatarUrl = await GetOwnUploadBlobUrlAsync(http, "/users/me/avatar/upload-url");
        await http.PutAsJsonAsync(
            "/users/me/avatar",
            new { BlobUrl = userAvatarUrl },
            TestContext.Current.CancellationToken);
        // Leave ProfessionalProfile.AvatarBlobUrl as null

        Guid profPublicId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider
                .GetRequiredService<FormUp.Application.Infrastructure.Data.ApplicationDbContext>();
            var userId = (await db.Users.FirstAsync(
                u => u.Email == trainerEmail,
                TestContext.Current.CancellationToken)).Id;
            var profile = await db.ProfessionalProfiles.FirstAsync(
                p => p.UserId == userId,
                TestContext.Current.CancellationToken);
            profPublicId = profile.PublicId;
            // Confirm professional-profile avatar really is null
            profile.AvatarBlobUrl.Should().BeNull();
        }

        var clientHttp = factory.CreateClient();
        var clientEmail = UniqueEmail();
        await TestHelpers.RegisterAsync(clientHttp, clientEmail, Password, "Dave", "Client", "Client");
        var (clientToken, _) = await TestHelpers.LoginAsync(clientHttp, clientEmail, Password);
        TestHelpers.SetBearerToken(clientHttp, clientToken);

        await LinkAsync(trainerEmail, clientEmail);

        var resp = await clientHttp.PostAsJsonAsync(
            "/conversations",
            new { ParticipantId = profPublicId },
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<ConversationResponse>(
            cancellationToken: TestContext.Current.CancellationToken);

        body.Should().NotBeNull();
        body!.Participant.AvatarBlobUrl.Should().Be(userAvatarUrl,
            "professional-profile avatar is null, so the user-level avatar must be used");
    }

    // ── GET /conversations ───────────────────────────────────────────────────

    /// <summary>
    /// After a conversation is started, GET /conversations as the client returns
    /// the professional-profile avatar on the participant (professional side).
    /// </summary>
    [Fact]
    public async Task GetConversations_AsClient_ParticipantAvatarIsFromProfessionalProfile()
    {
        var http = factory.CreateClient();

        var trainerEmail = UniqueEmail();
        await TestHelpers.RegisterAsync(http, trainerEmail, Password, "Eve", "Trainer", "Trainer");
        var (trainerToken, _) = await TestHelpers.LoginAsync(http, trainerEmail, Password);
        TestHelpers.SetBearerToken(http, trainerToken);

        var profAvatarUrl = await GetOwnUploadBlobUrlAsync(http, "/professionals/me/avatar/upload-url");
        await http.PutAsJsonAsync(
            "/professionals/me/avatar",
            new { BlobUrl = profAvatarUrl },
            TestContext.Current.CancellationToken);

        Guid profPublicId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider
                .GetRequiredService<FormUp.Application.Infrastructure.Data.ApplicationDbContext>();
            var userId = (await db.Users.FirstAsync(
                u => u.Email == trainerEmail,
                TestContext.Current.CancellationToken)).Id;
            var profile = await db.ProfessionalProfiles.FirstAsync(
                p => p.UserId == userId,
                TestContext.Current.CancellationToken);
            profPublicId = profile.PublicId;
        }

        var clientHttp = factory.CreateClient();
        var clientEmail = UniqueEmail();
        await TestHelpers.RegisterAsync(clientHttp, clientEmail, Password, "Frank", "Client", "Client");
        var (clientToken, _) = await TestHelpers.LoginAsync(clientHttp, clientEmail, Password);
        TestHelpers.SetBearerToken(clientHttp, clientToken);

        await LinkAsync(trainerEmail, clientEmail);

        // Start conversation so it appears in the list
        await clientHttp.PostAsJsonAsync(
            "/conversations",
            new { ParticipantId = profPublicId },
            TestContext.Current.CancellationToken);

        var listResp = await clientHttp.GetAsync(
            "/conversations",
            TestContext.Current.CancellationToken);

        listResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var conversations = await listResp.Content.ReadFromJsonAsync<List<ConversationResponse>>(
            cancellationToken: TestContext.Current.CancellationToken);

        conversations.Should().NotBeNull();
        var conv = conversations!.FirstOrDefault(c => c.Participant.AvatarBlobUrl == profAvatarUrl);
        conv.Should().NotBeNull("the conversation with the professional whose avatar was set must appear");
    }

    // ── Local response DTOs (per slice rules — no cross-feature imports) ─────

    private record ParticipantResponse(Guid Id, string Name, string? AvatarBlobUrl);
    private record ConversationResponse(Guid Id, ParticipantResponse Participant);
    private record UploadUrlResponse(string UploadUrl, string BlobUrl);
}
