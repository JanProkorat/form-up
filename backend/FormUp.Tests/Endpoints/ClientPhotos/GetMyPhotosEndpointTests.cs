using System.Security.Claims;
using FastEndpoints;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.ClientPhotos.GetMyPhotos;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;

namespace FormUp.Tests.Endpoints.ClientPhotos;

/// <summary>
/// Unit tests for <see cref="GetMyPhotosEndpoint"/>.
/// Covers authorization, pagination, category filter, date filter, and month grouping.
/// </summary>
public class GetMyPhotosEndpointTests
{
    private readonly Guid _clientUserId = Guid.NewGuid();

    private static PlanPhoto MakePhoto(
        long clientProfileId,
        PlanPhotoCategory category = PlanPhotoCategory.Body,
        DateTime? takenAt = null)
    {
        return new PlanPhoto
        {
            Id = Random.Shared.NextInt64(1, 100_000),
            PublicId = Guid.NewGuid(),
            ClientProfileId = clientProfileId,
            Category = category,
            BlobUrl = "https://blob/photo.jpg",
            TakenAt = takenAt ?? DateTime.UtcNow,
            UploadedByUserId = Guid.NewGuid(),
            DateCreated = DateTime.UtcNow,
        };
    }

    // ── Authorization ─────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_NoClientProfile_Returns404()
    {
        // User with no ClientProfile in the database
        var db = new MockDbBuilder().Build();

        var ep = Factory.Create<GetMyPhotosEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_clientUserId, AppRoles.Client))),
            db,
            new FakeBlobStorageService());

        await ep.HandleAsync(
            new GetMyPhotosRequest(),
            TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(404);
    }

    // ── Basic flat response ───────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WithPhotos_Returns200WithFlatList()
    {
        var clientProfile = EntityBuilder.ClientProfile
            .WithUserId(_clientUserId)
            .WithId(10)
            .Build();

        var photo1 = MakePhoto(10, PlanPhotoCategory.Body);
        var photo2 = MakePhoto(10, PlanPhotoCategory.Food);

        var db = new MockDbBuilder()
            .With(clientProfile)
            .With(photo1)
            .With(photo2)
            .Build();

        var ep = Factory.Create<GetMyPhotosEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_clientUserId, AppRoles.Client))),
            db,
            new FakeBlobStorageService());

        await ep.HandleAsync(
            new GetMyPhotosRequest { Page = 1, PageSize = 20 },
            TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(200);
        ep.Response.Photos.Should().NotBeNull();
        ep.Response.Photos!.Count.Should().Be(2);
        ep.Response.Groups.Should().BeNull();
        ep.HttpContext.Response.Headers["X-Total-Count"].ToString().Should().Be("2");
    }

    [Fact]
    public async Task HandleAsync_EmptyResult_Returns200WithEmptyList()
    {
        var clientProfile = EntityBuilder.ClientProfile
            .WithUserId(_clientUserId)
            .WithId(10)
            .Build();

        var db = new MockDbBuilder()
            .With(clientProfile)
            .Build(); // no photos

        var ep = Factory.Create<GetMyPhotosEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_clientUserId, AppRoles.Client))),
            db,
            new FakeBlobStorageService());

        await ep.HandleAsync(
            new GetMyPhotosRequest { Page = 1, PageSize = 20 },
            TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(200);
        ep.Response.Photos.Should().NotBeNull().And.BeEmpty();
        ep.HttpContext.Response.Headers["X-Total-Count"].ToString().Should().Be("0");
    }

    // ── Signed read URLs (F9) ────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_WithPhotos_ReturnsSignedReadUrlNotStoredValue()
    {
        var clientProfile = EntityBuilder.ClientProfile
            .WithUserId(_clientUserId)
            .WithId(10)
            .Build();

        var photo = MakePhoto(10, PlanPhotoCategory.Body);

        var db = new MockDbBuilder()
            .With(clientProfile)
            .With(photo)
            .Build();

        var blobStorage = new FakeBlobStorageService();

        var ep = Factory.Create<GetMyPhotosEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_clientUserId, AppRoles.Client))),
            db,
            blobStorage);

        await ep.HandleAsync(
            new GetMyPhotosRequest { Page = 1, PageSize = 20 },
            TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(200);

        // Positive control: the stored BlobUrl reached the signing call verbatim.
        blobStorage.SignedUrlRequests.Should().Contain("https://blob/photo.jpg");

        // Negative control: DisplayUrl carries the signed marker a revoked-link professional
        // could not keep re-fetching forever (F9), while BlobUrl stays the canonical, permanent
        // identity value so a client can safely echo it back on a later write.
        ep.Response.Photos!.Single().DisplayUrl.Should().Be("https://blob/photo.jpg?signed=test");
        ep.Response.Photos!.Single().BlobUrl.Should().Be("https://blob/photo.jpg");
    }

    [Fact]
    public async Task HandleAsync_GroupByMonth_ReturnsSignedReadUrlNotStoredValue()
    {
        var clientProfile = EntityBuilder.ClientProfile
            .WithUserId(_clientUserId)
            .WithId(10)
            .Build();

        var db = new MockDbBuilder()
            .With(clientProfile)
            .With(MakePhoto(10, takenAt: new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc)))
            .Build();

        var blobStorage = new FakeBlobStorageService();

        var ep = Factory.Create<GetMyPhotosEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_clientUserId, AppRoles.Client))),
            db,
            blobStorage);

        await ep.HandleAsync(
            new GetMyPhotosRequest { GroupByMonth = true, Page = 1, PageSize = 20 },
            TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(200);
        blobStorage.SignedUrlRequests.Should().Contain("https://blob/photo.jpg");
        ep.Response.Groups!.Single().Photos.Single().DisplayUrl
            .Should().Be("https://blob/photo.jpg?signed=test");
        ep.Response.Groups!.Single().Photos.Single().BlobUrl
            .Should().Be("https://blob/photo.jpg");
    }

    // ── Diary request link ───────────────────────────────────────────────────

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandleAsync_PhotoAnsweringDiaryRequest_ReturnsDiaryRequestId(bool groupByMonth)
    {
        var clientProfile = EntityBuilder.ClientProfile
            .WithUserId(_clientUserId)
            .WithId(10)
            .Build();

        var diaryRequestId = Guid.NewGuid();
        var answer = EntityBuilder.PlanPhoto.ForClient(10, _clientUserId).AnsweringRequest(diaryRequestId).Build();
        var free = EntityBuilder.PlanPhoto.ForClient(10, _clientUserId).Build();

        var db = new MockDbBuilder()
            .With(clientProfile)
            .With(answer)
            .With(free)
            .Build();

        var ep = Factory.Create<GetMyPhotosEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_clientUserId, AppRoles.Client))),
            db,
            new FakeBlobStorageService());

        await ep.HandleAsync(
            new GetMyPhotosRequest { GroupByMonth = groupByMonth, Page = 1, PageSize = 20 },
            TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(200);

        var photos = groupByMonth
            ? ep.Response.Groups!.SelectMany(g => g.Photos).ToList()
            : ep.Response.Photos!;

        photos.Should().HaveCount(2);
        photos.Single(p => p.Id == answer.PublicId).DiaryRequestId.Should().Be(diaryRequestId);
        photos.Single(p => p.Id == free.PublicId).DiaryRequestId.Should().BeNull();
    }

    // ── Pagination ────────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_Pagination_ReturnsCorrectPage()
    {
        var clientProfile = EntityBuilder.ClientProfile
            .WithUserId(_clientUserId)
            .WithId(10)
            .Build();

        var builder = new MockDbBuilder().With(clientProfile);

        for (var i = 0; i < 5; i++)
        {
            builder.With(MakePhoto(10, PlanPhotoCategory.Body, DateTime.UtcNow.AddDays(-i)));
        }

        var db = builder.Build();

        var ep = Factory.Create<GetMyPhotosEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_clientUserId, AppRoles.Client))),
            db,
            new FakeBlobStorageService());

        await ep.HandleAsync(
            new GetMyPhotosRequest { Page = 2, PageSize = 2 },
            TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(200);
        ep.Response.Photos!.Count.Should().Be(2);
        ep.HttpContext.Response.Headers["X-Total-Count"].ToString().Should().Be("5");
    }

    // ── Category filter ───────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_CategoryFilter_ReturnsOnlyMatchingPhotos()
    {
        var clientProfile = EntityBuilder.ClientProfile
            .WithUserId(_clientUserId)
            .WithId(10)
            .Build();

        var db = new MockDbBuilder()
            .With(clientProfile)
            .With(MakePhoto(10, PlanPhotoCategory.Body))
            .With(MakePhoto(10, PlanPhotoCategory.Food))
            .With(MakePhoto(10, PlanPhotoCategory.FreeForm))
            .Build();

        var ep = Factory.Create<GetMyPhotosEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_clientUserId, AppRoles.Client))),
            db,
            new FakeBlobStorageService());

        await ep.HandleAsync(
            new GetMyPhotosRequest
            {
                Category = PlanPhotoCategory.Body,
                Page = 1,
                PageSize = 20,
            },
            TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(200);
        ep.Response.Photos!.Should().HaveCount(1);
        ep.Response.Photos[0].Category.Should().Be(PlanPhotoCategory.Body);
        ep.HttpContext.Response.Headers["X-Total-Count"].ToString().Should().Be("1");
    }

    // ── Date filter ───────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_DateFilter_ReturnsOnlyPhotosInRange()
    {
        var clientProfile = EntityBuilder.ClientProfile
            .WithUserId(_clientUserId)
            .WithId(10)
            .Build();

        var anchor = new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc);

        var db = new MockDbBuilder()
            .With(clientProfile)
            .With(MakePhoto(10, takenAt: anchor.AddDays(-10))) // before From
            .With(MakePhoto(10, takenAt: anchor))               // on From boundary (inclusive)
            .With(MakePhoto(10, takenAt: anchor.AddDays(5)))    // inside range
            .With(MakePhoto(10, takenAt: anchor.AddDays(20)))   // after To
            .Build();

        var ep = Factory.Create<GetMyPhotosEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_clientUserId, AppRoles.Client))),
            db,
            new FakeBlobStorageService());

        await ep.HandleAsync(
            new GetMyPhotosRequest
            {
                From = anchor,
                To = anchor.AddDays(10),
                Page = 1,
                PageSize = 20,
            },
            TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(200);
        ep.Response.Photos!.Count.Should().Be(2);
        ep.HttpContext.Response.Headers["X-Total-Count"].ToString().Should().Be("2");
    }

    // ── Group by month ────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_GroupByMonth_ReturnsGroupedResponse()
    {
        var clientProfile = EntityBuilder.ClientProfile
            .WithUserId(_clientUserId)
            .WithId(10)
            .Build();

        var db = new MockDbBuilder()
            .With(clientProfile)
            .With(MakePhoto(10, takenAt: new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc)))
            .With(MakePhoto(10, takenAt: new DateTime(2026, 1, 20, 0, 0, 0, DateTimeKind.Utc)))
            .With(MakePhoto(10, takenAt: new DateTime(2026, 2, 5, 0, 0, 0, DateTimeKind.Utc)))
            .Build();

        var ep = Factory.Create<GetMyPhotosEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_clientUserId, AppRoles.Client))),
            db,
            new FakeBlobStorageService());

        await ep.HandleAsync(
            new GetMyPhotosRequest { GroupByMonth = true, Page = 1, PageSize = 20 },
            TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(200);
        ep.Response.Groups.Should().NotBeNull();
        ep.Response.Photos.Should().BeNull();
        ep.Response.Groups!.Should().HaveCount(2);
        ep.Response.Groups[0].YearMonth.Should().Be("2026-02"); // descending
        ep.Response.Groups[1].YearMonth.Should().Be("2026-01");
        ep.Response.Groups[1].Photos.Should().HaveCount(2);
        ep.HttpContext.Response.Headers["X-Total-Count"].ToString().Should().Be("2");
    }

    [Fact]
    public async Task HandleAsync_GroupByMonthFalse_ReturnsPhotosNotGroups()
    {
        var clientProfile = EntityBuilder.ClientProfile
            .WithUserId(_clientUserId)
            .WithId(10)
            .Build();

        var db = new MockDbBuilder()
            .With(clientProfile)
            .With(MakePhoto(10, takenAt: new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc)))
            .Build();

        var ep = Factory.Create<GetMyPhotosEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_clientUserId, AppRoles.Client))),
            db,
            new FakeBlobStorageService());

        await ep.HandleAsync(
            new GetMyPhotosRequest { GroupByMonth = false, Page = 1, PageSize = 20 },
            TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(200);
        ep.Response.Photos.Should().NotBeNull();
        ep.Response.Groups.Should().BeNull();
    }

    [Fact]
    public async Task HandleAsync_GroupByMonth_Pagination_PaginatesGroups()
    {
        var clientProfile = EntityBuilder.ClientProfile
            .WithUserId(_clientUserId)
            .WithId(10)
            .Build();

        var db = new MockDbBuilder()
            .With(clientProfile)
            .With(MakePhoto(10, takenAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)))
            .With(MakePhoto(10, takenAt: new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)))
            .With(MakePhoto(10, takenAt: new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc)))
            .Build();

        var ep = Factory.Create<GetMyPhotosEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_clientUserId, AppRoles.Client))),
            db,
            new FakeBlobStorageService());

        await ep.HandleAsync(
            new GetMyPhotosRequest { GroupByMonth = true, Page = 2, PageSize = 2 },
            TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(200);
        // 3 groups total, page 2 with size 2 → 1 group (oldest month)
        ep.Response.Groups!.Should().HaveCount(1);
        ep.Response.Groups[0].YearMonth.Should().Be("2026-01");
        ep.HttpContext.Response.Headers["X-Total-Count"].ToString().Should().Be("3");
    }
}
