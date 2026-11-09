using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Features.Messaging.Shared;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Messaging;

/// <summary>
/// Integration tests for <c>POST /conversations/{ConversationId}/messages</c> — the image
/// attachment path. Text-only sends are covered incidentally by <see cref="GetMessagesEndpointTests"/>'s
/// setup helper and are not re-asserted here.
/// </summary>
[Collection(TestCollection.Name)]
public class SendMessageEndpointTests(FitnessApiFactory factory)
{
    private static string UniqueEmail() => $"{Guid.NewGuid():N}@sendmsg-test.com";
    private const string Password = "TestPass1!";

    // A minimal valid JPEG signature — enough for ChatImagePolicy.SniffContentType to detect
    // "image/jpeg"; the sniff only inspects the first bytes, so no full JPEG body is needed.
    private static readonly byte[] ValidJpegBytes = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46];

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<(HttpClient TrainerHttp, HttpClient ClientHttp, Guid ConversationId, Guid TrainerUserId)>
        SetupConversationAsync()
    {
        var ct = TestContext.Current.CancellationToken;

        var trainerHttp = factory.CreateClient();
        var trainerEmail = UniqueEmail();
        await TestHelpers.RegisterAsync(trainerHttp, trainerEmail, Password, "Tina", "Trainer", "Trainer");
        var (trainerToken, _) = await TestHelpers.LoginAsync(trainerHttp, trainerEmail, Password);
        TestHelpers.SetBearerToken(trainerHttp, trainerToken);

        Guid profPublicId;
        Guid trainerUserId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            trainerUserId = (await db.Users.FirstAsync(u => u.Email == trainerEmail, ct)).Id;
            var profile = await db.ProfessionalProfiles.FirstAsync(p => p.UserId == trainerUserId, ct);
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

        return (trainerHttp, clientHttp, convBody!.Id, trainerUserId);
    }

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

    /// <summary>Generates an upload URL and returns the uploadId, then seeds the staged bytes directly on the fake.</summary>
    private async Task<Guid> SeedStagedImageAsync(
        HttpClient http, Guid conversationId, Guid callerUserId, byte[] data, string declaredContentType = "image/jpeg")
    {
        var ct = TestContext.Current.CancellationToken;

        var uploadUrlResp = await http.PostAsJsonAsync(
            $"/conversations/{conversationId}/messages/image-upload-url",
            new { ContentType = declaredContentType, SizeBytes = data.Length },
            ct);
        uploadUrlResp.EnsureSuccessStatusCode();
        var body = await uploadUrlResp.Content.ReadFromJsonAsync<UploadUrlResponse>(cancellationToken: ct);

        var stagingPath = ChatImagePolicy.BuildStagingContainerPath(conversationId, callerUserId, body!.UploadId);
        var fakeBlobStorage = (FakeBlobStorageService)factory.Services.GetRequiredService<IBlobStorageService>();
        fakeBlobStorage.SeedObject(stagingPath, data);

        return body.UploadId;
    }

    private async Task<Guid> ResolveUserIdAsync(string email)
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return (await db.Users.FirstAsync(u => u.Email == email, ct)).Id;
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
        var (trainerHttp, _, _, _) = await SetupConversationAsync();

        var response = await trainerHttp.PostAsJsonAsync(
            $"/conversations/{Guid.NewGuid()}/messages", new { Text = "hi" }, ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task HandleAsync_CallerNotParticipant_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, _, conversationId, _) = await SetupConversationAsync();

        var outsiderHttp = factory.CreateClient();
        var outsiderEmail = UniqueEmail();
        await TestHelpers.RegisterAsync(outsiderHttp, outsiderEmail, Password, "Otto", "Outsider", "Client");
        var (outsiderToken, _) = await TestHelpers.LoginAsync(outsiderHttp, outsiderEmail, Password);
        TestHelpers.SetBearerToken(outsiderHttp, outsiderToken);

        var response = await outsiderHttp.PostAsJsonAsync(
            $"/conversations/{conversationId}/messages", new { Text = "hi" }, ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task HandleAsync_ImageUploadIdWithNoStagedObject_Returns400_NoMessageCreated()
    {
        var ct = TestContext.Current.CancellationToken;
        var (trainerHttp, _, conversationId, _) = await SetupConversationAsync();

        var response = await trainerHttp.PostAsJsonAsync(
            $"/conversations/{conversationId}/messages",
            new { Text = "", ImageUploadId = Guid.NewGuid() },
            ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var messages = await GetMessagesAsync(trainerHttp, conversationId, ct);
        messages.Should().BeEmpty("a missing staged upload must not create a message row");
    }

    [Fact]
    public async Task HandleAsync_StagedBytesFailSniff_Returns400_DeletesStaging_NoMessageCreated()
    {
        var ct = TestContext.Current.CancellationToken;
        var (trainerHttp, _, conversationId, trainerUserId) = await SetupConversationAsync();

        var htmlBytes = "<html><body>not an image</body></html>"u8.ToArray();
        var uploadId = await SeedStagedImageAsync(trainerHttp, conversationId, trainerUserId, htmlBytes);

        var response = await trainerHttp.PostAsJsonAsync(
            $"/conversations/{conversationId}/messages",
            new { Text = "", ImageUploadId = uploadId },
            ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var stagingPath = ChatImagePolicy.BuildStagingContainerPath(conversationId, trainerUserId, uploadId);
        var fakeBlobStorage = (FakeBlobStorageService)factory.Services.GetRequiredService<IBlobStorageService>();
        fakeBlobStorage.DeletedPaths.Should().Contain(stagingPath);

        var messages = await GetMessagesAsync(trainerHttp, conversationId, ct);
        messages.Should().BeEmpty("a signature mismatch must not create a message row");
    }

    [Fact]
    public async Task HandleAsync_StagedObjectExceedsSizeCap_Returns413_DeletesStaging_NoMessageCreated()
    {
        var ct = TestContext.Current.CancellationToken;
        var (trainerHttp, _, conversationId, trainerUserId) = await SetupConversationAsync();

        // The upload-url validator caps the DECLARED size at 5 MiB, so declare something small,
        // then seed bytes that actually exceed the cap directly on the fake — simulating a
        // presigned PUT that enforced no length.
        var oversizedBytes = new byte[5 * 1024 * 1024 + 1];
        ValidJpegBytes.CopyTo(oversizedBytes, 0);

        var uploadUrlResp = await trainerHttp.PostAsJsonAsync(
            $"/conversations/{conversationId}/messages/image-upload-url",
            new { ContentType = "image/jpeg", SizeBytes = 1024 },
            ct);
        uploadUrlResp.EnsureSuccessStatusCode();
        var uploadBody = await uploadUrlResp.Content.ReadFromJsonAsync<UploadUrlResponse>(cancellationToken: ct);

        var stagingPath = ChatImagePolicy.BuildStagingContainerPath(conversationId, trainerUserId, uploadBody!.UploadId);
        var fakeBlobStorage = (FakeBlobStorageService)factory.Services.GetRequiredService<IBlobStorageService>();
        fakeBlobStorage.SeedObject(stagingPath, oversizedBytes);

        var response = await trainerHttp.PostAsJsonAsync(
            $"/conversations/{conversationId}/messages",
            new { Text = "", ImageUploadId = uploadBody.UploadId },
            ct);

        response.StatusCode.Should().Be((HttpStatusCode)413);
        fakeBlobStorage.DeletedPaths.Should().Contain(stagingPath);

        var messages = await GetMessagesAsync(trainerHttp, conversationId, ct);
        messages.Should().BeEmpty("an oversized staged object must not create a message row");
    }

    [Fact]
    public async Task HandleAsync_ImageOnlyMessage_Returns200_WithSignedImageUrl_EmptyText()
    {
        var ct = TestContext.Current.CancellationToken;
        var (trainerHttp, clientHttp, conversationId, trainerUserId) = await SetupConversationAsync();

        var uploadId = await SeedStagedImageAsync(trainerHttp, conversationId, trainerUserId, ValidJpegBytes);

        var response = await trainerHttp.PostAsJsonAsync(
            $"/conversations/{conversationId}/messages",
            new { Text = "", ImageUploadId = uploadId, ImageWidth = 640, ImageHeight = 480 },
            ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<SendMessageResponseDto>(cancellationToken: ct);

        body.Should().NotBeNull();
        body!.Text.Should().BeEmpty();
        body.ImageUrl.Should().NotBeNullOrEmpty();
        body.ImageWidth.Should().Be(640);
        body.ImageHeight.Should().Be(480);

        // Recipient sees the image in the list too, routed through GenerateReadUrlAsync rather
        // than the stored ImageBlobUrl being echoed unsigned.
        var messages = await GetMessagesAsync(clientHttp, conversationId, ct);
        messages.Should().ContainSingle();
        messages[0].ImageUrl.Should().NotBeNullOrEmpty();

        var fakeBlobStorage = (FakeBlobStorageService)factory.Services.GetRequiredService<IBlobStorageService>();
        var finalPath = ChatImagePolicy.BuildFinalContainerPath(conversationId, body.Id, "jpg");
        fakeBlobStorage.SignedUrlRequests.Should().Contain(finalPath);
    }

    [Fact]
    public async Task HandleAsync_ImageOnlyMessage_PersistsImageBlobUrlAsBuildPublicUrlForm()
    {
        // Asserts SendMessageEndpoint stores blobStorage.BuildPublicUrl(finalPath) — the form
        // MinioBlobStorageService.GenerateReadUrlAsync's TryExtractContainerPath can reverse —
        // rather than the bare finalPath it used to store (root cause of #1096: chat images
        // returned imageUrl: "" because the real service fails closed on a bare path). This test
        // host uses FakeBlobStorageService, whose BuildPublicUrl is deliberately an identity
        // function (see its own doc comment), so it cannot by itself distinguish the fixed call
        // from the pre-fix bug — MinioBlobStorageServiceTests'
        // GenerateReadUrlAsync_ChatImageBarePath_FailsClosed_NotBugSymptom /
        // _ChatImagePublicUrlForm_RoundTripsToSignedUrl pair proves the real-service contract
        // this call must satisfy. What this test proves is the source-level invariant: the
        // persisted value is exactly blobStorage.BuildPublicUrl(finalPath), not a raw path built
        // by hand.
        var ct = TestContext.Current.CancellationToken;
        var (trainerHttp, _, conversationId, trainerUserId) = await SetupConversationAsync();

        var uploadId = await SeedStagedImageAsync(trainerHttp, conversationId, trainerUserId, ValidJpegBytes);

        var response = await trainerHttp.PostAsJsonAsync(
            $"/conversations/{conversationId}/messages",
            new { Text = "", ImageUploadId = uploadId },
            ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<SendMessageResponseDto>(cancellationToken: ct);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var blobStorage = scope.ServiceProvider.GetRequiredService<IBlobStorageService>();
        var message = await db.ChatMessages.AsNoTracking().FirstAsync(m => m.PublicId == body!.Id, ct);

        var finalPath = ChatImagePolicy.BuildFinalContainerPath(conversationId, body!.Id, "jpg");
        message.ImageBlobUrl.Should().Be(blobStorage.BuildPublicUrl(finalPath));
    }

    [Fact]
    public async Task HandleAsync_NullTextWithImageUploadId_Returns200_WithEmptyText()
    {
        // An explicit JSON "text": null overrides SendMessageRequest.Text's string.Empty default
        // (System.Text.Json deserializes null into a non-nullable reference property when
        // RespectNullableAnnotations is not enabled — it isn't, here). The validator's Must rule
        // treats null the same as empty via IsNullOrWhiteSpace, so a null text with a valid
        // ImageUploadId passes validation and reaches the handler, which must not NRE on
        // req.Text.Trim() (root cause of #1096's image-then-500 orphaned-blob bug).
        var ct = TestContext.Current.CancellationToken;
        var (trainerHttp, _, conversationId, trainerUserId) = await SetupConversationAsync();

        var uploadId = await SeedStagedImageAsync(trainerHttp, conversationId, trainerUserId, ValidJpegBytes);

        var response = await trainerHttp.PostAsJsonAsync(
            $"/conversations/{conversationId}/messages",
            new { Text = (string?)null, ImageUploadId = uploadId },
            ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<SendMessageResponseDto>(cancellationToken: ct);

        body.Should().NotBeNull();
        body!.Text.Should().BeEmpty();
        body.ImageUrl.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task HandleAsync_TextAndImage_BothPersisted()
    {
        var ct = TestContext.Current.CancellationToken;
        var (trainerHttp, _, conversationId, trainerUserId) = await SetupConversationAsync();

        var uploadId = await SeedStagedImageAsync(trainerHttp, conversationId, trainerUserId, ValidJpegBytes);

        var response = await trainerHttp.PostAsJsonAsync(
            $"/conversations/{conversationId}/messages",
            new { Text = "check this out", ImageUploadId = uploadId },
            ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<SendMessageResponseDto>(cancellationToken: ct);

        body!.Text.Should().Be("check this out");
        body.ImageUrl.Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// <c>SendMessageRequest</c> carries no <c>kind</c>/<c>eventType</c> field (#1100 C6) — extra
    /// JSON properties on the body are silently ignored by model binding, so a client can never
    /// forge an event row through this endpoint. The persisted message stays plain Text.
    /// </summary>
    [Fact]
    public async Task HandleAsync_BodyCarriesKindAndEventType_IgnoredByBinding_PersistsPlainTextOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        var (trainerHttp, _, conversationId, _) = await SetupConversationAsync();

        var response = await trainerHttp.PostAsJsonAsync(
            $"/conversations/{conversationId}/messages",
            new { Text = "hi", Kind = "Event", EventType = "Accepted" },
            ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var conv = await db.Conversations.FirstAsync(c => c.PublicId == conversationId, ct);
        var message = await db.ChatMessages.AsNoTracking().FirstAsync(m => m.ConversationId == conv.Id, ct);

        message.Kind.Should().Be(ChatMessageKind.Text);
        message.EventType.Should().BeNull();
    }

    /// <summary>
    /// A conversation whose last message was a cooperation event (<c>LastMessageEventType</c>
    /// set) must have that field reset to null once a plain message is sent — #1100 C6, "RULING
    /// preview field".
    /// </summary>
    [Fact]
    public async Task HandleAsync_Success_ResetsConversationLastMessageEventType()
    {
        var ct = TestContext.Current.CancellationToken;
        var (trainerHttp, _, conversationId, trainerUserId) = await SetupConversationAsync();

        Guid clientUserId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var conv = await db.Conversations.FirstAsync(c => c.PublicId == conversationId, ct);
            clientUserId = conv.ClientUserId;

            var seedService = scope.ServiceProvider.GetRequiredService<IConversationSeedService>();
            await seedService.AppendCooperationEventAsync(
                trainerUserId, clientUserId, trainerUserId, ChatEventType.Accepted,
                sourceId: Guid.NewGuid(), messageText: null, createConversationIfMissing: false, ct);

            var convAfterEvent = await db.Conversations.AsNoTracking().FirstAsync(c => c.PublicId == conversationId, ct);
            convAfterEvent.LastMessageEventType.Should().Be(ChatEventType.Accepted,
                "test setup must actually leave the conversation with an event-typed last message");
        }

        var response = await trainerHttp.PostAsJsonAsync(
            $"/conversations/{conversationId}/messages", new { Text = "back to normal" }, ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var convAfterMessage = await verifyDb.Conversations.AsNoTracking().FirstAsync(c => c.PublicId == conversationId, ct);
        convAfterMessage.LastMessageEventType.Should().BeNull(
            "a plain-text send must reset LastMessageEventType — it is not still-event once superseded");
    }

    // ── Local helpers ────────────────────────────────────────────────────────

    private static async Task<List<SendMessageResponseDto>> GetMessagesAsync(
        HttpClient http, Guid conversationId, CancellationToken ct)
    {
        var response = await http.GetAsync($"/conversations/{conversationId}/messages", ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<GetMessagesResponseDto>(cancellationToken: ct);
        return body!.Items;
    }

    private record ConversationResponse(Guid Id);
    private record UploadUrlResponse(string UploadUrl, Guid UploadId);

    private record SendMessageResponseDto(
        Guid Id, Guid SenderId, string Text, DateTime Timestamp, bool IsRead,
        string? ImageUrl, int? ImageWidth, int? ImageHeight);

    private record GetMessagesResponseDto(List<SendMessageResponseDto> Items, Guid? Cursor);
}
