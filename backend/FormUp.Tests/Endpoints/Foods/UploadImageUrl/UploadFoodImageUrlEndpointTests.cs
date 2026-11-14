using System.Security.Claims;
using FastEndpoints;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Features.Foods.ConfirmFoodImage;
using FormUp.Application.Features.Foods.Shared;
using FormUp.Application.Features.Foods.UploadImageUrl;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Endpoints;
using FormUp.Tests.Endpoints.Foods;
using MongoDB.Driver;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace FormUp.Tests.Endpoints.Foods.UploadImageUrl;

/// <summary>
/// Tests for <see cref="UploadFoodImageUrlEndpoint"/> and <see cref="ConfirmFoodImageEndpoint"/>.
/// </summary>
public class UploadFoodImageUrlEndpointTests
{
    private readonly Guid _nutritionistId = Guid.NewGuid();
    private readonly IImageUploadService _imageUpload = Substitute.For<IImageUploadService>();

    private static bool IsIssuedKey(string subPath, Guid foodId, string slot, string extension) =>
        subPath.StartsWith($"{foodId}/{slot}-")
        && subPath.EndsWith($".{extension}")
        && FoodImageKeys.TryParse(subPath, slot, out _);

    private Endpoint<UploadFoodImageUrlRequest, UploadFoodImageUrlResponse> CreateEndpoint(IMongoContext mongo) =>
        Factory.Create<UploadFoodImageUrlEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_nutritionistId, AppRoles.Nutritionist)))),
            mongo, _imageUpload);

    // ── Happy path: upload URL — main slot ─────────────────────────────────

    [Fact]
    public async Task UploadUrl_MainSlot_Nutritionist_FoodExists_ReturnsUploadUrlAndUniqueBlobUrl()
    {
        var foodId = Guid.NewGuid();
        var food = FoodTestHelpers.CreateFood(externalId: foodId, nutritionistId: _nutritionistId);
        var mongo = FoodTestHelpers.CreateMockMongo(food);

        var expectedBlobUrl = $"foods/{foodId}/main-{Guid.NewGuid():N}.jpg";
        _imageUpload
            .GenerateUploadUrlAsync(
                ImageUploadScope.Food,
                Arg.Is<string>(subPath => IsIssuedKey(subPath, foodId, "main", "jpg")),
                "image/jpeg",
                1024,
                Arg.Any<CancellationToken>())
            .Returns(new BlobUploadUrl("https://storage/upload?token=abc", expectedBlobUrl));

        var ep = CreateEndpoint(mongo);

        await ep.HandleAsync(new UploadFoodImageUrlRequest
        {
            FoodId = foodId,
            Slot = "main",
            ContentType = "image/jpeg",
            SizeBytes = 1024
        }, CancellationToken.None);

        ep.Response.UploadUrl.Should().Be("https://storage/upload?token=abc");
        ep.Response.BlobUrl.Should().Be(expectedBlobUrl);
    }

    // ── Happy path: upload URL — gallery slot ──────────────────────────────

    [Fact]
    public async Task UploadUrl_GallerySlot_EmptyGallery_IssuesUniqueGalleryKey()
    {
        var foodId = Guid.NewGuid();
        var food = FoodTestHelpers.CreateFood(externalId: foodId, nutritionistId: _nutritionistId);
        var mongo = FoodTestHelpers.CreateMockMongo(food);

        var expectedBlobUrl = $"foods/{foodId}/gallery-{Guid.NewGuid():N}.jpg";
        _imageUpload
            .GenerateUploadUrlAsync(
                ImageUploadScope.Food,
                Arg.Is<string>(subPath => IsIssuedKey(subPath, foodId, "gallery", "jpg")),
                "image/jpeg",
                2048,
                Arg.Any<CancellationToken>())
            .Returns(new BlobUploadUrl("https://storage/upload?token=xyz", expectedBlobUrl));

        var ep = CreateEndpoint(mongo);

        await ep.HandleAsync(new UploadFoodImageUrlRequest
        {
            FoodId = foodId,
            Slot = "gallery",
            ContentType = "image/jpeg",
            SizeBytes = 2048
        }, CancellationToken.None);

        ep.Response.BlobUrl.Should().Be(expectedBlobUrl);
    }

    // ── Gallery slot: key never derives from the existing entries ──────────

    [Fact]
    public async Task UploadUrl_GallerySlot_ExistingEntries_IssuesKeyThatIsNotPositional()
    {
        var foodId = Guid.NewGuid();
        var food = FoodTestHelpers.CreateFood(externalId: foodId, nutritionistId: _nutritionistId);
        food.GalleryImageUrls.Add($"foods/{foodId}/gallery-0.jpg");
        food.GalleryImageUrls.Add($"foods/{foodId}/gallery-1.png");
        var mongo = FoodTestHelpers.CreateMockMongo(food);

        _imageUpload
            .GenerateUploadUrlAsync(
                ImageUploadScope.Food, Arg.Any<string>(), "image/webp", 512, Arg.Any<CancellationToken>())
            .Returns(new BlobUploadUrl("https://storage/upload", $"foods/{foodId}/gallery-x.webp"));

        var ep = CreateEndpoint(mongo);

        await ep.HandleAsync(new UploadFoodImageUrlRequest
        {
            FoodId = foodId,
            Slot = "gallery",
            ContentType = "image/webp",
            SizeBytes = 512
        }, CancellationToken.None);

        await _imageUpload.Received(1).GenerateUploadUrlAsync(
            ImageUploadScope.Food,
            Arg.Is<string>(subPath => IsIssuedKey(subPath, foodId, "gallery", "webp")),
            "image/webp",
            512,
            Arg.Any<CancellationToken>());
    }

    // ── Gallery cap enforcement on upload ──────────────────────────────────

    [Fact]
    public async Task UploadUrl_GallerySlot_GalleryFull_Throws_FoodGalleryFull()
    {
        var foodId = Guid.NewGuid();
        var food = FoodTestHelpers.CreateFood(externalId: foodId, nutritionistId: _nutritionistId);
        for (var i = 0; i < 6; i++)
        {
            food.GalleryImageUrls.Add($"foods/{foodId}/gallery-{Guid.NewGuid():N}.jpg");
        }

        var ep = CreateEndpoint(FoodTestHelpers.CreateMockMongo(food));

        var act = () => ep.HandleAsync(new UploadFoodImageUrlRequest
        {
            FoodId = foodId,
            Slot = "gallery",
            ContentType = "image/jpeg",
            SizeBytes = 1024
        }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationFailureException>();
        ex.Which.Failures.Should()
            .ContainSingle(f => f.ErrorCode == ErrorCodes.FoodGalleryFull);

        await _imageUpload.DidNotReceive().GenerateUploadUrlAsync(
            Arg.Any<ImageUploadScope>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    // ── Non-owner ──────────────────────────────────────────────────────────

    [Fact]
    public async Task UploadUrl_NonOwner_Throws_FoodNotOwned()
    {
        var foodId = Guid.NewGuid();
        var food = FoodTestHelpers.CreateFood(externalId: foodId, nutritionistId: Guid.NewGuid()); // different owner
        var ep = CreateEndpoint(FoodTestHelpers.CreateMockMongo(food));

        var act = () => ep.HandleAsync(new UploadFoodImageUrlRequest
        {
            FoodId = foodId,
            Slot = "main",
            ContentType = "image/jpeg",
            SizeBytes = 1024
        }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationFailureException>();
        ex.Which.Failures.Should()
            .ContainSingle(f => f.ErrorCode == ErrorCodes.FoodNotOwned);

        await _imageUpload.DidNotReceive().GenerateUploadUrlAsync(
            Arg.Any<ImageUploadScope>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    // ── Blob-path format ────────────────────────────────────────────────────

    [Theory]
    [InlineData("image/jpeg", "main",    "jpg")]
    [InlineData("image/png",  "main",    "png")]
    [InlineData("image/webp", "main",    "webp")]
    [InlineData("image/jpeg", "gallery", "jpg")]
    [InlineData("image/png",  "gallery", "png")]
    public async Task UploadUrl_SubPathConstruction_MatchesUniqueKeyConvention(
        string contentType, string slot, string extension)
    {
        var foodId = Guid.NewGuid();
        var food = FoodTestHelpers.CreateFood(externalId: foodId, nutritionistId: _nutritionistId);

        _imageUpload
            .GenerateUploadUrlAsync(
                ImageUploadScope.Food, Arg.Any<string>(), contentType, 512, Arg.Any<CancellationToken>())
            .Returns(new BlobUploadUrl("https://storage/upload", $"foods/{foodId}/{slot}-x.{extension}"));

        var ep = CreateEndpoint(FoodTestHelpers.CreateMockMongo(food));

        await ep.HandleAsync(new UploadFoodImageUrlRequest
        {
            FoodId = foodId,
            Slot = slot,
            ContentType = contentType,
            SizeBytes = 512
        }, CancellationToken.None);

        await _imageUpload.Received(1).GenerateUploadUrlAsync(
            ImageUploadScope.Food,
            Arg.Is<string>(subPath => IsIssuedKey(subPath, foodId, slot, extension)),
            contentType,
            512,
            Arg.Any<CancellationToken>());
    }

    // ── Food not found ──────────────────────────────────────────────────────

    [Fact]
    public async Task UploadUrl_FoodNotFound_Returns404()
    {
        var ep = CreateEndpoint(FoodTestHelpers.CreateMockMongo()); // no foods

        await ep.HandleAsync(new UploadFoodImageUrlRequest
        {
            FoodId = Guid.NewGuid(),
            Slot = "main",
            ContentType = "image/jpeg",
            SizeBytes = 1024
        }, CancellationToken.None);

        ep.HttpContext.Response.StatusCode.Should().Be(404);
        await _imageUpload.DidNotReceive().GenerateUploadUrlAsync(
            Arg.Any<ImageUploadScope>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<long>(),
            Arg.Any<CancellationToken>());
    }

    // ── Service-level rejections ────────────────────────────────────────────

    [Fact]
    public async Task UploadUrl_InvalidContentType_ServiceThrows_PropagatesException()
    {
        var foodId = Guid.NewGuid();
        var food = FoodTestHelpers.CreateFood(externalId: foodId, nutritionistId: _nutritionistId);

        _imageUpload
            .GenerateUploadUrlAsync(
                Arg.Any<ImageUploadScope>(),
                Arg.Any<string>(),
                "application/pdf",
                Arg.Any<long>(),
                Arg.Any<CancellationToken>())
            .Throws(new ValidationFailureException(
                [new FluentValidation.Results.ValidationFailure("contentType", "invalid")
                    { ErrorCode = ErrorCodes.InvalidImageContentType }],
                "Invalid content type."));

        var ep = CreateEndpoint(FoodTestHelpers.CreateMockMongo(food));

        var act = () => ep.HandleAsync(new UploadFoodImageUrlRequest
        {
            FoodId = foodId,
            Slot = "main",
            ContentType = "application/pdf",
            SizeBytes = 1024
        }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationFailureException>();
        ex.Which.Failures.Should()
            .ContainSingle(f => f.ErrorCode == ErrorCodes.InvalidImageContentType);
    }

    [Fact]
    public async Task UploadUrl_Oversize_ServiceThrows_PropagatesException()
    {
        const long sixMb = 6L * 1024 * 1024;

        var foodId = Guid.NewGuid();
        var food = FoodTestHelpers.CreateFood(externalId: foodId, nutritionistId: _nutritionistId);

        _imageUpload
            .GenerateUploadUrlAsync(
                Arg.Any<ImageUploadScope>(),
                Arg.Any<string>(),
                "image/jpeg",
                sixMb,
                Arg.Any<CancellationToken>())
            .Throws(new ValidationFailureException(
                [new FluentValidation.Results.ValidationFailure("sizeBytes", "too large")
                    { ErrorCode = ErrorCodes.ImageTooLarge }],
                "Image too large."));

        var ep = CreateEndpoint(FoodTestHelpers.CreateMockMongo(food));

        var act = () => ep.HandleAsync(new UploadFoodImageUrlRequest
        {
            FoodId = foodId,
            Slot = "main",
            ContentType = "image/jpeg",
            SizeBytes = sixMb
        }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationFailureException>();
        ex.Which.Failures.Should()
            .ContainSingle(f => f.ErrorCode == ErrorCodes.ImageTooLarge);
    }
}

/// <summary>
/// Tests for <see cref="ConfirmFoodImageEndpoint"/>.
/// </summary>
public class ConfirmFoodImageEndpointTests
{
    private readonly Guid _nutritionistId = Guid.NewGuid();
    private readonly IImageUploadService _imageUpload = CreateAcceptingImageUpload();

    private static string NewKey(Guid foodId, string slot) => $"foods/{foodId}/{slot}-{Guid.NewGuid():N}.jpg";

    private Endpoint<ConfirmFoodImageRequest> CreateEndpoint(IMongoContext mongo) =>
        Factory.Create<ConfirmFoodImageEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_nutritionistId, AppRoles.Nutritionist)))),
            mongo,
            _imageUpload);

    /// <summary>An unstubbed UpdateOneAsync reports ModifiedCount 0, which the gallery push reads as "full".</summary>
    private static void StubUpdateResult(IMongoContext mongo, long modifiedCount)
    {
        var result = Substitute.For<UpdateResult>();
        result.ModifiedCount.Returns(modifiedCount);
        mongo.Foods.UpdateOneAsync(
                Arg.Any<FilterDefinition<Food>>(),
                Arg.Any<UpdateDefinition<Food>>(),
                Arg.Any<UpdateOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(result);
    }

    private static Task AssertNoWriteAsync(IMongoContext mongo) =>
        mongo.Foods.DidNotReceive().UpdateOneAsync(
            Arg.Any<FilterDefinition<Food>>(),
            Arg.Any<UpdateDefinition<Food>>(),
            Arg.Any<UpdateOptions>(),
            Arg.Any<CancellationToken>());

    // ── Happy path: main slot ──────────────────────────────────────────────

    [Fact]
    public async Task ConfirmImage_MainSlot_Owner_SetsImageUrlOnFood_Returns204()
    {
        var foodId = Guid.NewGuid();
        var food = FoodTestHelpers.CreateFood(externalId: foodId, nutritionistId: _nutritionistId);
        var mongo = FoodTestHelpers.CreateMockMongo(food);
        var ep = CreateEndpoint(mongo);

        await ep.HandleAsync(new ConfirmFoodImageRequest
        {
            FoodId = foodId,
            Slot = "main",
            BlobUrl = NewKey(foodId, "main")
        }, CancellationToken.None);

        ep.HttpContext.Response.StatusCode.Should().Be(204);

        await mongo.Foods.Received(1).UpdateOneAsync(
            Arg.Any<FilterDefinition<Food>>(),
            Arg.Any<UpdateDefinition<Food>>(),
            Arg.Any<UpdateOptions>(),
            Arg.Any<CancellationToken>());
    }

    // ── Happy path: gallery slot ───────────────────────────────────────────

    [Fact]
    public async Task ConfirmImage_GallerySlot_Owner_AppendsToGallery_Returns204()
    {
        var foodId = Guid.NewGuid();
        var food = FoodTestHelpers.CreateFood(externalId: foodId, nutritionistId: _nutritionistId);
        var mongo = FoodTestHelpers.CreateMockMongo(food);
        StubUpdateResult(mongo, modifiedCount: 1);
        var ep = CreateEndpoint(mongo);

        await ep.HandleAsync(new ConfirmFoodImageRequest
        {
            FoodId = foodId,
            Slot = "gallery",
            BlobUrl = NewKey(foodId, "gallery")
        }, CancellationToken.None);

        ep.HttpContext.Response.StatusCode.Should().Be(204);
        await mongo.Foods.Received(1).UpdateOneAsync(
            Arg.Any<FilterDefinition<Food>>(),
            Arg.Any<UpdateDefinition<Food>>(),
            Arg.Any<UpdateOptions>(),
            Arg.Any<CancellationToken>());
    }

    // ── Gallery cap enforcement on confirm ────────────────────────────────

    [Fact]
    public async Task ConfirmImage_GallerySlot_GalleryFull_Throws_FoodGalleryFull()
    {
        var foodId = Guid.NewGuid();
        var food = FoodTestHelpers.CreateFood(externalId: foodId, nutritionistId: _nutritionistId);
        for (var i = 0; i < 6; i++)
        {
            food.GalleryImageUrls.Add(NewKey(foodId, "gallery"));
        }

        var mongo = FoodTestHelpers.CreateMockMongo(food);
        var ep = CreateEndpoint(mongo);

        var act = () => ep.HandleAsync(new ConfirmFoodImageRequest
        {
            FoodId = foodId,
            Slot = "gallery",
            BlobUrl = NewKey(foodId, "gallery")
        }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationFailureException>();
        ex.Which.Failures.Should()
            .ContainSingle(f => f.ErrorCode == ErrorCodes.FoodGalleryFull);

        await AssertNoWriteAsync(mongo);
    }

    [Fact]
    public async Task ConfirmImage_GallerySlot_AtomicPushMatchesNothing_Throws_FoodGalleryFull()
    {
        var foodId = Guid.NewGuid();
        var food = FoodTestHelpers.CreateFood(externalId: foodId, nutritionistId: _nutritionistId);
        var mongo = FoodTestHelpers.CreateMockMongo(food);
        StubUpdateResult(mongo, modifiedCount: 0); // a concurrent confirm filled the 6th slot after the read
        var ep = CreateEndpoint(mongo);

        var act = () => ep.HandleAsync(new ConfirmFoodImageRequest
        {
            FoodId = foodId,
            Slot = "gallery",
            BlobUrl = NewKey(foodId, "gallery")
        }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationFailureException>();
        ex.Which.Failures.Should()
            .ContainSingle(f => f.ErrorCode == ErrorCodes.FoodGalleryFull);
    }

    // ── Key validation ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("main")]
    [InlineData("gallery")]
    public async Task ConfirmImage_LegacyPositionalUrl_Throws_InvalidBlobUrl(string slot)
    {
        var foodId = Guid.NewGuid();
        var food = FoodTestHelpers.CreateFood(externalId: foodId, nutritionistId: _nutritionistId);
        var mongo = FoodTestHelpers.CreateMockMongo(food);
        var ep = CreateEndpoint(mongo);

        var act = () => ep.HandleAsync(new ConfirmFoodImageRequest
        {
            FoodId = foodId,
            Slot = slot,
            BlobUrl = slot == "main" ? $"foods/{foodId}.jpg" : $"foods/{foodId}/gallery-0.jpg"
        }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationFailureException>();
        ex.Which.Failures.Should()
            .ContainSingle(f => f.ErrorCode == ErrorCodes.InvalidBlobUrl);

        await AssertNoWriteAsync(mongo);
    }

    [Fact]
    public async Task ConfirmImage_SlotMismatch_Throws_InvalidBlobUrl()
    {
        var foodId = Guid.NewGuid();
        var food = FoodTestHelpers.CreateFood(externalId: foodId, nutritionistId: _nutritionistId);
        var mongo = FoodTestHelpers.CreateMockMongo(food);
        var ep = CreateEndpoint(mongo);

        var act = () => ep.HandleAsync(new ConfirmFoodImageRequest
        {
            FoodId = foodId,
            Slot = "main",
            BlobUrl = NewKey(foodId, "gallery")
        }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationFailureException>();
        ex.Which.Failures.Should()
            .ContainSingle(f => f.ErrorCode == ErrorCodes.InvalidBlobUrl);
    }

    [Fact]
    public async Task ConfirmImage_UrlRejectedByUploadService_Throws_InvalidBlobUrl()
    {
        var foodId = Guid.NewGuid();
        var food = FoodTestHelpers.CreateFood(externalId: foodId, nutritionistId: _nutritionistId);
        var mongo = FoodTestHelpers.CreateMockMongo(food);
        var rejecting = Substitute.For<IImageUploadService>();
        rejecting.IsValidBlobUrlForSubPath(Arg.Any<ImageUploadScope>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(false);
        var ep = Factory.Create<ConfirmFoodImageEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_nutritionistId, AppRoles.Nutritionist)))),
            mongo,
            rejecting);

        var act = () => ep.HandleAsync(new ConfirmFoodImageRequest
        {
            FoodId = foodId,
            Slot = "gallery",
            BlobUrl = NewKey(foodId, "gallery")
        }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationFailureException>();
        ex.Which.Failures.Should()
            .ContainSingle(f => f.ErrorCode == ErrorCodes.InvalidBlobUrl);
    }

    [Fact]
    public async Task ConfirmImage_GalleryUrlAlreadyInGallery_Throws_InvalidBlobUrl()
    {
        var foodId = Guid.NewGuid();
        var url = NewKey(foodId, "gallery");
        var food = FoodTestHelpers.CreateFood(externalId: foodId, nutritionistId: _nutritionistId);
        food.GalleryImageUrls.Add(url);
        var mongo = FoodTestHelpers.CreateMockMongo(food);
        var ep = CreateEndpoint(mongo);

        var act = () => ep.HandleAsync(new ConfirmFoodImageRequest
        {
            FoodId = foodId,
            Slot = "gallery",
            BlobUrl = url
        }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationFailureException>();
        ex.Which.Failures.Should()
            .ContainSingle(f => f.ErrorCode == ErrorCodes.InvalidBlobUrl);

        await AssertNoWriteAsync(mongo);
    }

    // ── Ownership gate ──────────────────────────────────────────────────────

    [Fact]
    public async Task ConfirmImage_NonOwner_ThrowsFoodNotOwnedError()
    {
        var foodId = Guid.NewGuid();
        var food = FoodTestHelpers.CreateFood(externalId: foodId, nutritionistId: Guid.NewGuid()); // different owner
        var mongo = FoodTestHelpers.CreateMockMongo(food);
        var ep = CreateEndpoint(mongo);

        var act = () => ep.HandleAsync(new ConfirmFoodImageRequest
        {
            FoodId = foodId,
            Slot = "main",
            BlobUrl = NewKey(foodId, "main")
        }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<ValidationFailureException>();
        ex.Which.Failures.Should()
            .ContainSingle(f => f.ErrorCode == ErrorCodes.FoodNotOwned);

        await AssertNoWriteAsync(mongo);
    }

    // ── Food not found ──────────────────────────────────────────────────────

    [Fact]
    public async Task ConfirmImage_FoodNotFound_Returns404()
    {
        var mongo = FoodTestHelpers.CreateMockMongo(); // no foods
        var ep = CreateEndpoint(mongo);

        await ep.HandleAsync(new ConfirmFoodImageRequest
        {
            FoodId = Guid.NewGuid(),
            Slot = "main",
            BlobUrl = "foods/nonexistent.jpg"
        }, CancellationToken.None);

        ep.HttpContext.Response.StatusCode.Should().Be(404);
        await AssertNoWriteAsync(mongo);
    }

    // ── Soft-deleted food ───────────────────────────────────────────────────

    /// <summary>
    /// When FindAsync returns no document (because the food is soft-deleted —
    /// the find filter includes IsDeleted == false), the endpoint must return 404
    /// and must not issue an UpdateOneAsync call.
    /// </summary>
    [Fact]
    public async Task ConfirmImage_SoftDeletedFood_FindReturnsNull_Returns404AndUpdateNotCalled()
    {
        var mongo = FoodTestHelpers.CreateMockMongo(); // no foods → FindAsync returns empty cursor
        var ep = CreateEndpoint(mongo);

        await ep.HandleAsync(new ConfirmFoodImageRequest
        {
            FoodId = Guid.NewGuid(),
            Slot = "main",
            BlobUrl = "foods/deleted-food.jpg"
        }, CancellationToken.None);

        ep.HttpContext.Response.StatusCode.Should().Be(404,
            "a soft-deleted (or never-existing) food must result in 404, not a write");

        await AssertNoWriteAsync(mongo);
    }

    // ── Get reflects stored image ───────────────────────────────────────────

    [Fact]
    public void ConfirmImage_AfterConfirm_GetFoodSummary_ReflectsImageUrlAndGallery()
    {
        var foodId = Guid.NewGuid();
        var blobUrl = NewKey(foodId, "main");
        var galleryUrl = NewKey(foodId, "gallery");

        // Simulate what happens after both confirms: the food document has both fields set.
        var food = FoodTestHelpers.CreateFood(externalId: foodId, nutritionistId: _nutritionistId);
        food.ImageUrl = blobUrl;
        food.GalleryImageUrls.Add(galleryUrl);

        var summary = FoodSummary.FromDocument(food, currentUserId: _nutritionistId);

        summary.ImageUrl.Should().Be(blobUrl);
        summary.GalleryImageUrls.Should().ContainSingle(u => u == galleryUrl);
        summary.FoodId.Should().Be(foodId);
    }

    // The upload service's own blob-URL check is stubbed to accept here; key shape is what these tests exercise.
    private static IImageUploadService CreateAcceptingImageUpload()
    {
        var imageUpload = Substitute.For<IImageUploadService>();
        imageUpload.IsValidBlobUrlForSubPath(Arg.Any<ImageUploadScope>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(true);
        return imageUpload;
    }
}
