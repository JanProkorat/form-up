using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FormUp.Tests.Infrastructure;

namespace FormUp.Tests.Endpoints.Recipes;

/// <summary>
/// Integration tests for the recipe gallery picture flow: unique upload keys, remove and promote.
/// </summary>
[Collection(TestCollection.Shard3)]
public class RecipeGalleryIntegrationTests(FitnessApiFactory factory)
{
    private static async Task<string> SeedNutritionistAsync(HttpClient client, string tag)
    {
        var email = $"{Guid.NewGuid():N}@recipe-gallery-{tag}.test";
        await TestHelpers.RegisterAsync(client, email, "TestPass1!", "Test", "User", "Nutritionist");
        var (accessToken, _) = await TestHelpers.LoginAsync(client, email, "TestPass1!");
        return accessToken;
    }

    private static string NewKey(Guid recipeId, string slot) =>
        $"recipes/{recipeId}/{slot}-{Guid.NewGuid():N}.jpg";

    private async Task<(HttpClient Client, string Token, Guid RecipeId)> CreateOwnedRecipeAsync(string tag)
    {
        var client = factory.CreateClient();
        var token = await SeedNutritionistAsync(client, tag);
        var foodId = await TestHelpers.CreateFoodAsync(client, token, TestContext.Current.CancellationToken);
        TestHelpers.SetBearerToken(client, token);

        var response = await client.PostAsJsonAsync("/recipes", new
        {
            Name = $"Gallery Recipe {Guid.NewGuid():N}",
            MealTypes = new[] { "Lunch" },
            Foods = new[] { new { FoodExternalId = foodId, AmountGrams = 100m } }
        }, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await response.Content.ReadFromJsonAsync<RecipeRef>(
            cancellationToken: TestContext.Current.CancellationToken);
        return (client, token, body!.RecipeId);
    }

    private static async Task ConfirmAsync(HttpClient client, Guid recipeId, string slot, string blobUrl)
    {
        var response = await client.PutAsJsonAsync(
            $"/recipes/{recipeId}/image?slot={slot}", new { BlobUrl = blobUrl },
            TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    private static async Task<RecipeDetail> GetAsync(HttpClient client, Guid recipeId)
    {
        var response = await client.GetAsync($"/recipes/{recipeId}", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<RecipeDetail>(
            cancellationToken: TestContext.Current.CancellationToken))!;
    }

    private static Task<HttpResponseMessage> PromoteAsync(HttpClient client, Guid recipeId, string imageUrl) =>
        client.PostAsJsonAsync($"/recipes/{recipeId}/gallery/promote", new { ImageUrl = imageUrl },
            TestContext.Current.CancellationToken);

    private static Task<HttpResponseMessage> RemoveAsync(HttpClient client, Guid recipeId, string imageUrl) =>
        client.DeleteAsync($"/recipes/{recipeId}/gallery?imageUrl={Uri.EscapeDataString(imageUrl)}",
            TestContext.Current.CancellationToken);

    // ── Unique upload keys ─────────────────────────────────────────────────

    [Fact]
    public async Task UploadUrl_Gallery_TwiceAndAfterMiddleRemoval_NeverReusesAKey()
    {
        var (client, _, recipeId) = await CreateOwnedRecipeAsync("keys");
        var issued = new List<string>();

        for (var i = 0; i < 2; i++)
        {
            var response = await client.PostAsJsonAsync(
                $"/recipes/{recipeId}/image/upload-url?slot=gallery",
                new { ContentType = "image/jpeg", SizeBytes = 1024L }, TestContext.Current.CancellationToken);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            issued.Add((await response.Content.ReadFromJsonAsync<UploadUrl>(
                cancellationToken: TestContext.Current.CancellationToken))!.BlobUrl);
        }

        issued.Distinct().Should().HaveCount(2);

        var stored = new[] { NewKey(recipeId, "gallery"), NewKey(recipeId, "gallery"), NewKey(recipeId, "gallery") };
        foreach (var url in stored)
        {
            await ConfirmAsync(client, recipeId, "gallery", url);
        }

        (await RemoveAsync(client, recipeId, stored[1])).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var next = await client.PostAsJsonAsync(
            $"/recipes/{recipeId}/image/upload-url?slot=gallery",
            new { ContentType = "image/jpeg", SizeBytes = 1024L }, TestContext.Current.CancellationToken);
        var nextUrl = (await next.Content.ReadFromJsonAsync<UploadUrl>(
            cancellationToken: TestContext.Current.CancellationToken))!.BlobUrl;

        stored.Should().NotContain(nextUrl);
        issued.Should().NotContain(nextUrl);
    }

    [Fact]
    public async Task ConfirmImage_SameGalleryUrlTwice_SecondIsRejected_NoDuplicate()
    {
        var (client, _, recipeId) = await CreateOwnedRecipeAsync("dup");
        var url = NewKey(recipeId, "gallery");
        await ConfirmAsync(client, recipeId, "gallery", url);

        var second = await client.PutAsJsonAsync(
            $"/recipes/{recipeId}/image?slot=gallery", new { BlobUrl = url }, TestContext.Current.CancellationToken);

        second.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await second.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("INVALID_BLOB_URL");
        (await GetAsync(client, recipeId)).GalleryImageUrls.Should().ContainSingle();
    }

    // ── Remove ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task RemoveGalleryImage_Owner_RemovesOnlyThatUrl_KeepsOrder_IsIdempotent_AndKeepsVersion()
    {
        var (client, _, recipeId) = await CreateOwnedRecipeAsync("remove");
        var urls = new[] { NewKey(recipeId, "gallery"), NewKey(recipeId, "gallery"), NewKey(recipeId, "gallery") };
        foreach (var url in urls)
        {
            await ConfirmAsync(client, recipeId, "gallery", url);
        }

        var before = await GetAsync(client, recipeId);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            (await RemoveAsync(client, recipeId, urls[1])).StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        var after = await GetAsync(client, recipeId);
        after.GalleryImageUrls.Should().Equal(urls[0], urls[2]);
        after.Version.Should().Be(before.Version);
    }

    [Fact]
    public async Task RemoveGalleryImage_UrlNotPresent_Returns204_AndChangesNothing()
    {
        var (client, _, recipeId) = await CreateOwnedRecipeAsync("remove-absent");
        var url = NewKey(recipeId, "gallery");
        await ConfirmAsync(client, recipeId, "gallery", url);

        (await RemoveAsync(client, recipeId, NewKey(recipeId, "gallery"))).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await GetAsync(client, recipeId)).GalleryImageUrls.Should().Equal(url);
    }

    [Fact]
    public async Task RemoveGalleryImage_NonOwner_Returns400RecipeNotOwned_AndKeepsGallery()
    {
        var (client, ownerToken, recipeId) = await CreateOwnedRecipeAsync("remove-owner");
        var url = NewKey(recipeId, "gallery");
        await ConfirmAsync(client, recipeId, "gallery", url);

        TestHelpers.SetBearerToken(client, await SeedNutritionistAsync(client, "remove-other"));
        var response = await RemoveAsync(client, recipeId, url);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("RECIPE_NOT_OWNED");

        TestHelpers.SetBearerToken(client, ownerToken);
        (await GetAsync(client, recipeId)).GalleryImageUrls.Should().Equal(url);
    }

    [Fact]
    public async Task RemoveGalleryImage_MissingRecipe_Returns404()
    {
        var (client, _, _) = await CreateOwnedRecipeAsync("remove-missing");

        var response = await RemoveAsync(client, Guid.NewGuid(), "recipes/x/gallery-a.jpg");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RemoveGalleryImage_EmptyImageUrl_Returns400()
    {
        var (client, _, recipeId) = await CreateOwnedRecipeAsync("remove-empty");

        var response = await client.DeleteAsync($"/recipes/{recipeId}/gallery", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Promote ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Promote_WithMain_SwapsAtSameIndex_AndKeepsVersion()
    {
        var (client, _, recipeId) = await CreateOwnedRecipeAsync("promote-swap");
        var main = NewKey(recipeId, "main");
        var gallery = new[] { NewKey(recipeId, "gallery"), NewKey(recipeId, "gallery"), NewKey(recipeId, "gallery") };
        await ConfirmAsync(client, recipeId, "main", main);
        foreach (var url in gallery)
        {
            await ConfirmAsync(client, recipeId, "gallery", url);
        }

        var before = await GetAsync(client, recipeId);

        (await PromoteAsync(client, recipeId, gallery[1])).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var after = await GetAsync(client, recipeId);
        after.ImageUrl.Should().Be(gallery[1]);
        after.GalleryImageUrls.Should().Equal(gallery[0], main, gallery[2]);
        after.Version.Should().Be(before.Version);
    }

    [Fact]
    public async Task Promote_WithoutMain_MovesEntryOutOfGallery_KeepingOthersInPlace()
    {
        var (client, _, recipeId) = await CreateOwnedRecipeAsync("promote-nomain");
        var gallery = new[] { NewKey(recipeId, "gallery"), NewKey(recipeId, "gallery"), NewKey(recipeId, "gallery") };
        foreach (var url in gallery)
        {
            await ConfirmAsync(client, recipeId, "gallery", url);
        }

        (await PromoteAsync(client, recipeId, gallery[1])).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var after = await GetAsync(client, recipeId);
        after.ImageUrl.Should().Be(gallery[1]);
        after.GalleryImageUrls.Should().Equal(gallery[0], gallery[2]);
    }

    [Fact]
    public async Task Promote_CurrentMain_IsNoOp204()
    {
        var (client, _, recipeId) = await CreateOwnedRecipeAsync("promote-main");
        var main = NewKey(recipeId, "main");
        var galleryUrl = NewKey(recipeId, "gallery");
        await ConfirmAsync(client, recipeId, "main", main);
        await ConfirmAsync(client, recipeId, "gallery", galleryUrl);

        (await PromoteAsync(client, recipeId, main)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var after = await GetAsync(client, recipeId);
        after.ImageUrl.Should().Be(main);
        after.GalleryImageUrls.Should().Equal(galleryUrl);
    }

    [Fact]
    public async Task Promote_UnknownUrl_Returns400GalleryImageNotFound()
    {
        var (client, _, recipeId) = await CreateOwnedRecipeAsync("promote-unknown");

        var response = await PromoteAsync(client, recipeId, NewKey(recipeId, "gallery"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain("RECIPE_GALLERY_IMAGE_NOT_FOUND");
    }

    [Fact]
    public async Task Promote_NonOwner_Returns400RecipeNotOwned_AndKeepsImages()
    {
        var (client, ownerToken, recipeId) = await CreateOwnedRecipeAsync("promote-owner");
        var url = NewKey(recipeId, "gallery");
        await ConfirmAsync(client, recipeId, "gallery", url);

        TestHelpers.SetBearerToken(client, await SeedNutritionistAsync(client, "promote-other"));
        var response = await PromoteAsync(client, recipeId, url);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("RECIPE_NOT_OWNED");

        TestHelpers.SetBearerToken(client, ownerToken);
        var after = await GetAsync(client, recipeId);
        after.ImageUrl.Should().BeNull();
        after.GalleryImageUrls.Should().Equal(url);
    }

    [Fact]
    public async Task Promote_MissingRecipe_Returns404()
    {
        var (client, _, _) = await CreateOwnedRecipeAsync("promote-missing");

        var response = await PromoteAsync(client, Guid.NewGuid(), "recipes/x/gallery-a.jpg");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Promote_EmptyImageUrl_Returns400()
    {
        var (client, _, recipeId) = await CreateOwnedRecipeAsync("promote-empty");

        var response = await PromoteAsync(client, recipeId, string.Empty);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private record UploadUrl(string BlobUrl);
    private record RecipeRef(Guid RecipeId);
    private record RecipeDetail(Guid RecipeId, string? ImageUrl, List<string> GalleryImageUrls, int Version);
}
