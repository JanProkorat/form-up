using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Messaging;

/// <summary>
/// Integration tests for <c>POST /conversations/{ConversationId}/messages/image-upload-url</c>.
/// </summary>
[Collection(TestCollection.Name)]
public class GenerateChatImageUploadUrlEndpointTests(FitnessApiFactory factory)
{
    private static string UniqueEmail() => $"{Guid.NewGuid():N}@chat-upload-test.com";
    private const string Password = "TestPass1!";

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Registers a trainer and a client, links them, starts a conversation, and returns their
    /// HTTP clients and the conversation's PublicId.
    /// </summary>
    private async Task<(HttpClient TrainerHttp, HttpClient ClientHttp, Guid ConversationId)>
        SetupConversationAsync()
    {
        var ct = TestContext.Current.CancellationToken;

        var trainerHttp = factory.CreateClient();
        var trainerEmail = UniqueEmail();
        await TestHelpers.RegisterAsync(trainerHttp, trainerEmail, Password, "Tina", "Trainer", "Trainer");
        var (trainerToken, _) = await TestHelpers.LoginAsync(trainerHttp, trainerEmail, Password);
        TestHelpers.SetBearerToken(trainerHttp, trainerToken);

        Guid profPublicId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var userId = (await db.Users.FirstAsync(u => u.Email == trainerEmail, ct)).Id;
            var profile = await db.ProfessionalProfiles.FirstAsync(p => p.UserId == userId, ct);
            profPublicId = profile.PublicId;
        }

        var clientHttp = factory.CreateClient();
        var clientEmail = UniqueEmail();
        await TestHelpers.RegisterAsync(clientHttp, clientEmail, Password, "Carl", "Client", "Client");
        var (clientToken, _) = await TestHelpers.LoginAsync(clientHttp, clientEmail, Password);
        TestHelpers.SetBearerToken(clientHttp, clientToken);

        await LinkAsync(trainerEmail, clientEmail);

        var convResp = await clientHttp.PostAsJsonAsync("/conversations", new { ParticipantId = profPublicId }, ct);
        convResp.EnsureSuccessStatusCode();
        var convBody = await convResp.Content.ReadFromJsonAsync<ConversationResponse>(cancellationToken: ct);

        return (trainerHttp, clientHttp, convBody!.Id);
    }

    /// <summary>Creates a live <c>ClientProfessionalLink</c> so a new conversation can start.</summary>
    private async Task LinkAsync(string trainerEmail, string clientEmail)
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

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

    /// <summary>
    /// Registers a user with a publicly-registerable role, then swaps it out-of-band for
    /// <see cref="UserRole.Admin"/> via <see cref="UserManager{TUser}"/> (Admin is intentionally
    /// excluded from public self-registration — see <c>RegisterValidator</c>), so the resulting
    /// caller holds ONLY the Admin role — none of Trainer/Nutritionist/Client — to prove the
    /// endpoint's role gate rejects a genuinely excluded role.
    /// </summary>
    private async Task<HttpClient> SetupAdminOnlyCallerAsync(CancellationToken ct)
    {
        var http = factory.CreateClient();
        var email = UniqueEmail();
        await TestHelpers.RegisterAsync(http, email, Password, "Ada", "Admin", "Client");

        using (var scope = factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await userManager.FindByEmailAsync(email);
            var removeResult = await userManager.RemoveFromRoleAsync(user!, nameof(UserRole.Client));
            removeResult.Succeeded.Should().BeTrue(
                $"RemoveFromRoleAsync failed: {string.Join(", ", removeResult.Errors.Select(e => e.Description))}");
            var addResult = await userManager.AddToRoleAsync(user!, nameof(UserRole.Admin));
            addResult.Succeeded.Should().BeTrue(
                $"AddToRoleAsync failed: {string.Join(", ", addResult.Errors.Select(e => e.Description))}");
        }

        var (accessToken, _) = await TestHelpers.LoginAsync(http, email, Password);
        TestHelpers.SetBearerToken(http, accessToken);

        return http;
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_ConversationDoesNotExist_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        var (trainerHttp, _, _) = await SetupConversationAsync();

        var response = await trainerHttp.PostAsJsonAsync(
            $"/conversations/{Guid.NewGuid()}/messages/image-upload-url",
            new { ContentType = "image/jpeg", SizeBytes = 1024 },
            ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task HandleAsync_CallerNotParticipant_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _, conversationId) = await SetupConversationAsync();

        var outsiderHttp = factory.CreateClient();
        var outsiderEmail = UniqueEmail();
        await TestHelpers.RegisterAsync(outsiderHttp, outsiderEmail, Password, "Otto", "Outsider", "Client");
        var (outsiderToken, _) = await TestHelpers.LoginAsync(outsiderHttp, outsiderEmail, Password);
        TestHelpers.SetBearerToken(outsiderHttp, outsiderToken);

        var response = await outsiderHttp.PostAsJsonAsync(
            $"/conversations/{conversationId}/messages/image-upload-url",
            new { ContentType = "image/jpeg", SizeBytes = 1024 },
            ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task HandleAsync_DisallowedContentType_Returns400()
    {
        // End-to-end confirmation that the narrower chat allowlist (jpeg/png/webp) is wired
        // through the validator, not just unit-tested in isolation — heic passes the SHARED
        // ImageUploadService's allowlist but must be rejected here.
        var ct = TestContext.Current.CancellationToken;
        var (trainerHttp, _, conversationId) = await SetupConversationAsync();

        var response = await trainerHttp.PostAsJsonAsync(
            $"/conversations/{conversationId}/messages/image-upload-url",
            new { ContentType = "image/heic", SizeBytes = 1024 },
            ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task HandleAsync_OversizeDeclaredSize_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;
        var (trainerHttp, _, conversationId) = await SetupConversationAsync();

        var response = await trainerHttp.PostAsJsonAsync(
            $"/conversations/{conversationId}/messages/image-upload-url",
            new { ContentType = "image/jpeg", SizeBytes = 6L * 1024 * 1024 },
            ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task HandleAsync_ValidRequest_ReturnsUploadUrlAndUploadId()
    {
        var ct = TestContext.Current.CancellationToken;
        var (trainerHttp, _, conversationId) = await SetupConversationAsync();

        var response = await trainerHttp.PostAsJsonAsync(
            $"/conversations/{conversationId}/messages/image-upload-url",
            new { ContentType = "image/jpeg", SizeBytes = 1024 },
            ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<UploadUrlResponse>(cancellationToken: ct);

        body.Should().NotBeNull();
        body!.UploadId.Should().NotBeEmpty();
        body.UploadUrl.Should().Contain($"chat-uploads/{conversationId}/");
        body.UploadUrl.Should().Contain(body.UploadId.ToString());
    }

    [Fact]
    public async Task HandleAsync_ClientCaller_AlsoAllowed()
    {
        // The upload-url endpoint is shared by all three Messaging roles — a Client caller
        // starting the image share (not just the professional) must also succeed.
        var ct = TestContext.Current.CancellationToken;
        var (_, clientHttp, conversationId) = await SetupConversationAsync();

        var response = await clientHttp.PostAsJsonAsync(
            $"/conversations/{conversationId}/messages/image-upload-url",
            new { ContentType = "image/png", SizeBytes = 2048 },
            ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private record ConversationResponse(Guid Id);

    private record UploadUrlResponse(string UploadUrl, Guid UploadId);
}
