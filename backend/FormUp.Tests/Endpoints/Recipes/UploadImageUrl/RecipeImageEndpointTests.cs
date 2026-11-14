using System.Security.Claims;
using System.Text.RegularExpressions;
using FastEndpoints;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Features.Recipes.ConfirmRecipeImage;
using FormUp.Application.Features.Recipes.UploadImageUrl;
using FormUp.Tests.Endpoints;
using FormUp.Tests.Endpoints.Recipes;
using MongoDB.Driver;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace FormUp.Tests.Endpoints.Recipes.UploadImageUrl;

/// <summary>
/// Unit tests for <see cref="UploadRecipeImageUrlEndpoint"/> and <see cref="ConfirmRecipeImageEndpoint"/>.
/// </summary>
public class UploadRecipeImageUrlEndpointTests
{
    private readonly Guid _nutritionistId = Guid.NewGuid();
    private readonly IImageUploadService _imageUpload = Substitute.For<IImageUploadService>();

    // ── Happy path: upload URL — main slot ─────────────────────────────────

    [Fact]
    public async Task UploadUrl_MainSlot_Nutritionist_RecipeExists_ReturnsUploadUrlAndBlobUrl()
    {
        var recipeId = Guid.NewGuid();
        var recipe = RecipeTestHelpers.CreateRecipe(externalId: recipeId, nutritionistId: _nutritionistId);
        var mongo = RecipeTestHelpers.CreateMockMongo(recipes: [recipe]);

        _imageUpload
            .GenerateUploadUrlAsync(
                ImageUploadScope.Recipe,
                Arg.Is<string>(s => Regex.IsMatch(s, $"^{recipeId}/main-[0-9a-f]{{32}}\\.jpg$")),
                "image/jpeg",
                1024,
                Arg.Any<CancellationToken>())
            .Returns(call => new BlobUploadUrl("https://storage/upload?token=abc", $"recipes/{call.ArgAt<string>(1)}"));

        var ep = Factory.Create<UploadRecipeImageUrlEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_nutritionistId, AppRoles.Nutritionist)))),
            mongo, _imageUpload);

        await ep.HandleAsync(new UploadRecipeImageUrlRequest
        {
            RecipeId = recipeId,
            Slot = "main",
            ContentType = "image/jpeg",
            SizeBytes = 1024
        }, CancellationToken.None);

        ep.Response.UploadUrl.Should().Be("https://storage/upload?token=abc");
        ep.Response.BlobUrl.Should().MatchRegex($"^recipes/{recipeId}/main-[0-9a-f]{{32}}\\.jpg$");
    }

    // ── Happy path: upload URL — gallery slot ──────────────────────────────

    [Fact]
    public async Task UploadUrl_GallerySlot_EmptyGallery_ReturnsUniqueGalleryBlobUrl()
    {
        var recipeId = Guid.NewGuid();
        var recipe = RecipeTestHelpers.CreateRecipe(externalId: recipeId, nutritionistId: _nutritionistId);
        // Empty gallery
        var mongo = RecipeTestHelpers.CreateMockMongo(recipes: [recipe]);

        _imageUpload
            .GenerateUploadUrlAsync(
                ImageUploadScope.Recipe,
                Arg.Is<string>(s => Regex.IsMatch(s, $"^{recipeId}/gallery-[0-9a-f]{{32}}\\.jpg$")),
                "image/jpeg",
                2048,
                Arg.Any<CancellationToken>())
            .Returns(call => new BlobUploadUrl("https://storage/upload?token=xyz", $"recipes/{call.ArgAt<string>(1)}"));

        var ep = Factory.Create<UploadRecipeImageUrlEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_nutritionistId, AppRoles.Nutritionist)))),
            mongo, _imageUpload);

        await ep.HandleAsync(new UploadRecipeImageUrlRequest
        {
            RecipeId = recipeId,
            Slot = "gallery",
            ContentType = "image/jpeg",
            SizeBytes = 2048
        }, CancellationToken.None);

        ep.Response.BlobUrl.Should().MatchRegex($"^recipes/{recipeId}/gallery-[0-9a-f]{{32}}\\.jpg$");
    }

    // ── Gallery slot: key never depends on the current gallery size ────────

    [Fact]
    public async Task UploadUrl_GallerySlot_ExistingEntries_StillIssuesFreshGuidKey()
    {
        var recipeId = Guid.NewGuid();
        var recipe = RecipeTestHelpers.CreateRecipe(externalId: recipeId, nutritionistId: _nutritionistId);
        recipe.GalleryImageUrls.Add($"recipes/{recipeId}/gallery-0.jpg");
        recipe.GalleryImageUrls.Add($"recipes/{recipeId}/gallery-1.png");
        var mongo = RecipeTestHelpers.CreateMockMongo(recipes: [recipe]);

        _imageUpload
            .GenerateUploadUrlAsync(
                ImageUploadScope.Recipe,
                Arg.Any<string>(),
                "image/webp",
                512,
                Arg.Any<CancellationToken>())
            .Returns(call => new BlobUploadUrl("https://storage/upload", $"recipes/{call.ArgAt<string>(1)}"));

        var ep = Factory.Create<UploadRecipeImageUrlEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_nutritionistId, AppRoles.Nutritionist)))),
            mongo, _imageUpload);

        await ep.HandleAsync(new UploadRecipeImageUrlRequest
        {
            RecipeId = recipeId,
            Slot = "gallery",
            ContentType = "image/webp",
            SizeBytes = 512
        }, CancellationToken.None);

        await _imageUpload.Received(1).GenerateUploadUrlAsync(
            ImageUploadScope.Recipe,
            Arg.Is<string>(s => Regex.IsMatch(s, $"^{recipeId}/gallery-[0-9a-f]{{32}}\\.webp$")),
            "image/webp",
            512,
            Arg.Any<CancellationToken>());
    }

    // ── Gallery cap enforcement on upload ──────────────────────────────────

    [Fact]
    public async Task UploadUrl_GallerySlot_GalleryFull_Throws_RecipeGalleryFull()
    {
        var recipeId = Guid.NewGuid();
        var recipe = RecipeTestHelpers.CreateRecipe(externalId: recipeId, nutritionistId: _nutritionistId);
        // Fill gallery to cap
        for (var i = 0; i < 6; i++)
            recipe.GalleryImageUrls.Add($"recipes/{recipeId}/gallery-{i}.jpg");

        var mongo = RecipeTestHelpers.CreateMockMongo(recipes: [recipe]);

        var ep = Factory.Create<UploadRecipeImageUrlEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_nutritionistId, AppRoles.Nutritionist)))),
            mongo, _imageUpload);

        var act = () => ep.HandleAsync(new UploadRecipeImageUrlRequest
        {
            RecipeId = recipeId,
            Slot = "gallery",
            ContentType = "image/jpeg",
            SizeBytes = 1024
        }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationFailureException>();
        ex.Which.Failures.Should()
            .ContainSingle(f => f.ErrorCode == ErrorCodes.RecipeGalleryFull);

        await _imageUpload.DidNotReceive().GenerateUploadUrlAsync(
            Arg.Any<ImageUploadScope>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    // ── Non-owner ──────────────────────────────────────────────────────────

    [Fact]
    public async Task UploadUrl_NonOwner_Throws_RecipeNotOwned()
    {
        var recipeId = Guid.NewGuid();
        var recipe = RecipeTestHelpers.CreateRecipe(externalId: recipeId, nutritionistId: Guid.NewGuid());
        var mongo = RecipeTestHelpers.CreateMockMongo(recipes: [recipe]);

        var ep = Factory.Create<UploadRecipeImageUrlEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_nutritionistId, AppRoles.Nutritionist)))),
            mongo, _imageUpload);

        var act = () => ep.HandleAsync(new UploadRecipeImageUrlRequest
        {
            RecipeId = recipeId,
            Slot = "main",
            ContentType = "image/jpeg",
            SizeBytes = 1024
        }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationFailureException>();
        ex.Which.Failures.Should()
            .ContainSingle(f => f.ErrorCode == ErrorCodes.RecipeNotOwned);
    }

    // ── Recipe not found ───────────────────────────────────────────────────

    [Fact]
    public async Task UploadUrl_RecipeNotFound_Returns404()
    {
        var mongo = RecipeTestHelpers.CreateMockMongo(); // no recipes

        var ep = Factory.Create<UploadRecipeImageUrlEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_nutritionistId, AppRoles.Nutritionist)))),
            mongo, _imageUpload);

        await ep.HandleAsync(new UploadRecipeImageUrlRequest
        {
            RecipeId = Guid.NewGuid(),
            Slot = "main",
            ContentType = "image/jpeg",
            SizeBytes = 1024
        }, CancellationToken.None);

        ep.HttpContext.Response.StatusCode.Should().Be(404);
        await _imageUpload.DidNotReceive().GenerateUploadUrlAsync(
            Arg.Any<ImageUploadScope>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    // ── Blob-path format ───────────────────────────────────────────────────

    [Theory]
    [InlineData("image/jpeg", "main",    "jpg")]
    [InlineData("image/png",  "main",    "png")]
    [InlineData("image/webp", "main",    "webp")]
    [InlineData("image/jpeg", "gallery", "jpg")]
    [InlineData("image/png",  "gallery", "png")]
    public async Task UploadUrl_SubPathConstruction_MatchesBlobPathConvention(
        string contentType, string slot, string extension)
    {
        var recipeId = Guid.NewGuid();
        var recipe = RecipeTestHelpers.CreateRecipe(externalId: recipeId, nutritionistId: _nutritionistId);
        var mongo = RecipeTestHelpers.CreateMockMongo(recipes: [recipe]);

        var expectedPattern = $"^{recipeId}/{slot}-[0-9a-f]{{32}}\\.{extension}$";
        _imageUpload
            .GenerateUploadUrlAsync(
                ImageUploadScope.Recipe,
                Arg.Is<string>(s => Regex.IsMatch(s, expectedPattern)),
                contentType,
                512,
                Arg.Any<CancellationToken>())
            .Returns(call => new BlobUploadUrl("https://storage/upload", $"recipes/{call.ArgAt<string>(1)}"));

        var ep = Factory.Create<UploadRecipeImageUrlEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_nutritionistId, AppRoles.Nutritionist)))),
            mongo, _imageUpload);

        await ep.HandleAsync(new UploadRecipeImageUrlRequest
        {
            RecipeId = recipeId,
            Slot = slot,
            ContentType = contentType,
            SizeBytes = 512
        }, CancellationToken.None);

        await _imageUpload.Received(1).GenerateUploadUrlAsync(
            ImageUploadScope.Recipe,
            Arg.Is<string>(s => Regex.IsMatch(s, expectedPattern)),
            contentType,
            512,
            Arg.Any<CancellationToken>());
    }

    // ── Service-level rejections ───────────────────────────────────────────

    [Fact]
    public async Task UploadUrl_InvalidContentType_ServiceThrows_PropagatesException()
    {
        var recipeId = Guid.NewGuid();
        var recipe = RecipeTestHelpers.CreateRecipe(externalId: recipeId, nutritionistId: _nutritionistId);
        var mongo = RecipeTestHelpers.CreateMockMongo(recipes: [recipe]);

        _imageUpload
            .GenerateUploadUrlAsync(
                Arg.Any<ImageUploadScope>(), Arg.Any<string>(), "application/pdf",
                Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Throws(new ValidationFailureException(
                [new FluentValidation.Results.ValidationFailure("contentType", "invalid")
                    { ErrorCode = ErrorCodes.InvalidImageContentType }],
                "Invalid content type."));

        var ep = Factory.Create<UploadRecipeImageUrlEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_nutritionistId, AppRoles.Nutritionist)))),
            mongo, _imageUpload);

        var act = () => ep.HandleAsync(new UploadRecipeImageUrlRequest
        {
            RecipeId = recipeId,
            Slot = "main",
            ContentType = "application/pdf",
            SizeBytes = 1024
        }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationFailureException>();
        ex.Which.Failures.Should()
            .ContainSingle(f => f.ErrorCode == ErrorCodes.InvalidImageContentType);
    }
}

/// <summary>
/// Unit tests for <see cref="ConfirmRecipeImageEndpoint"/>.
/// </summary>
public class ConfirmRecipeImageEndpointTests
{
    private readonly Guid _nutritionistId = Guid.NewGuid();
    private readonly IImageUploadService _imageUpload = Substitute.For<IImageUploadService>();

    // ── Happy path: main slot ──────────────────────────────────────────────

    [Fact]
    public async Task ConfirmImage_MainSlot_Owner_SetsImageUrl_Returns204()
    {
        var recipeId = Guid.NewGuid();
        var recipe = RecipeTestHelpers.CreateRecipe(externalId: recipeId, nutritionistId: _nutritionistId);
        var mongo = RecipeTestHelpers.CreateMockMongo(recipes: [recipe]);
        var keyId = Guid.NewGuid();
        var blobUrl = $"recipes/{recipeId}/main-{keyId:N}.jpg";
        _imageUpload.IsValidBlobUrlForSubPath(ImageUploadScope.Recipe, $"{recipeId}/main-{keyId:N}", blobUrl)
            .Returns(true);

        var ep = Factory.Create<ConfirmRecipeImageEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_nutritionistId, AppRoles.Nutritionist)))),
            mongo, _imageUpload);

        await ep.HandleAsync(new ConfirmRecipeImageRequest
        {
            RecipeId = recipeId,
            Slot = "main",
            BlobUrl = blobUrl
        }, CancellationToken.None);

        ep.HttpContext.Response.StatusCode.Should().Be(204);

        await mongo.Recipes.Received(1).UpdateOneAsync(
            Arg.Any<FilterDefinition<Application.Domain.Documents.Recipe>>(),
            Arg.Any<UpdateDefinition<Application.Domain.Documents.Recipe>>(),
            Arg.Any<UpdateOptions>(),
            Arg.Any<CancellationToken>());
    }

    // ── Happy path: gallery slot ───────────────────────────────────────────

    [Fact]
    public async Task ConfirmImage_GallerySlot_Owner_AppendsToGallery_Returns204()
    {
        var recipeId = Guid.NewGuid();
        var recipe = RecipeTestHelpers.CreateRecipe(externalId: recipeId, nutritionistId: _nutritionistId);
        var mongo = RecipeTestHelpers.CreateMockMongo(recipes: [recipe]);
        var keyId = Guid.NewGuid();
        var blobUrl = $"recipes/{recipeId}/gallery-{keyId:N}.jpg";
        _imageUpload.IsValidBlobUrlForSubPath(ImageUploadScope.Recipe, $"{recipeId}/gallery-{keyId:N}", blobUrl)
            .Returns(true);

        var ep = Factory.Create<ConfirmRecipeImageEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_nutritionistId, AppRoles.Nutritionist)))),
            mongo, _imageUpload);

        await ep.HandleAsync(new ConfirmRecipeImageRequest
        {
            RecipeId = recipeId,
            Slot = "gallery",
            BlobUrl = blobUrl
        }, CancellationToken.None);

        ep.HttpContext.Response.StatusCode.Should().Be(204);
        await mongo.Recipes.Received(1).UpdateOneAsync(
            Arg.Any<FilterDefinition<Application.Domain.Documents.Recipe>>(),
            Arg.Any<UpdateDefinition<Application.Domain.Documents.Recipe>>(),
            Arg.Any<UpdateOptions>(),
            Arg.Any<CancellationToken>());
    }

    // ── Gallery cap enforcement on confirm ────────────────────────────────

    [Fact]
    public async Task ConfirmImage_GallerySlot_GalleryFull_Throws_RecipeGalleryFull()
    {
        var recipeId = Guid.NewGuid();
        var recipe = RecipeTestHelpers.CreateRecipe(externalId: recipeId, nutritionistId: _nutritionistId);
        for (var i = 0; i < 6; i++)
            recipe.GalleryImageUrls.Add($"recipes/{recipeId}/gallery-{i}.jpg");

        var mongo = RecipeTestHelpers.CreateMockMongo(recipes: [recipe]);

        var ep = Factory.Create<ConfirmRecipeImageEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_nutritionistId, AppRoles.Nutritionist)))),
            mongo, _imageUpload);

        var act = () => ep.HandleAsync(new ConfirmRecipeImageRequest
        {
            RecipeId = recipeId,
            Slot = "gallery",
            BlobUrl = $"recipes/{recipeId}/gallery-{Guid.NewGuid():N}.jpg"
        }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationFailureException>();
        ex.Which.Failures.Should()
            .ContainSingle(f => f.ErrorCode == ErrorCodes.RecipeGalleryFull);

        await mongo.Recipes.DidNotReceive().UpdateOneAsync(
            Arg.Any<FilterDefinition<Application.Domain.Documents.Recipe>>(),
            Arg.Any<UpdateDefinition<Application.Domain.Documents.Recipe>>(),
            Arg.Any<UpdateOptions>(),
            Arg.Any<CancellationToken>());
    }

    // ── Ownership gate ─────────────────────────────────────────────────────

    [Fact]
    public async Task ConfirmImage_NonOwner_Throws_RecipeNotOwned()
    {
        var recipeId = Guid.NewGuid();
        var recipe = RecipeTestHelpers.CreateRecipe(externalId: recipeId, nutritionistId: Guid.NewGuid());
        var mongo = RecipeTestHelpers.CreateMockMongo(recipes: [recipe]);

        var ep = Factory.Create<ConfirmRecipeImageEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(Guid.NewGuid(), AppRoles.Nutritionist)))),
            mongo, _imageUpload);

        var act = () => ep.HandleAsync(new ConfirmRecipeImageRequest
        {
            RecipeId = recipeId,
            Slot = "main",
            BlobUrl = $"recipes/{recipeId}/main.jpg"
        }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationFailureException>();
        ex.Which.Failures.Should()
            .ContainSingle(f => f.ErrorCode == ErrorCodes.RecipeNotOwned);

        await mongo.Recipes.DidNotReceive().UpdateOneAsync(
            Arg.Any<FilterDefinition<Application.Domain.Documents.Recipe>>(),
            Arg.Any<UpdateDefinition<Application.Domain.Documents.Recipe>>(),
            Arg.Any<UpdateOptions>(),
            Arg.Any<CancellationToken>());
    }

    // ── Recipe not found ───────────────────────────────────────────────────

    [Fact]
    public async Task ConfirmImage_RecipeNotFound_Returns404()
    {
        var mongo = RecipeTestHelpers.CreateMockMongo(); // no recipes

        var ep = Factory.Create<ConfirmRecipeImageEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_nutritionistId, AppRoles.Nutritionist)))),
            mongo, _imageUpload);

        await ep.HandleAsync(new ConfirmRecipeImageRequest
        {
            RecipeId = Guid.NewGuid(),
            Slot = "main",
            BlobUrl = "recipes/nonexistent/main.jpg"
        }, CancellationToken.None);

        ep.HttpContext.Response.StatusCode.Should().Be(404);
        await mongo.Recipes.DidNotReceive().UpdateOneAsync(
            Arg.Any<FilterDefinition<Application.Domain.Documents.Recipe>>(),
            Arg.Any<UpdateDefinition<Application.Domain.Documents.Recipe>>(),
            Arg.Any<UpdateOptions>(),
            Arg.Any<CancellationToken>());
    }

    // ── Blob URL must match the presigned key ─────────────────────────────

    [Theory]
    [InlineData("main", "https://evil.example/x.jpg")]
    [InlineData("gallery", "https://evil.example/x.jpg")]
    [InlineData("main", "recipes/{0}/main.jpg")]                 // legacy fixed name
    [InlineData("gallery", "recipes/{0}/gallery-3.jpg")]         // legacy index name
    [InlineData("gallery", "recipes/{0}/main-0123456789abcdef0123456789abcdef.jpg")] // other slot's prefix
    [InlineData("main", "recipes/{0}/main-0123456789ABCDEF0123456789ABCDEF.jpg")]    // upper-case hex
    [InlineData("main", "recipes/{0}/main-0123456789abcdef.jpg")]                    // not a 32-hex guid
    public async Task ConfirmImage_BlobUrlNotMatchingKey_Throws_InvalidBlobUrl_AndDoesNotWrite(string slot, string urlTemplate)
    {
        var recipeId = Guid.NewGuid();
        var recipe = RecipeTestHelpers.CreateRecipe(externalId: recipeId, nutritionistId: _nutritionistId);
        var mongo = RecipeTestHelpers.CreateMockMongo(recipes: [recipe]);
        _imageUpload.IsValidBlobUrlForSubPath(Arg.Any<ImageUploadScope>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);

        var ep = Factory.Create<ConfirmRecipeImageEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_nutritionistId, AppRoles.Nutritionist)))),
            mongo, _imageUpload);

        var act = () => ep.HandleAsync(new ConfirmRecipeImageRequest
        {
            RecipeId = recipeId,
            Slot = slot,
            BlobUrl = string.Format(urlTemplate, recipeId)
        }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationFailureException>();
        ex.Which.Failures.Should().ContainSingle(f => f.ErrorCode == ErrorCodes.InvalidBlobUrl);

        await mongo.Recipes.DidNotReceive().UpdateOneAsync(
            Arg.Any<FilterDefinition<Application.Domain.Documents.Recipe>>(),
            Arg.Any<UpdateDefinition<Application.Domain.Documents.Recipe>>(),
            Arg.Any<UpdateOptions>(),
            Arg.Any<CancellationToken>());
    }

    // ── Already-stored URLs are rejected ───────────────────────────────────

    [Theory]
    [InlineData("gallery", true)]
    [InlineData("main", false)]
    public async Task ConfirmImage_UrlAlreadyStored_Throws_InvalidBlobUrl_AndDoesNotWrite(string slot, bool storedInGallery)
    {
        var recipeId = Guid.NewGuid();
        var blobUrl = $"recipes/{recipeId}/{slot}-{Guid.NewGuid():N}.jpg";
        var recipe = RecipeTestHelpers.CreateRecipe(externalId: recipeId, nutritionistId: _nutritionistId);

        if (storedInGallery)
        {
            recipe.GalleryImageUrls.Add(blobUrl);
        }
        else
        {
            recipe.ImageUrl = blobUrl;
        }

        var mongo = RecipeTestHelpers.CreateMockMongo(recipes: [recipe]);
        _imageUpload.IsValidBlobUrlForSubPath(Arg.Any<ImageUploadScope>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);

        var ep = Factory.Create<ConfirmRecipeImageEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_nutritionistId, AppRoles.Nutritionist)))),
            mongo, _imageUpload);

        var act = () => ep.HandleAsync(new ConfirmRecipeImageRequest
        {
            RecipeId = recipeId,
            Slot = slot,
            BlobUrl = blobUrl
        }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationFailureException>();
        ex.Which.Failures.Should().ContainSingle(f => f.ErrorCode == ErrorCodes.InvalidBlobUrl);
        await mongo.Recipes.DidNotReceive().UpdateOneAsync(
            Arg.Any<FilterDefinition<Application.Domain.Documents.Recipe>>(),
            Arg.Any<UpdateDefinition<Application.Domain.Documents.Recipe>>(),
            Arg.Any<UpdateOptions>(),
            Arg.Any<CancellationToken>());
    }

    // ── Atomic gallery append lost the race ────────────────────────────────

    [Fact]
    public async Task ConfirmImage_GallerySlot_AtomicAppendModifiesNothing_Throws_RecipeGalleryFull()
    {
        var recipeId = Guid.NewGuid();
        var blobUrl = $"recipes/{recipeId}/gallery-{Guid.NewGuid():N}.jpg";
        var recipe = RecipeTestHelpers.CreateRecipe(externalId: recipeId, nutritionistId: _nutritionistId);
        var mongo = RecipeTestHelpers.CreateMockMongo(recipes: [recipe], modifiedCount: 0);
        _imageUpload.IsValidBlobUrlForSubPath(Arg.Any<ImageUploadScope>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);

        var ep = Factory.Create<ConfirmRecipeImageEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_nutritionistId, AppRoles.Nutritionist)))),
            mongo, _imageUpload);

        var act = () => ep.HandleAsync(new ConfirmRecipeImageRequest
        {
            RecipeId = recipeId,
            Slot = "gallery",
            BlobUrl = blobUrl
        }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationFailureException>();
        ex.Which.Failures.Should().ContainSingle(f => f.ErrorCode == ErrorCodes.RecipeGalleryFull);
    }

    // ── Get reflects stored image (unit-level) ─────────────────────────────

    [Fact]
    public void ConfirmImage_AfterConfirm_GetRecipe_ReflectsImageUrlAndGallery()
    {
        var recipeId = Guid.NewGuid();
        var blobUrl = $"recipes/{recipeId}/main.jpg";
        var galleryUrl = $"recipes/{recipeId}/gallery-0.png";

        // Simulate what a recipe document looks like after both confirms
        var recipe = RecipeTestHelpers.CreateRecipe(externalId: recipeId, nutritionistId: _nutritionistId);
        recipe.ImageUrl = blobUrl;
        recipe.GalleryImageUrls.Add(galleryUrl);

        var response = Application.Features.Recipes.Shared.GetRecipeResponse.FromDocument(recipe, _nutritionistId);

        response.ImageUrl.Should().Be(blobUrl);
        response.GalleryImageUrls.Should().ContainSingle(u => u == galleryUrl);
        response.RecipeId.Should().Be(recipeId);
    }
}
