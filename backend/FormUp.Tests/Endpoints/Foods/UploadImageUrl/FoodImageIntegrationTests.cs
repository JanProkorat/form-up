using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FormUp.Tests.Infrastructure;

namespace FormUp.Tests.Endpoints.Foods.UploadImageUrl;

/// <summary>
/// Integration tests for the food image upload flow:
///   POST /foods/{foodId}/image/upload-url?slot=main|gallery  (UploadFoodImageUrlEndpoint)
///   PUT  /foods/{foodId}/image?slot=main|gallery             (ConfirmFoodImageEndpoint)
///   GET  /foods/{foodId}                                     (GetFoodEndpoint — image reflection)
///
/// These tests use a real HTTP stack (FitnessApiFactory with Testcontainers) so
/// the authentication/authorisation middleware and the full DI pipeline are
/// exercised.  NSubstitute unit tests in the sibling file cover logic-level
/// branches; this file fills the AC gap by proving the role gate actually fires.
/// </summary>
[Collection(TestCollection.Shard4)]
public class FoodImageIntegrationTests(FitnessApiFactory factory)
{
    // ── Helpers ────────────────────────────────────────────────────────────────

    private static string UniqueEmail(string tag) => $"{Guid.NewGuid():N}@food-img-{tag}.test";

    /// <summary>
    /// Registers a user with the given role and returns a bearer token.
    /// </summary>
    private static async Task<string> SeedUserAsync(
        HttpClient client, string role, string tag = "")
    {
        var email = UniqueEmail(tag.Length > 0 ? tag : role.ToLowerInvariant());
        await TestHelpers.RegisterAsync(client, email, "TestPass1!", "Test", "User", role);
        var (accessToken, _) = await TestHelpers.LoginAsync(client, email, "TestPass1!");
        return accessToken;
    }

    /// <summary>
    /// Calls <c>POST /foods/{foodId}/image/upload-url?slot=main</c> as <paramref name="ownerToken"/>
    /// and returns the issued <c>blobUrl</c>. Confirm now validates the blobUrl against this
    /// food's presigned key, so main-slot confirm tests must use a real issued URL rather than
    /// a hand-built relative path.
    /// </summary>
    private static async Task<string> RequestMainSlotBlobUrlAsync(HttpClient client, string ownerToken, Guid foodId)
    {
        TestHelpers.SetBearerToken(client, ownerToken);

        var response = await client.PostAsJsonAsync(
            $"/foods/{foodId}/image/upload-url?slot=main",
            new { ContentType = "image/jpeg", SizeBytes = 102400L },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<UploadUrlResponse>(
            cancellationToken: TestContext.Current.CancellationToken);

        return body!.BlobUrl;
    }

    // ── Happy path: upload-url (main slot) ────────────────────────────────────

    /// <summary>
    /// A nutritionist requests an upload URL for their own food (main slot).
    /// Expects 200 with both <c>uploadUrl</c> and <c>blobUrl</c>;
    /// <c>blobUrl</c> must be a unique <c>foods/{foodId}/main-{guid}.jpg</c> key.
    /// </summary>
    [Fact]
    public async Task UploadUrl_Nutritionist_MainSlot_HappyPath_Returns200WithBlobUrl()
    {
        var client = factory.CreateClient();
        var token = await SeedUserAsync(client, "Nutritionist");
        var foodId = await TestHelpers.CreateFoodAsync(client, token, TestContext.Current.CancellationToken);

        TestHelpers.SetBearerToken(client, token);
        var response = await client.PostAsJsonAsync(
            $"/foods/{foodId}/image/upload-url?slot=main",
            new { ContentType = "image/jpeg", SizeBytes = 102400L },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<UploadUrlResponse>(
            cancellationToken: TestContext.Current.CancellationToken);

        body.Should().NotBeNull();
        body!.UploadUrl.Should().NotBeNullOrEmpty();
        body.BlobUrl.Should().MatchRegex($"^foods/{foodId}/main-[0-9a-f]{{32}}\\.jpg$");
    }

    // ── Happy path: upload-url (gallery slot) ─────────────────────────────────

    /// <summary>
    /// A nutritionist requests an upload URL for their own food (gallery slot, empty gallery).
    /// Expects 200 with a unique <c>foods/{foodId}/gallery-{guid}.jpg</c> <c>blobUrl</c>.
    /// </summary>
    [Fact]
    public async Task UploadUrl_Nutritionist_GallerySlot_EmptyGallery_Returns200WithUniqueGalleryBlobUrl()
    {
        var client = factory.CreateClient();
        var token = await SeedUserAsync(client, "Nutritionist", "gallery-upload");
        var foodId = await TestHelpers.CreateFoodAsync(client, token, TestContext.Current.CancellationToken);

        TestHelpers.SetBearerToken(client, token);
        var response = await client.PostAsJsonAsync(
            $"/foods/{foodId}/image/upload-url?slot=gallery",
            new { ContentType = "image/jpeg", SizeBytes = 102400L },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<UploadUrlResponse>(
            cancellationToken: TestContext.Current.CancellationToken);

        body.Should().NotBeNull();
        body!.BlobUrl.Should().MatchRegex($"^foods/{foodId}/gallery-[0-9a-f]{{32}}\\.jpg$");
    }

    // ── Ownership check: upload-url ────────────────────────────────────────────

    /// <summary>
    /// Nutritionist B tries to get an upload URL for a food owned by nutritionist A.
    /// Expects 400 with error code FOOD_NOT_OWNED in the Problem Details payload.
    /// </summary>
    [Fact]
    public async Task UploadUrl_NonOwner_Returns400WithFoodNotOwnedError()
    {
        var client = factory.CreateClient();

        // Nutritionist A creates the food
        var tokenA = await SeedUserAsync(client, "Nutritionist", "upload-owner-a");
        var foodId = await TestHelpers.CreateFoodAsync(client, tokenA, TestContext.Current.CancellationToken);

        // Nutritionist B tries to get the upload URL
        var tokenB = await SeedUserAsync(client, "Nutritionist", "upload-owner-b");
        TestHelpers.SetBearerToken(client, tokenB);

        var response = await client.PostAsJsonAsync(
            $"/foods/{foodId}/image/upload-url?slot=main",
            new { ContentType = "image/jpeg", SizeBytes = 102400L },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        raw.Should().Contain("FOOD_NOT_OWNED",
            "the Problem Details payload must carry the FOOD_NOT_OWNED error code");
    }

    // ── Happy path: confirm + GET reflection (main slot) ──────────────────────

    /// <summary>
    /// Nutritionist confirms a main image URL via PUT /foods/{foodId}/image?slot=main.
    /// Subsequent GET /foods/{foodId} must return the DTO with imageUrl set.
    /// </summary>
    [Fact]
    public async Task ConfirmImage_MainSlot_HappyPath_Returns204_AndGetReflectsImageUrl()
    {
        var client = factory.CreateClient();
        var token = await SeedUserAsync(client, "Nutritionist", "confirm-happy");
        var foodId = await TestHelpers.CreateFoodAsync(client, token, TestContext.Current.CancellationToken);

        var blobUrl = await RequestMainSlotBlobUrlAsync(client, token, foodId);

        TestHelpers.SetBearerToken(client, token);

        // PUT to confirm the main image
        var putResponse = await client.PutAsJsonAsync(
            $"/foods/{foodId}/image?slot=main",
            new { BlobUrl = blobUrl },
            TestContext.Current.CancellationToken);

        putResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // GET must now return the food with imageUrl set
        var getResponse = await client.GetAsync(
            $"/foods/{foodId}",
            TestContext.Current.CancellationToken);

        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var food = await getResponse.Content.ReadFromJsonAsync<FoodResponse>(
            cancellationToken: TestContext.Current.CancellationToken);

        food.Should().NotBeNull();
        food!.FoodId.Should().Be(foodId);
        food.ImageUrl.Should().Be(blobUrl);
    }

    // ── Happy path: confirm + GET reflection (gallery slot) ───────────────────

    /// <summary>
    /// Nutritionist confirms a gallery image via PUT /foods/{foodId}/image?slot=gallery.
    /// Subsequent GET /foods/{foodId} must return the DTO with galleryImageUrls containing the blob URL.
    /// </summary>
    [Fact]
    public async Task ConfirmImage_GallerySlot_HappyPath_Returns204_AndGetReflectsGalleryUrl()
    {
        var client = factory.CreateClient();
        var token = await SeedUserAsync(client, "Nutritionist", "confirm-gallery");
        var foodId = await TestHelpers.CreateFoodAsync(client, token, TestContext.Current.CancellationToken);

        var galleryUrl = $"foods/{foodId}/gallery-{Guid.NewGuid():N}.jpg";

        TestHelpers.SetBearerToken(client, token);

        // PUT to confirm the gallery image
        var putResponse = await client.PutAsJsonAsync(
            $"/foods/{foodId}/image?slot=gallery",
            new { BlobUrl = galleryUrl },
            TestContext.Current.CancellationToken);

        putResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // GET must now return the food with galleryImageUrls containing the blob URL
        var getResponse = await client.GetAsync(
            $"/foods/{foodId}",
            TestContext.Current.CancellationToken);

        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var food = await getResponse.Content.ReadFromJsonAsync<FoodResponseWithGallery>(
            cancellationToken: TestContext.Current.CancellationToken);

        food.Should().NotBeNull();
        food!.FoodId.Should().Be(foodId);
        food.GalleryImageUrls.Should().ContainSingle(u => u == galleryUrl,
            "the confirmed gallery blob URL must appear in galleryImageUrls");
    }

    // ── Ownership check on confirm ─────────────────────────────────────────────

    /// <summary>
    /// Nutritionist B tries to confirm an image on a food owned by nutritionist A.
    /// Expects 400 with error code FOOD_NOT_OWNED in the Problem Details payload.
    /// </summary>
    [Fact]
    public async Task ConfirmImage_NonOwner_Returns400WithFoodNotOwnedError()
    {
        var client = factory.CreateClient();

        // Nutritionist A creates the food
        var tokenA = await SeedUserAsync(client, "Nutritionist", "owner-a");
        var foodId = await TestHelpers.CreateFoodAsync(client, tokenA, TestContext.Current.CancellationToken);

        // Nutritionist B tries to confirm an image on it
        var tokenB = await SeedUserAsync(client, "Nutritionist", "owner-b");
        TestHelpers.SetBearerToken(client, tokenB);

        var response = await client.PutAsJsonAsync(
            $"/foods/{foodId}/image?slot=main",
            new { BlobUrl = $"foods/{foodId}/main-{Guid.NewGuid():N}.jpg" },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        raw.Should().Contain("FOOD_NOT_OWNED",
            "the Problem Details payload must carry the FOOD_NOT_OWNED error code");
    }

    // ── Blob URL validation on confirm (both slots) ───────────────────────────

    /// <summary>
    /// A nutritionist tries to confirm the main image using another food's key
    /// (a well-formed <c>foods/{id}/main-{guid}.ext</c> URL, just for the wrong food). Expects 400 with
    /// error code INVALID_BLOB_URL — the blobUrl must match this food's own issued key.
    /// </summary>
    [Fact]
    public async Task ConfirmImage_MainSlot_ForeignBlobUrl_Returns400WithInvalidBlobUrlError()
    {
        var client = factory.CreateClient();
        var token = await SeedUserAsync(client, "Nutritionist", "foreign-blob-url");

        var foodId = await TestHelpers.CreateFoodAsync(client, token, TestContext.Current.CancellationToken);
        var otherFoodId = await TestHelpers.CreateFoodAsync(client, token, TestContext.Current.CancellationToken);

        TestHelpers.SetBearerToken(client, token);

        var response = await client.PutAsJsonAsync(
            $"/foods/{foodId}/image?slot=main",
            new { BlobUrl = $"foods/{otherFoodId}/main-{Guid.NewGuid():N}.jpg" },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        raw.Should().Contain("INVALID_BLOB_URL",
            "the Problem Details payload must carry the INVALID_BLOB_URL error code");
    }

    // ── Local response DTOs (per slice rules — no cross-feature imports) ────────

    private record UploadUrlResponse(string UploadUrl, string BlobUrl);

    private record FoodResponse(Guid FoodId, string Name, string? ImageUrl);

    private record FoodResponseWithGallery(Guid FoodId, string Name, string? ImageUrl, List<string> GalleryImageUrls);
}
