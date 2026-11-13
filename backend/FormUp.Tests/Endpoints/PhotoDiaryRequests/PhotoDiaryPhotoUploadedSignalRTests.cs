using System.Security.Claims;
using FastEndpoints;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Features.PhotoDiaryRequests;
using FormUp.Application.Features.ClientPhotos;
using FormUp.Application.Features.ClientPhotos.FinalizePlanPhoto;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Application.Infrastructure.Services;
using FormUp.Tests.Builders;
using FormUp.Tests.Endpoints.NutritionPlans;
using FormUp.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ApplicationPhotoRequest = FormUp.Application.Domain.Entities.PhotoDiaryRequest;

namespace FormUp.Tests.Endpoints.PhotoDiaryRequests;

/// <summary>
/// Verifies the <c>photoDiaryPhotoUploaded</c> SignalR broadcast emitted by
/// <see cref="FinalizePlanPhotoEndpoint"/> when a photo is linked to an active diary request.
///
/// Covers:
/// <list type="bullet">
///   <item>Upload with diary request → professional receives <c>photoDiaryPhotoUploaded</c>.</item>
///   <item>Upload without diary request → <c>photoDiaryPhotoUploaded</c> is NOT emitted.</item>
///   <item>DayIndex computation: AcceptedAt set → computed; AcceptedAt null → 1.</item>
///   <item>Best-effort: broadcast failure does not fail the HTTP response.</item>
/// </list>
/// </summary>
public class PhotoDiaryPhotoUploadedSignalRTests
{
    // ── Shared identities ────────────────────────────────────────────────────────

    private readonly Guid _clientId = Guid.NewGuid();
    private readonly Guid _nutritionistId = Guid.NewGuid();

    // ── Dependencies ─────────────────────────────────────────────────────────────

    private readonly IRealtimeNotifier _notifier = Substitute.For<IRealtimeNotifier>();
    private readonly ILogger<FinalizePlanPhotoEndpoint> _logger =
        Substitute.For<ILogger<FinalizePlanPhotoEndpoint>>();

    // ── DB builder helpers ────────────────────────────────────────────────────────

    private ApplicationUser MakeClientUser() => new()
    {
        Id = _clientId,
        FirstName = "Petr",
        LastName = "Novak",
        Email = "petr@example.com",
        UserName = "petr@example.com",
    };

    private ClientProfile MakeClientProfile(ApplicationUser user) => new()
    {
        Id = 1,
        UserId = _clientId,
        PublicId = _clientId,
        User = user,
    };

    private ApplicationPhotoRequest MakeDiaryRequest(
        long linkId,
        ClientProfessionalLink link,
        PhotoDiaryStatus status = PhotoDiaryStatus.Accepted,
        DateTimeOffset? acceptedAt = null) => new()
    {
        Id = Guid.NewGuid(),
        ProfessionalId = _nutritionistId,
        LinkId = linkId,
        Link = link,
        DurationDays = 7,
        Status = status,
        Mode = PhotoDiaryMode.Workflow,
        AcceptedAt = acceptedAt ?? DateTimeOffset.UtcNow.AddDays(-2),
        CreatedAt = DateTimeOffset.UtcNow.AddDays(-3),
        UpdatedAt = DateTimeOffset.UtcNow.AddDays(-2),
    };

    // ── Mongo helper ──────────────────────────────────────────────────────────────

    private IMongoContext CreateMongoWithNutritionPlan(Guid planId) =>
        PlanTestHelpers.CreateMockMongo([PlanTestHelpers.CreatePlan(
            externalId: planId,
            clientId: _clientId,
            nutritionistId: _nutritionistId,
            status: NutritionPlanStatus.Active)]);

    // ── Endpoint factory ──────────────────────────────────────────────────────────

    private FinalizePlanPhotoEndpoint CreateEndpoint(
        IMongoContext mongo, IApplicationDbContext db, IClientLinkAuthorizationService? linkAuthorizationService = null) =>
        Factory.Create<FinalizePlanPhotoEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(_clientId, AppRoles.Client))),
            mongo, db, _notifier, linkAuthorizationService ?? EndpointTestHelpers.CreateGrantingLinkAuthorizationService(), _logger, new FakeBlobStorageService());

    // ── Tests ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Upload_WithDiaryRequest_EmitsPhotoDiaryPhotoUploaded_ToProfessional()
    {
        var planId = Guid.NewGuid();
        var clientUser = MakeClientUser();
        var clientProfile = MakeClientProfile(clientUser);
        var link = new ClientProfessionalLink
        {
            Id = 1,
            ProfessionalProfileId = 2,
            ClientProfileId = clientProfile.Id,
            ClientProfile = clientProfile,
            ConnectionState = ConnectionState.Connected,
            ProfessionalRole = UserRole.Nutritionist,
            PublicId = Guid.NewGuid(),
            DateCreated = DateTime.UtcNow,
        };
        var diaryReq = MakeDiaryRequest(link.Id, link);

        var db = new MockDbBuilder()
            .With(clientUser)
            .With(clientProfile)
            .With(link)
            .With(diaryReq)
            .Build();

        var mongo = CreateMongoWithNutritionPlan(planId);
        var ep = CreateEndpoint(mongo, db);

        await ep.HandleAsync(new FinalizePlanPhotoRequest
        {
            PlanId = planId,
            BlobUrl = $"plan-photos/{planId}/{Guid.NewGuid()}.jpg",
            Category = PlanPhotoCategory.Body,
            DiaryRequestId = diaryReq.Id,
        }, TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(201);

        // Professional must receive photoDiaryPhotoUploaded
        await _notifier.Received(1).NotifyAsync(
            _nutritionistId,          // recipient = professional group
            "photodiaryphotouploaded",
            Arg.Is<PhotoDiaryPhotoUploadedEvent>(e =>
                e.RequestId == diaryReq.Id &&
                e.ClientName == "Petr Novak"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Upload_WithDiaryRequest_EventContainsCorrectPhotoId()
    {
        var planId = Guid.NewGuid();
        var clientUser = MakeClientUser();
        var clientProfile = MakeClientProfile(clientUser);
        var link = new ClientProfessionalLink
        {
            Id = 1,
            ProfessionalProfileId = 2,
            ClientProfileId = clientProfile.Id,
            ClientProfile = clientProfile,
            ConnectionState = ConnectionState.Connected,
            ProfessionalRole = UserRole.Nutritionist,
            PublicId = Guid.NewGuid(),
            DateCreated = DateTime.UtcNow,
        };
        var diaryReq = MakeDiaryRequest(link.Id, link);

        var db = new MockDbBuilder()
            .With(clientUser)
            .With(clientProfile)
            .With(link)
            .With(diaryReq)
            .Build();

        var mongo = CreateMongoWithNutritionPlan(planId);
        var ep = CreateEndpoint(mongo, db);

        await ep.HandleAsync(new FinalizePlanPhotoRequest
        {
            PlanId = planId,
            BlobUrl = $"plan-photos/{planId}/{Guid.NewGuid()}.jpg",
            Category = PlanPhotoCategory.Body,
            DiaryRequestId = diaryReq.Id,
        }, TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(201);

        // PhotoId in the diary event must match the photo returned by the endpoint
        await _notifier.Received(1).NotifyAsync(
            _nutritionistId,
            "photodiaryphotouploaded",
            Arg.Is<PhotoDiaryPhotoUploadedEvent>(e => e.PhotoId == ep.Response.Id),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Upload_WithDiaryRequest_DayIndex_ComputedFromAcceptedAt()
    {
        var planId = Guid.NewGuid();
        var clientUser = MakeClientUser();
        var clientProfile = MakeClientProfile(clientUser);
        var link = new ClientProfessionalLink
        {
            Id = 1,
            ProfessionalProfileId = 2,
            ClientProfileId = clientProfile.Id,
            ClientProfile = clientProfile,
            ConnectionState = ConnectionState.Connected,
            ProfessionalRole = UserRole.Nutritionist,
            PublicId = Guid.NewGuid(),
            DateCreated = DateTime.UtcNow,
        };

        // AcceptedAt 3 days ago → DayIndex should be 4
        var acceptedAt = DateTimeOffset.UtcNow.AddDays(-3);
        var diaryReq = MakeDiaryRequest(link.Id, link, PhotoDiaryStatus.InProgress, acceptedAt);

        var db = new MockDbBuilder()
            .With(clientUser)
            .With(clientProfile)
            .With(link)
            .With(diaryReq)
            .Build();

        var mongo = CreateMongoWithNutritionPlan(planId);
        var ep = CreateEndpoint(mongo, db);

        await ep.HandleAsync(new FinalizePlanPhotoRequest
        {
            PlanId = planId,
            BlobUrl = $"plan-photos/{planId}/{Guid.NewGuid()}.jpg",
            Category = PlanPhotoCategory.Body,
            DiaryRequestId = diaryReq.Id,
        }, TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(201);

        await _notifier.Received(1).NotifyAsync(
            _nutritionistId,
            "photodiaryphotouploaded",
            Arg.Is<PhotoDiaryPhotoUploadedEvent>(e => e.DayIndex == 4),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Upload_WithoutDiaryRequest_DoesNotEmitPhotoDiaryPhotoUploaded()
    {
        var planId = Guid.NewGuid();
        var clientUser = MakeClientUser();
        var clientProfile = MakeClientProfile(clientUser);

        var db = new MockDbBuilder()
            .With(clientUser)
            .With(clientProfile)
            .Build();

        var mongo = CreateMongoWithNutritionPlan(planId);
        var ep = CreateEndpoint(mongo, db);

        await ep.HandleAsync(new FinalizePlanPhotoRequest
        {
            PlanId = planId,
            BlobUrl = $"plan-photos/{planId}/{Guid.NewGuid()}.jpg",
            Category = PlanPhotoCategory.Body,
            // No DiaryRequestId — plain upload
        }, TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(201);

        // photoDiaryPhotoUploaded must NOT be emitted for a plain upload
        await _notifier.DidNotReceive().NotifyAsync(
            Arg.Any<Guid>(),
            "photodiaryphotouploaded",
            Arg.Any<object>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Upload_WithDiaryRequest_BroadcastThrows_MutationStillSucceeds()
    {
        _notifier
            .NotifyAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("hub unavailable")));

        var planId = Guid.NewGuid();
        var clientUser = MakeClientUser();
        var clientProfile = MakeClientProfile(clientUser);
        var link = new ClientProfessionalLink
        {
            Id = 1,
            ProfessionalProfileId = 2,
            ClientProfileId = clientProfile.Id,
            ClientProfile = clientProfile,
            ConnectionState = ConnectionState.Connected,
            ProfessionalRole = UserRole.Nutritionist,
            PublicId = Guid.NewGuid(),
            DateCreated = DateTime.UtcNow,
        };
        var diaryReq = MakeDiaryRequest(link.Id, link);

        var db = new MockDbBuilder()
            .With(clientUser)
            .With(clientProfile)
            .With(link)
            .With(diaryReq)
            .Build();

        var mongo = CreateMongoWithNutritionPlan(planId);
        var ep = CreateEndpoint(mongo, db);

        var act = () => ep.HandleAsync(new FinalizePlanPhotoRequest
        {
            PlanId = planId,
            BlobUrl = $"plan-photos/{planId}/{Guid.NewGuid()}.jpg",
            Category = PlanPhotoCategory.Body,
            DiaryRequestId = diaryReq.Id,
        }, TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
        ep.HttpContext.Response.StatusCode.Should().Be(201);
    }

    /// <summary>
    /// Falsifiability anchor for #989: proves the moved
    /// <c>PhotoDiaryRequestOwnership.IsOwnedByClient</c> predicate still enforces the link's
    /// <c>IsActive</c> flag after the promotion to <c>Domain/Extensions</c>. A diary request whose
    /// link has been deactivated must not be finalizable even though the link still points at the
    /// caller's own client profile — mirrors <c>IsDiaryRequestOwnedByClient</c>'s original
    /// <c>&amp;&amp; request.Link.IsActive</c> term. Verified falsifiable by mutation: dropping that
    /// term from the shared predicate turns this red.
    /// </summary>
    [Fact]
    public async Task Upload_WithDiaryRequest_DeactivatedLink_Returns404()
    {
        var planId = Guid.NewGuid();
        var clientUser = MakeClientUser();
        var clientProfile = MakeClientProfile(clientUser);
        var link = new ClientProfessionalLink
        {
            Id = 1,
            ProfessionalProfileId = 2,
            ClientProfileId = clientProfile.Id,
            ClientProfile = clientProfile,
            ConnectionState = ConnectionState.Ended,
            ProfessionalRole = UserRole.Nutritionist,
            PublicId = Guid.NewGuid(),
            DateCreated = DateTime.UtcNow,
        };
        var diaryReq = MakeDiaryRequest(link.Id, link);

        var db = new MockDbBuilder()
            .With(clientUser)
            .With(clientProfile)
            .With(link)
            .With(diaryReq)
            .Build();

        var mongo = CreateMongoWithNutritionPlan(planId);
        var ep = CreateEndpoint(mongo, db);

        await ep.HandleAsync(new FinalizePlanPhotoRequest
        {
            PlanId = planId,
            BlobUrl = $"plan-photos/{planId}/{Guid.NewGuid()}.jpg",
            Category = PlanPhotoCategory.Body,
            DiaryRequestId = diaryReq.Id,
        }, TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(404);

        await _notifier.DidNotReceive().NotifyAsync(
            Arg.Any<Guid>(),
            "photodiaryphotouploaded",
            Arg.Any<object>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Confirms the moved predicate preserves the other-client branch: a diary request whose link
    /// points at a different client's profile must 404, without leaking whether the request itself
    /// exists.
    /// </summary>
    [Fact]
    public async Task Upload_WithDiaryRequest_LinkedToDifferentClient_Returns404()
    {
        var planId = Guid.NewGuid();
        var clientUser = MakeClientUser();
        var clientProfile = MakeClientProfile(clientUser);

        var otherClientUser = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            FirstName = "Jana",
            LastName = "Svobodova",
            Email = "jana@example.com",
            UserName = "jana@example.com",
        };
        var otherClientProfile = new ClientProfile
        {
            Id = 2,
            UserId = otherClientUser.Id,
            PublicId = otherClientUser.Id,
            User = otherClientUser,
        };
        var link = new ClientProfessionalLink
        {
            Id = 1,
            ProfessionalProfileId = 2,
            ClientProfileId = otherClientProfile.Id,
            ClientProfile = otherClientProfile,
            ConnectionState = ConnectionState.Connected,
            ProfessionalRole = UserRole.Nutritionist,
            PublicId = Guid.NewGuid(),
            DateCreated = DateTime.UtcNow,
        };
        var diaryReq = MakeDiaryRequest(link.Id, link);

        var db = new MockDbBuilder()
            .With(clientUser)
            .With(clientProfile)
            .With(otherClientUser)
            .With(otherClientProfile)
            .With(link)
            .With(diaryReq)
            .Build();

        var mongo = CreateMongoWithNutritionPlan(planId);
        var ep = CreateEndpoint(mongo, db);

        await ep.HandleAsync(new FinalizePlanPhotoRequest
        {
            PlanId = planId,
            BlobUrl = $"plan-photos/{planId}/{Guid.NewGuid()}.jpg",
            Category = PlanPhotoCategory.Body,
            DiaryRequestId = diaryReq.Id,
        }, TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(404);

        await _notifier.DidNotReceive().NotifyAsync(
            Arg.Any<Guid>(),
            "photodiaryphotouploaded",
            Arg.Any<object>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Upload_WithDiaryRequest_ProfessionalLacksCapability_DoesNotEmitPhotoDiaryPhotoUploaded()
    {
        // The nutritionist authored the plan and the diary request, but no longer holds a live,
        // nutrition-capable ClientProfessionalLink — the same capability gate that protects
        // planPhotoUploaded (F6 residual) also covers this diary-tracking event.
        var planId = Guid.NewGuid();
        var clientUser = MakeClientUser();
        var clientProfile = MakeClientProfile(clientUser);
        var link = new ClientProfessionalLink
        {
            Id = 1,
            ProfessionalProfileId = 2,
            ClientProfileId = clientProfile.Id,
            ClientProfile = clientProfile,
            ConnectionState = ConnectionState.Connected,
            ProfessionalRole = UserRole.Nutritionist,
            PublicId = Guid.NewGuid(),
            DateCreated = DateTime.UtcNow,
        };
        var diaryReq = MakeDiaryRequest(link.Id, link);

        var db = new MockDbBuilder()
            .With(clientUser)
            .With(clientProfile)
            .With(link)
            .With(diaryReq)
            .Build();

        var mongo = CreateMongoWithNutritionPlan(planId);
        var ep = CreateEndpoint(mongo, db, EndpointTestHelpers.CreateGrantingLinkAuthorizationService(canViewNutritionPlans: false));

        await ep.HandleAsync(new FinalizePlanPhotoRequest
        {
            PlanId = planId,
            BlobUrl = $"plan-photos/{planId}/{Guid.NewGuid()}.jpg",
            Category = PlanPhotoCategory.Body,
            DiaryRequestId = diaryReq.Id,
        }, TestContext.Current.CancellationToken);

        // The PlanPhoto row and diary status transition still happen — only the broadcasts
        // are gated.
        ep.HttpContext.Response.StatusCode.Should().Be(201);

        await _notifier.DidNotReceive().NotifyAsync(
            Arg.Any<Guid>(),
            "photodiaryphotouploaded",
            Arg.Any<object>(),
            Arg.Any<CancellationToken>());
    }
}
