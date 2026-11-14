using System.Security.Claims;
using FastEndpoints;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.ClientPhotos.GetTrainerClientPhotos;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;

namespace FormUp.Tests.Endpoints.ClientPhotos;

/// <summary>
/// Unit tests for <see cref="GetTrainerClientPhotosEndpoint"/>.
/// Covers authorization, pagination, category filter, date filter, and month grouping.
/// </summary>
/// <remarks>
/// Authorization is exercised against the REAL <see cref="ClientLinkAuthorizationService"/>
/// (constructed over the same mocked <see cref="IApplicationDbContext"/>), not a substitute —
/// the deny-path tests below seed the exact professional-profile / client-profile / link rows
/// the production service reads, so a regression in the service itself (not just the endpoint's
/// use of it) fails these tests too.
/// </remarks>
public class GetTrainerClientPhotosEndpointTests
{
    private readonly Guid _trainerUserId = Guid.NewGuid();
    private readonly Guid _clientPublicId = Guid.NewGuid();

    /// <summary>
    /// Builds the real <see cref="ClientLinkAuthorizationService"/> over the given mocked
    /// <see cref="IApplicationDbContext"/>, so authorization tests exercise production logic.
    /// </summary>
    private static IClientLinkAuthorizationService CreateLinkAuthorizationService(IApplicationDbContext db) =>
        new ClientLinkAuthorizationService(db);

    // Builds a standard set of DB entities: trainer profile (id=1), client profile (id=2), active link.
    // Both capability flags default to true so every pre-existing call site is unaffected; the
    // flag-inversion tests below are the only callers that pass a narrower link.
    private (MockDbBuilder builder, ProfessionalProfile trainerProfile, ClientProfile clientProfile)
        CreateLinkedSetup(bool canViewNutritionPlans = true, bool canViewTrainingPlans = true)
    {
        var trainerProfile = EntityBuilder.ProfessionalProfile
            .WithUserId(_trainerUserId)
            .WithId(1)
            .Build();

        var clientProfile = EntityBuilder.ClientProfile
            .WithPublicId(_clientPublicId)
            .WithId(2)
            .Build();

        var link = EntityBuilder.ClientProfessionalLink
            .WithProfessionalProfileId(1)
            .WithClientProfileId(2)
            .WithCanViewNutritionPlans(canViewNutritionPlans)
            .WithCanViewTrainingPlans(canViewTrainingPlans)
            .Build();

        var builder = new MockDbBuilder()
            .With(trainerProfile)
            .With(clientProfile)
            .With(link);

        return (builder, trainerProfile, clientProfile);
    }

    private static PlanPhoto MakePhoto(
        long clientProfileId,
        PlanPhotoCategory category = PlanPhotoCategory.Body,
        DateTime? takenAt = null,
        Guid? planId = null,
        Guid? diaryRequestId = null)
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
            PlanId = planId,
            DiaryRequestId = diaryRequestId,
        };
    }

    // ── Authorization ─────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_TrainerNotLinked_Returns404()
    {
        // Trainer has a profile but no link to this client
        var otherTrainerId = Guid.NewGuid();

        var trainerProfile = EntityBuilder.ProfessionalProfile
            .WithUserId(otherTrainerId)
            .WithId(1)
            .Build();

        var clientProfile = EntityBuilder.ClientProfile
            .WithPublicId(_clientPublicId)
            .WithId(2)
            .Build();

        // No link added to db
        var db = new MockDbBuilder()
            .With(trainerProfile)
            .With(clientProfile)
            .Build();

        var ep = Factory.Create<GetTrainerClientPhotosEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(otherTrainerId, AppRoles.Trainer)))),
            db,
            CreateLinkAuthorizationService(db),
            new FakeBlobStorageService(),
            new MockMongoBuilder().Build());

        await ep.HandleAsync(
            new GetTrainerClientPhotosRequest { ClientId = _clientPublicId },
            TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task HandleAsync_InactiveLink_Returns404()
    {
        var trainerProfile = EntityBuilder.ProfessionalProfile
            .WithUserId(_trainerUserId)
            .WithId(1)
            .Build();

        var clientProfile = EntityBuilder.ClientProfile
            .WithPublicId(_clientPublicId)
            .WithId(2)
            .Build();

        var inactiveLink = EntityBuilder.ClientProfessionalLink
            .WithProfessionalProfileId(1)
            .WithClientProfileId(2)
            .Inactive()
            .Build();

        var db = new MockDbBuilder()
            .With(trainerProfile)
            .With(clientProfile)
            .With(inactiveLink)
            .Build();

        var ep = Factory.Create<GetTrainerClientPhotosEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_trainerUserId, AppRoles.Trainer)))),
            db,
            CreateLinkAuthorizationService(db),
            new FakeBlobStorageService(),
            new MockMongoBuilder().Build());

        await ep.HandleAsync(
            new GetTrainerClientPhotosRequest { ClientId = _clientPublicId },
            TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(404);
    }

    /// <summary>
    /// The link is active but grants neither domain — <see cref="LinkCapabilities.GrantsNothing"/>
    /// denies at :75, matching #973's finding that a universal 403 is not this endpoint's
    /// contract. The <see cref="ClientProfile"/> row is still seeded so the endpoint's own
    /// GrantsNothing guard is what produces the 404, not the later clientProfile-is-null guard —
    /// otherwise this test would pass identically with the capability guard removed.
    /// </summary>
    [Fact]
    public async Task HandleAsync_GrantsNothing_Returns404()
    {
        var (builder, _, _) = CreateLinkedSetup(canViewNutritionPlans: false, canViewTrainingPlans: false);
        var db = builder.Build();

        var ep = Factory.Create<GetTrainerClientPhotosEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_trainerUserId, AppRoles.Trainer)))),
            db,
            CreateLinkAuthorizationService(db),
            new FakeBlobStorageService(),
            new MockMongoBuilder().Build());

        await ep.HandleAsync(
            new GetTrainerClientPhotosRequest { ClientId = _clientPublicId },
            TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(404);
    }

    // ── Basic flat response ───────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_ActiveLink_WithPhotos_Returns200WithFlatList()
    {
        var (builder, _, _) = CreateLinkedSetup();
        var photo1 = MakePhoto(2, PlanPhotoCategory.Body);
        var photo2 = MakePhoto(2, PlanPhotoCategory.Food);
        var db = builder.WithViewedPhoto(_trainerUserId, photo1).WithViewedPhoto(_trainerUserId, photo2).Build();

        var ep = Factory.Create<GetTrainerClientPhotosEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_trainerUserId, AppRoles.Trainer)))),
            db,
            CreateLinkAuthorizationService(db),
            new FakeBlobStorageService(),
            new MockMongoBuilder().Build());

        await ep.HandleAsync(
            new GetTrainerClientPhotosRequest { ClientId = _clientPublicId, Page = 1, PageSize = 20 },
            TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(200);
        ep.Response.Photos.Should().NotBeNull();
        ep.Response.Photos!.Count.Should().Be(2);
        ep.Response.Groups.Should().BeNull();
    }

    // ── Signed read URLs (F9) ────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_ActiveLink_ReturnsSignedReadUrlNotStoredValue()
    {
        var (builder, _, _) = CreateLinkedSetup();
        var photo = MakePhoto(2, PlanPhotoCategory.Body);
        var db = builder.WithViewedPhoto(_trainerUserId, photo).Build();

        var blobStorage = new FakeBlobStorageService();

        var ep = Factory.Create<GetTrainerClientPhotosEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_trainerUserId, AppRoles.Trainer)))),
            db,
            CreateLinkAuthorizationService(db),
            blobStorage,
            new MockMongoBuilder().Build());

        await ep.HandleAsync(
            new GetTrainerClientPhotosRequest { ClientId = _clientPublicId, Page = 1, PageSize = 20 },
            TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(200);

        // Positive control: the stored BlobUrl reached the signing call verbatim.
        blobStorage.SignedUrlRequests.Should().Contain("https://blob/photo.jpg");

        // Negative control: a professional whose link was later revoked, holding this exact
        // response from before revocation, must not be able to keep re-fetching the raw
        // permanent URL via DisplayUrl — only the short-lived signed marker is returned there
        // (F9) — while BlobUrl stays the canonical, permanent identity value.
        ep.Response.Photos!.Single().DisplayUrl.Should().Be("https://blob/photo.jpg?signed=test");
        ep.Response.Photos!.Single().BlobUrl.Should().Be("https://blob/photo.jpg");
    }

    [Fact]
    public async Task HandleAsync_EmptyResult_Returns200WithEmptyList()
    {
        var (builder, _, _) = CreateLinkedSetup();
        var db = builder.Build(); // no photos

        var ep = Factory.Create<GetTrainerClientPhotosEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_trainerUserId, AppRoles.Trainer)))),
            db,
            CreateLinkAuthorizationService(db),
            new FakeBlobStorageService(),
            new MockMongoBuilder().Build());

        await ep.HandleAsync(
            new GetTrainerClientPhotosRequest { ClientId = _clientPublicId, Page = 1, PageSize = 20 },
            TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(200);
        ep.Response.Photos.Should().NotBeNull().And.BeEmpty();
    }

    // ── Pagination ────────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_Pagination_ReturnsCorrectPage()
    {
        var (builder, _, _) = CreateLinkedSetup();

        // Add 5 photos
        for (var i = 0; i < 5; i++)
        {
            builder.WithViewedPhoto(_trainerUserId, MakePhoto(2, PlanPhotoCategory.Body,
                DateTime.UtcNow.AddDays(-i)));
        }

        var db = builder.Build();

        var ep = Factory.Create<GetTrainerClientPhotosEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_trainerUserId, AppRoles.Trainer)))),
            db,
            CreateLinkAuthorizationService(db),
            new FakeBlobStorageService(),
            new MockMongoBuilder().Build());

        await ep.HandleAsync(
            new GetTrainerClientPhotosRequest { ClientId = _clientPublicId, Page = 2, PageSize = 2 },
            TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(200);
        // Page 2 with pageSize 2 out of 5 items should give 2 items
        ep.Response.Photos!.Count.Should().Be(2);
        // X-Total-Count header should be 5
        ep.HttpContext.Response.Headers["X-Total-Count"].ToString().Should().Be("5");
    }

    [Fact]
    public async Task HandleAsync_LastPage_ReturnsRemainingItems()
    {
        var (builder, _, _) = CreateLinkedSetup();

        // 3 photos, page size 2 → page 2 should give 1 item
        for (var i = 0; i < 3; i++)
        {
            builder.WithViewedPhoto(_trainerUserId, MakePhoto(2, PlanPhotoCategory.Body, DateTime.UtcNow.AddDays(-i)));
        }

        var db = builder.Build();

        var ep = Factory.Create<GetTrainerClientPhotosEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_trainerUserId, AppRoles.Trainer)))),
            db,
            CreateLinkAuthorizationService(db),
            new FakeBlobStorageService(),
            new MockMongoBuilder().Build());

        await ep.HandleAsync(
            new GetTrainerClientPhotosRequest { ClientId = _clientPublicId, Page = 2, PageSize = 2 },
            TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(200);
        ep.Response.Photos!.Count.Should().Be(1);
        ep.HttpContext.Response.Headers["X-Total-Count"].ToString().Should().Be("3");
    }

    // ── Category filter ───────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_CategoryFilter_ReturnsOnlyMatchingPhotos()
    {
        var (builder, _, _) = CreateLinkedSetup();
        builder.WithViewedPhoto(_trainerUserId, MakePhoto(2, PlanPhotoCategory.Body));
        builder.WithViewedPhoto(_trainerUserId, MakePhoto(2, PlanPhotoCategory.Food));
        builder.WithViewedPhoto(_trainerUserId, MakePhoto(2, PlanPhotoCategory.FreeForm));
        var db = builder.Build();

        var ep = Factory.Create<GetTrainerClientPhotosEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_trainerUserId, AppRoles.Trainer)))),
            db,
            CreateLinkAuthorizationService(db),
            new FakeBlobStorageService(),
            new MockMongoBuilder().Build());

        await ep.HandleAsync(
            new GetTrainerClientPhotosRequest
            {
                ClientId = _clientPublicId,
                Category = PlanPhotoCategory.Food,
                Page = 1,
                PageSize = 20,
            },
            TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(200);
        ep.Response.Photos.Should().NotBeNull();
        ep.Response.Photos!.Should().HaveCount(1);
        ep.Response.Photos[0].Category.Should().Be(PlanPhotoCategory.Food);
        ep.HttpContext.Response.Headers["X-Total-Count"].ToString().Should().Be("1");
    }

    // ── Domain-scoped capability filter ──────────────────────────────────────

    /// <summary>
    /// A nutrition-only link (<c>CanViewTrainingPlans: false</c>) must never surface
    /// <see cref="PlanPhotoCategory.Training"/> photos, even unfiltered — the domain scoping at
    /// :117 is applied to the base query before the caller's own filters. Body and Food photos,
    /// which the link does grant, must still come back so the exclusion is distinguishable from
    /// an empty result.
    /// </summary>
    [Fact]
    public async Task HandleAsync_NutritionOnlyLink_ExcludesTrainingCategoryPhotos()
    {
        var (builder, _, _) = CreateLinkedSetup(canViewNutritionPlans: true, canViewTrainingPlans: false);
        builder.WithViewedPhoto(_trainerUserId, MakePhoto(2, PlanPhotoCategory.Food));
        builder.WithViewedPhoto(_trainerUserId, MakePhoto(2, PlanPhotoCategory.Training));
        builder.WithViewedPhoto(_trainerUserId, MakePhoto(2, PlanPhotoCategory.Body));
        var db = builder.Build();

        var ep = Factory.Create<GetTrainerClientPhotosEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_trainerUserId, AppRoles.Trainer)))),
            db,
            CreateLinkAuthorizationService(db),
            new FakeBlobStorageService(),
            new MockMongoBuilder().Build());

        await ep.HandleAsync(
            new GetTrainerClientPhotosRequest { ClientId = _clientPublicId, Page = 1, PageSize = 20 },
            TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(200);
        ep.Response.Photos.Should().NotBeNull();
        ep.Response.Photos!.Should().HaveCount(2);
        ep.Response.Photos!.Should().NotContain(p => p.Category == PlanPhotoCategory.Training);
        ep.Response.Photos!.Should().Contain(p => p.Category == PlanPhotoCategory.Food);
        ep.Response.Photos!.Should().Contain(p => p.Category == PlanPhotoCategory.Body);
    }

    /// <summary>
    /// A training-only link (<c>CanViewNutritionPlans: false</c>) must never surface
    /// <see cref="PlanPhotoCategory.Food"/> photos, even unfiltered — the domain scoping at :112
    /// is applied to the base query before the caller's own filters. Body and Training photos,
    /// which the link does grant, must still come back so the exclusion is distinguishable from
    /// an empty result.
    /// </summary>
    [Fact]
    public async Task HandleAsync_TrainingOnlyLink_ExcludesFoodCategoryPhotos()
    {
        var (builder, _, _) = CreateLinkedSetup(canViewNutritionPlans: false, canViewTrainingPlans: true);
        builder.WithViewedPhoto(_trainerUserId, MakePhoto(2, PlanPhotoCategory.Food));
        builder.WithViewedPhoto(_trainerUserId, MakePhoto(2, PlanPhotoCategory.Training));
        builder.WithViewedPhoto(_trainerUserId, MakePhoto(2, PlanPhotoCategory.Body));
        var db = builder.Build();

        var ep = Factory.Create<GetTrainerClientPhotosEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_trainerUserId, AppRoles.Trainer)))),
            db,
            CreateLinkAuthorizationService(db),
            new FakeBlobStorageService(),
            new MockMongoBuilder().Build());

        await ep.HandleAsync(
            new GetTrainerClientPhotosRequest { ClientId = _clientPublicId, Page = 1, PageSize = 20 },
            TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(200);
        ep.Response.Photos.Should().NotBeNull();
        ep.Response.Photos!.Should().HaveCount(2);
        ep.Response.Photos!.Should().NotContain(p => p.Category == PlanPhotoCategory.Food);
        ep.Response.Photos!.Should().Contain(p => p.Category == PlanPhotoCategory.Training);
        ep.Response.Photos!.Should().Contain(p => p.Category == PlanPhotoCategory.Body);
    }

    // ── Date filter ───────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_DateFilter_ReturnsOnlyPhotosInRange()
    {
        var (builder, _, _) = CreateLinkedSetup();
        var anchor = new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc);
        builder.WithViewedPhoto(_trainerUserId, MakePhoto(2, takenAt: anchor.AddDays(-10))); // before range
        builder.WithViewedPhoto(_trainerUserId, MakePhoto(2, takenAt: anchor));               // on From boundary
        builder.WithViewedPhoto(_trainerUserId, MakePhoto(2, takenAt: anchor.AddDays(5)));    // inside range
        builder.WithViewedPhoto(_trainerUserId, MakePhoto(2, takenAt: anchor.AddDays(20)));   // after range
        var db = builder.Build();

        var ep = Factory.Create<GetTrainerClientPhotosEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_trainerUserId, AppRoles.Trainer)))),
            db,
            CreateLinkAuthorizationService(db),
            new FakeBlobStorageService(),
            new MockMongoBuilder().Build());

        await ep.HandleAsync(
            new GetTrainerClientPhotosRequest
            {
                ClientId = _clientPublicId,
                From = anchor,
                To = anchor.AddDays(10),
                Page = 1,
                PageSize = 20,
            },
            TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(200);
        ep.Response.Photos!.Count.Should().Be(2); // anchor and anchor+5
        ep.HttpContext.Response.Headers["X-Total-Count"].ToString().Should().Be("2");
    }

    // ── Group by month ────────────────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_GroupByMonth_ReturnsGroupedResponse()
    {
        var (builder, _, _) = CreateLinkedSetup();
        builder.WithViewedPhoto(_trainerUserId, MakePhoto(2, takenAt: new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc)));
        builder.WithViewedPhoto(_trainerUserId, MakePhoto(2, takenAt: new DateTime(2026, 1, 20, 0, 0, 0, DateTimeKind.Utc)));
        builder.WithViewedPhoto(_trainerUserId, MakePhoto(2, takenAt: new DateTime(2026, 2, 5, 0, 0, 0, DateTimeKind.Utc)));
        var db = builder.Build();

        var ep = Factory.Create<GetTrainerClientPhotosEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_trainerUserId, AppRoles.Trainer)))),
            db,
            CreateLinkAuthorizationService(db),
            new FakeBlobStorageService(),
            new MockMongoBuilder().Build());

        await ep.HandleAsync(
            new GetTrainerClientPhotosRequest
            {
                ClientId = _clientPublicId,
                GroupByMonth = true,
                Page = 1,
                PageSize = 20,
            },
            TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(200);
        ep.Response.Groups.Should().NotBeNull();
        ep.Response.Photos.Should().BeNull();
        ep.Response.Groups!.Should().HaveCount(2);
        ep.Response.Groups[0].YearMonth.Should().Be("2026-02"); // descending order
        ep.Response.Groups[1].YearMonth.Should().Be("2026-01");
        ep.Response.Groups[1].Photos.Should().HaveCount(2);
        ep.HttpContext.Response.Headers["X-Total-Count"].ToString().Should().Be("2");
    }

    [Fact]
    public async Task HandleAsync_GroupByMonth_Flat_ReturnsPhotosNotGroups()
    {
        var (builder, _, _) = CreateLinkedSetup();
        builder.WithViewedPhoto(_trainerUserId, MakePhoto(2, takenAt: new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc)));
        var db = builder.Build();

        var ep = Factory.Create<GetTrainerClientPhotosEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_trainerUserId, AppRoles.Trainer)))),
            db,
            CreateLinkAuthorizationService(db),
            new FakeBlobStorageService(),
            new MockMongoBuilder().Build());

        await ep.HandleAsync(
            new GetTrainerClientPhotosRequest
            {
                ClientId = _clientPublicId,
                GroupByMonth = false,
                Page = 1,
                PageSize = 20,
            },
            TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(200);
        ep.Response.Photos.Should().NotBeNull();
        ep.Response.Groups.Should().BeNull();
    }

    [Fact]
    public async Task HandleAsync_GroupByMonth_Pagination_PaginatesGroups()
    {
        var (builder, _, _) = CreateLinkedSetup();
        // 3 distinct months
        builder.WithViewedPhoto(_trainerUserId, MakePhoto(2, takenAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        builder.WithViewedPhoto(_trainerUserId, MakePhoto(2, takenAt: new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)));
        builder.WithViewedPhoto(_trainerUserId, MakePhoto(2, takenAt: new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc)));
        var db = builder.Build();

        var ep = Factory.Create<GetTrainerClientPhotosEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_trainerUserId, AppRoles.Trainer)))),
            db,
            CreateLinkAuthorizationService(db),
            new FakeBlobStorageService(),
            new MockMongoBuilder().Build());

        await ep.HandleAsync(
            new GetTrainerClientPhotosRequest
            {
                ClientId = _clientPublicId,
                GroupByMonth = true,
                Page = 2,
                PageSize = 2,
            },
            TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(200);
        // Total 3 groups, page 2 with pageSize 2 → 1 group (oldest month)
        ep.Response.Groups!.Should().HaveCount(1);
        ep.Response.Groups[0].YearMonth.Should().Be("2026-01");
        ep.HttpContext.Response.Headers["X-Total-Count"].ToString().Should().Be("3");
    }

    // ── DiaryRequestId projection ─────────────────────────────────────────────

    [Fact]
    public async Task HandleAsync_PhotoWithDiaryRequestId_ExposesDiaryRequestIdInResponse()
    {
        var diaryRequestId = Guid.NewGuid();
        var (builder, _, _) = CreateLinkedSetup();
        // Photo with a DiaryRequestId set
        builder.WithViewedPhoto(_trainerUserId, MakePhoto(2, PlanPhotoCategory.Body, diaryRequestId: diaryRequestId));
        // Photo without a DiaryRequestId (null)
        builder.WithViewedPhoto(_trainerUserId, MakePhoto(2, PlanPhotoCategory.Food));
        var db = builder.Build();

        var ep = Factory.Create<GetTrainerClientPhotosEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_trainerUserId, AppRoles.Trainer)))),
            db,
            CreateLinkAuthorizationService(db),
            new FakeBlobStorageService(),
            new MockMongoBuilder().Build());

        await ep.HandleAsync(
            new GetTrainerClientPhotosRequest { ClientId = _clientPublicId, Page = 1, PageSize = 20 },
            TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(200);
        ep.Response.Photos.Should().NotBeNull();
        ep.Response.Photos!.Count.Should().Be(2);

        var withDiary = ep.Response.Photos.Single(p => p.Category == PlanPhotoCategory.Body);
        withDiary.DiaryRequestId.Should().Be(diaryRequestId);

        var withoutDiary = ep.Response.Photos.Single(p => p.Category == PlanPhotoCategory.Food);
        withoutDiary.DiaryRequestId.Should().BeNull();
    }

    // ── Plan filter ───────────────────────────────────────────────────────────

    [Fact]
    public async Task GetTrainerClientPhotos_WithPlanIdFilter_ReturnsOnlyPhotosForThatPlan()
    {
        var planA = Guid.NewGuid();
        var planB = Guid.NewGuid();

        var (builder, _, _) = CreateLinkedSetup();

        // 2 photos in plan A (different categories to be deterministic)
        builder.WithViewedPhoto(_trainerUserId, MakePhoto(2, PlanPhotoCategory.Body,
            new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), planId: planA));
        builder.WithViewedPhoto(_trainerUserId, MakePhoto(2, PlanPhotoCategory.Food,
            new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc), planId: planA));

        // 1 photo in plan B
        builder.WithViewedPhoto(_trainerUserId, MakePhoto(2, PlanPhotoCategory.FreeForm,
            new DateTime(2026, 3, 3, 0, 0, 0, DateTimeKind.Utc), planId: planB));

        var db = builder.Build();

        var ep = Factory.Create<GetTrainerClientPhotosEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_trainerUserId, AppRoles.Trainer)))),
            db,
            CreateLinkAuthorizationService(db),
            new FakeBlobStorageService(),
            new MockMongoBuilder().Build());

        await ep.HandleAsync(
            new GetTrainerClientPhotosRequest
            {
                ClientId = _clientPublicId,
                PlanId = planA,
                Page = 1,
                PageSize = 20,
            },
            TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(200);
        ep.Response.Photos.Should().NotBeNull();
        ep.Response.Photos!.Should().HaveCount(2);
        ep.Response.Photos.Should().AllSatisfy(p => p.PlanId.Should().Be(planA));
        ep.HttpContext.Response.Headers["X-Total-Count"].ToString().Should().Be("2");
    }
}
