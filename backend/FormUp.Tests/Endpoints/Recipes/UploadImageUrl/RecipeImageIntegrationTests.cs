using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FormUp.Tests.Infrastructure;

namespace FormUp.Tests.Endpoints.Recipes.UploadImageUrl;

/// <summary>
/// Integration tests for the recipe image upload flow:
///   POST /recipes/{recipeId}/image/upload-url?slot={main|gallery}  (UploadRecipeImageUrlEndpoint)
///   PUT  /recipes/{recipeId}/image?slot={main|gallery}             (ConfirmRecipeImageEndpoint)
///   GET  /recipes/{recipeId}                                       (GetRecipeEndpoint — image reflection)
///
/// These tests use a real HTTP stack (FitnessApiFactory with Testcontainers) so
/// the authentication/authorisation middleware and the full DI pipeline are
/// exercised.  NSubstitute unit tests in the sibling file cover logic-level
/// branches; this file fills the AC gap by proving the role gate actually fires.
/// </summary>
[Collection(TestCollection.Shard3)]
public class RecipeImageIntegrationTests(FitnessApiFactory factory)
{
    // ── Helpers ────────────────────────────────────────────────────────────────

    private static string UniqueEmail(string tag) => $"{Guid.NewGuid():N}@recipe-img-{tag}.test";

    private static async Task<string> SeedUserAsync(
        HttpClient client, string role, string tag = "")
    {
        var email = UniqueEmail(tag.Length > 0 ? tag : role.ToLowerInvariant());
        await TestHelpers.RegisterAsync(client, email, "TestPass1!", "Test", "User", role);
        var (accessToken, _) = await TestHelpers.LoginAsync(client, email, "TestPass1!");
        return accessToken;
    }

    private static string NewKey(Guid recipeId, string slot) =>
        $"recipes/{recipeId}/{slot}-{Guid.NewGuid():N}.jpg";

    private static async Task<Guid> CreateRecipeAsync(HttpClient client, string ownerToken, Guid foodId)
    {
        TestHelpers.SetBearerToken(client, ownerToken);
        var response = await client.PostAsJsonAsync("/recipes", new
        {
            Name = $"Test Recipe {Guid.NewGuid():N}",
            MealTypes = new[] { "Lunch" },
            Foods = new[]
            {
                new { FoodExternalId = foodId, AmountGrams = 100m }
            }
        }, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created,
            "recipe creation must succeed for the integration test to proceed");

        var body = await response.Content.ReadFromJsonAsync<RecipeRef>(
            cancellationToken: TestContext.Current.CancellationToken);
        return body!.RecipeId;
    }

    // ── Happy path: upload-url — main slot ─────────────────────────────────────

    /// <summary>
    /// A nutritionist requests an upload URL for the main slot of their own recipe.
    /// Expects 200 with both <c>uploadUrl</c> and <c>blobUrl</c>;
    /// <c>blobUrl</c> must equal <c>recipes/{recipeId}/main.jpg</c>.
    /// </summary>
    [Fact]
    public async Task UploadUrl_Nutritionist_MainSlot_HappyPath_Returns200WithBlobUrl()
    {
        var client = factory.CreateClient();
        var token = await SeedUserAsync(client, "Nutritionist", "main-happy");
        var foodId = await TestHelpers.CreateFoodAsync(client, token, TestContext.Current.CancellationToken);
        var recipeId = await CreateRecipeAsync(client, token, foodId);

        TestHelpers.SetBearerToken(client, token);
        var response = await client.PostAsJsonAsync(
            $"/recipes/{recipeId}/image/upload-url?slot=main",
            new { ContentType = "image/jpeg", SizeBytes = 102400L },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<UploadUrlResponse>(
            cancellationToken: TestContext.Current.CancellationToken);

        body.Should().NotBeNull();
        body!.UploadUrl.Should().NotBeNullOrEmpty();
        body.BlobUrl.Should().MatchRegex($"^recipes/{recipeId}/main-[0-9a-f]{{32}}\\.jpg$");
    }

    // ── Happy path: upload-url — gallery slot (0th entry) ──────────────────────

    /// <summary>
    /// A nutritionist requests an upload URL for the gallery.
    /// <c>blobUrl</c> must match <c>recipes/{recipeId}/gallery-{guid:N}.jpg</c>.
    /// </summary>
    [Fact]
    public async Task UploadUrl_Nutritionist_GallerySlot_FirstEntry_Returns200WithGuidBlobUrl()
    {
        var client = factory.CreateClient();
        var token = await SeedUserAsync(client, "Nutritionist", "gallery-happy");
        var foodId = await TestHelpers.CreateFoodAsync(client, token, TestContext.Current.CancellationToken);
        var recipeId = await CreateRecipeAsync(client, token, foodId);

        TestHelpers.SetBearerToken(client, token);
        var response = await client.PostAsJsonAsync(
            $"/recipes/{recipeId}/image/upload-url?slot=gallery",
            new { ContentType = "image/jpeg", SizeBytes = 102400L },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<UploadUrlResponse>(
            cancellationToken: TestContext.Current.CancellationToken);

        body.Should().NotBeNull();
        body!.BlobUrl.Should().MatchRegex($"^recipes/{recipeId}/gallery-[0-9a-f]{{32}}\\.jpg$");
    }

    // ── Ownership gate: upload-url ─────────────────────────────────────────────

    /// <summary>
    /// Nutritionist B requests an upload URL for a recipe owned by nutritionist A.
    /// Expects 400 with RECIPE_NOT_OWNED.
    /// </summary>
    [Fact]
    public async Task UploadUrl_NonOwner_Returns400WithRecipeNotOwnedError()
    {
        var client = factory.CreateClient();

        var tokenA = await SeedUserAsync(client, "Nutritionist", "upload-owner-a");
        var foodId = await TestHelpers.CreateFoodAsync(client, tokenA, TestContext.Current.CancellationToken);
        var recipeId = await CreateRecipeAsync(client, tokenA, foodId);

        var tokenB = await SeedUserAsync(client, "Nutritionist", "upload-owner-b");
        TestHelpers.SetBearerToken(client, tokenB);

        var response = await client.PostAsJsonAsync(
            $"/recipes/{recipeId}/image/upload-url?slot=main",
            new { ContentType = "image/jpeg", SizeBytes = 102400L },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        raw.Should().Contain("RECIPE_NOT_OWNED",
            "the Problem Details payload must carry the RECIPE_NOT_OWNED error code");
    }

    // ── Gallery cap: upload-url ────────────────────────────────────────────────

    /// <summary>
    /// Gallery is filled to 6 entries via confirm, then a 7th upload-url request returns 400 RECIPE_GALLERY_FULL.
    /// </summary>
    [Fact]
    public async Task UploadUrl_GalleryFull_Returns400WithRecipeGalleryFullError()
    {
        var client = factory.CreateClient();
        var token = await SeedUserAsync(client, "Nutritionist", "gallery-full-upload");
        var foodId = await TestHelpers.CreateFoodAsync(client, token, TestContext.Current.CancellationToken);
        var recipeId = await CreateRecipeAsync(client, token, foodId);

        TestHelpers.SetBearerToken(client, token);

        // Confirm 6 gallery entries
        for (var i = 0; i < 6; i++)
        {
            var confirmResponse = await client.PutAsJsonAsync(
                $"/recipes/{recipeId}/image?slot=gallery",
                new { BlobUrl = NewKey(recipeId, "gallery") },
                TestContext.Current.CancellationToken);

            confirmResponse.StatusCode.Should().Be(HttpStatusCode.NoContent,
                $"confirming gallery entry {i} should succeed");
        }

        // Now try to request a 7th upload URL
        var response = await client.PostAsJsonAsync(
            $"/recipes/{recipeId}/image/upload-url?slot=gallery",
            new { ContentType = "image/jpeg", SizeBytes = 102400L },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        raw.Should().Contain("RECIPE_GALLERY_FULL",
            "the Problem Details payload must carry the RECIPE_GALLERY_FULL error code");
    }

    // ── Happy path: confirm + GET reflection — main slot ──────────────────────

    /// <summary>
    /// Nutritionist confirms a main image via PUT /recipes/{id}/image?slot=main.
    /// Subsequent GET /recipes/{id} must return the DTO with imageUrl set.
    /// </summary>
    [Fact]
    public async Task ConfirmImage_MainSlot_HappyPath_Returns204_AndGetReflectsImageUrl()
    {
        var client = factory.CreateClient();
        var token = await SeedUserAsync(client, "Nutritionist", "confirm-main-happy");
        var foodId = await TestHelpers.CreateFoodAsync(client, token, TestContext.Current.CancellationToken);
        var recipeId = await CreateRecipeAsync(client, token, foodId);

        var blobUrl = NewKey(recipeId, "main");

        TestHelpers.SetBearerToken(client, token);

        var putResponse = await client.PutAsJsonAsync(
            $"/recipes/{recipeId}/image?slot=main",
            new { BlobUrl = blobUrl },
            TestContext.Current.CancellationToken);

        putResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var getResponse = await client.GetAsync(
            $"/recipes/{recipeId}",
            TestContext.Current.CancellationToken);

        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var recipe = await getResponse.Content.ReadFromJsonAsync<RecipeDetailResponse>(
            cancellationToken: TestContext.Current.CancellationToken);

        recipe.Should().NotBeNull();
        recipe!.RecipeId.Should().Be(recipeId);
        recipe.ImageUrl.Should().Be(blobUrl);
    }

    // ── Happy path: confirm + GET reflection — gallery slot ───────────────────

    /// <summary>
    /// Nutritionist confirms a gallery image via PUT /recipes/{id}/image?slot=gallery.
    /// Subsequent GET /recipes/{id} must include the new entry in galleryImageUrls.
    /// </summary>
    [Fact]
    public async Task ConfirmImage_GallerySlot_HappyPath_Returns204_AndGetReflectsGalleryImageUrls()
    {
        var client = factory.CreateClient();
        var token = await SeedUserAsync(client, "Nutritionist", "confirm-gallery-happy");
        var foodId = await TestHelpers.CreateFoodAsync(client, token, TestContext.Current.CancellationToken);
        var recipeId = await CreateRecipeAsync(client, token, foodId);

        var galleryBlobUrl = NewKey(recipeId, "gallery");

        TestHelpers.SetBearerToken(client, token);

        var putResponse = await client.PutAsJsonAsync(
            $"/recipes/{recipeId}/image?slot=gallery",
            new { BlobUrl = galleryBlobUrl },
            TestContext.Current.CancellationToken);

        putResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var getResponse = await client.GetAsync(
            $"/recipes/{recipeId}",
            TestContext.Current.CancellationToken);

        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var recipe = await getResponse.Content.ReadFromJsonAsync<RecipeDetailResponse>(
            cancellationToken: TestContext.Current.CancellationToken);

        recipe.Should().NotBeNull();
        recipe!.GalleryImageUrls.Should().ContainSingle(u => u == galleryBlobUrl);
    }

    // ── Gallery cap: confirm ───────────────────────────────────────────────────

    /// <summary>
    /// Attempting to confirm a 7th gallery entry via PUT /recipes/{id}/image?slot=gallery
    /// when the gallery already has 6 entries returns 400 RECIPE_GALLERY_FULL.
    /// </summary>
    [Fact]
    public async Task ConfirmImage_GalleryOverflow_Returns400WithRecipeGalleryFullError()
    {
        var client = factory.CreateClient();
        var token = await SeedUserAsync(client, "Nutritionist", "gallery-full-confirm");
        var foodId = await TestHelpers.CreateFoodAsync(client, token, TestContext.Current.CancellationToken);
        var recipeId = await CreateRecipeAsync(client, token, foodId);

        TestHelpers.SetBearerToken(client, token);

        // Confirm 6 gallery entries
        for (var i = 0; i < 6; i++)
        {
            var confirmResponse = await client.PutAsJsonAsync(
                $"/recipes/{recipeId}/image?slot=gallery",
                new { BlobUrl = NewKey(recipeId, "gallery") },
                TestContext.Current.CancellationToken);

            confirmResponse.StatusCode.Should().Be(HttpStatusCode.NoContent,
                $"confirming gallery entry {i} should succeed");
        }

        // Attempt a 7th confirm — must fail with RECIPE_GALLERY_FULL
        var response = await client.PutAsJsonAsync(
            $"/recipes/{recipeId}/image?slot=gallery",
            new { BlobUrl = NewKey(recipeId, "gallery") },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        raw.Should().Contain("RECIPE_GALLERY_FULL",
            "the Problem Details payload must carry the RECIPE_GALLERY_FULL error code");
    }

    // ── Ownership check on confirm ─────────────────────────────────────────────

    /// <summary>
    /// Nutritionist B tries to confirm an image on a recipe owned by nutritionist A.
    /// Expects 400 with RECIPE_NOT_OWNED.
    /// </summary>
    [Fact]
    public async Task ConfirmImage_NonOwner_Returns400WithRecipeNotOwnedError()
    {
        var client = factory.CreateClient();

        var tokenA = await SeedUserAsync(client, "Nutritionist", "confirm-owner-a");
        var foodId = await TestHelpers.CreateFoodAsync(client, tokenA, TestContext.Current.CancellationToken);
        var recipeId = await CreateRecipeAsync(client, tokenA, foodId);

        var tokenB = await SeedUserAsync(client, "Nutritionist", "confirm-owner-b");
        TestHelpers.SetBearerToken(client, tokenB);

        var response = await client.PutAsJsonAsync(
            $"/recipes/{recipeId}/image?slot=main",
            new { BlobUrl = NewKey(recipeId, "main") },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        raw.Should().Contain("RECIPE_NOT_OWNED",
            "the Problem Details payload must carry the RECIPE_NOT_OWNED error code");
    }

    // ── Blob URL must match the presigned key ──────────────────────────────────

    [Theory]
    [InlineData("main")]
    [InlineData("gallery")]
    public async Task ConfirmImage_ForeignBlobUrl_Returns400InvalidBlobUrl_AndLeavesRecipeUntouched(string slot)
    {
        var client = factory.CreateClient();
        var token = await SeedUserAsync(client, "Nutritionist", $"confirm-bad-url-{slot}");
        var foodId = await TestHelpers.CreateFoodAsync(client, token, TestContext.Current.CancellationToken);
        var recipeId = await CreateRecipeAsync(client, token, foodId);

        TestHelpers.SetBearerToken(client, token);

        // Another recipe's key, an external URL and legacy count/fixed names are all rejected.
        var otherSlot = slot == "main" ? "gallery" : "main";
        foreach (var badUrl in new[]
                 {
                     NewKey(Guid.NewGuid(), slot), "https://evil.example/x.jpg", $"recipes/{recipeId}/gallery-3.jpg",
                     $"recipes/{recipeId}/{slot}.jpg", NewKey(recipeId, otherSlot),
                 })
        {
            var response = await client.PutAsJsonAsync(
                $"/recipes/{recipeId}/image?slot={slot}",
                new { BlobUrl = badUrl },
                TestContext.Current.CancellationToken);

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, badUrl);
            (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
                .Should().Contain("INVALID_BLOB_URL");
        }

        var recipe = await (await client.GetAsync($"/recipes/{recipeId}", TestContext.Current.CancellationToken))
            .Content.ReadFromJsonAsync<RecipeDetailResponse>(cancellationToken: TestContext.Current.CancellationToken);
        recipe!.ImageUrl.Should().BeNull();
        recipe.GalleryImageUrls.Should().BeEmpty();
    }

    // ── Remove main image ──────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteImage_Owner_ClearsMainImageOnly_AndIsIdempotent()
    {
        var client = factory.CreateClient();
        var token = await SeedUserAsync(client, "Nutritionist", "delete-image-owner");
        var foodId = await TestHelpers.CreateFoodAsync(client, token, TestContext.Current.CancellationToken);
        var recipeId = await CreateRecipeAsync(client, token, foodId);

        TestHelpers.SetBearerToken(client, token);
        (await client.PutAsJsonAsync($"/recipes/{recipeId}/image?slot=main",
            new { BlobUrl = NewKey(recipeId, "main") }, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.PutAsJsonAsync($"/recipes/{recipeId}/image?slot=gallery",
            new { BlobUrl = NewKey(recipeId, "gallery") }, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var response = await client.DeleteAsync($"/recipes/{recipeId}/image", TestContext.Current.CancellationToken);
            response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        var recipe = await (await client.GetAsync($"/recipes/{recipeId}", TestContext.Current.CancellationToken))
            .Content.ReadFromJsonAsync<RecipeDetailResponse>(cancellationToken: TestContext.Current.CancellationToken);
        recipe!.ImageUrl.Should().BeNull();
        recipe.GalleryImageUrls.Should().ContainSingle();
    }

    [Fact]
    public async Task DeleteImage_NonOwner_Returns400RecipeNotOwned_AndKeepsImage()
    {
        var client = factory.CreateClient();
        var tokenA = await SeedUserAsync(client, "Nutritionist", "delete-image-a");
        var foodId = await TestHelpers.CreateFoodAsync(client, tokenA, TestContext.Current.CancellationToken);
        var recipeId = await CreateRecipeAsync(client, tokenA, foodId);
        var mainUrl = NewKey(recipeId, "main");
        (await client.PutAsJsonAsync($"/recipes/{recipeId}/image?slot=main",
            new { BlobUrl = mainUrl }, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var tokenB = await SeedUserAsync(client, "Nutritionist", "delete-image-b");
        TestHelpers.SetBearerToken(client, tokenB);

        var response = await client.DeleteAsync($"/recipes/{recipeId}/image", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("RECIPE_NOT_OWNED");

        TestHelpers.SetBearerToken(client, tokenA);
        var recipe = await (await client.GetAsync($"/recipes/{recipeId}", TestContext.Current.CancellationToken))
            .Content.ReadFromJsonAsync<RecipeDetailResponse>(cancellationToken: TestContext.Current.CancellationToken);
        recipe!.ImageUrl.Should().Be(mainUrl);
    }

    [Fact]
    public async Task DeleteImage_MissingRecipe_Returns404()
    {
        var client = factory.CreateClient();
        var token = await SeedUserAsync(client, "Nutritionist", "delete-image-missing");
        TestHelpers.SetBearerToken(client, token);

        var response = await client.DeleteAsync($"/recipes/{Guid.NewGuid()}/image", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Local response DTOs (per slice rules — no cross-feature imports) ────────

    private record UploadUrlResponse(string UploadUrl, string BlobUrl);
    private record RecipeRef(Guid RecipeId);
    private record RecipeDetailResponse(Guid RecipeId, string Name, string? ImageUrl, List<string> GalleryImageUrls);
}
