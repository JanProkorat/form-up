using System.Security.Claims;
using FastEndpoints;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Features.PhotoDiaryRequests;
using FormUp.Application.Features.PhotoDiaryRequests.DismissRequest;
using FormUp.Application.Features.PhotoDiaryRequests.SubmitRequest;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ApplicationPhotoRequest = FormUp.Application.Domain.Entities.PhotoDiaryRequest;

namespace FormUp.Tests.Endpoints.PhotoDiaryRequests;

/// <summary>
/// Verifies SignalR broadcast behaviour for the diary lifecycle events
/// that do NOT require a full integration test host:
/// <list type="bullet">
///   <item><c>photoDiaryDismissed</c>   — emitted to the <b>professional</b> after DismissRequest.</item>
///   <item><c>photoDiarySubmitted</c>   — emitted to the <b>professional</b> after SubmitRequest.</item>
/// </list>
/// Uses mock-based unit tests (Factory.Create + NSubstitute) so these tests run without Docker.
///
/// The <c>photoDiaryRequested</c> event (emitted by CreateRequest) is covered by
/// <see cref="CreateRequestSignalRTests"/>, which uses <see cref="FormUp.Tests.Infrastructure.FitnessApiFactory"/>
/// because <c>CreateRequestEndpoint</c> calls <c>Send.CreatedAtAsync</c> which requires
/// a real <c>LinkGenerator</c> not available in unit-test mode.
/// </summary>
public class DiaryLifecycleSignalRTests
{
    // ── Shared identities ────────────────────────────────────────────────────────

    private readonly Guid _professionalId = Guid.NewGuid();
    private readonly Guid _clientId = Guid.NewGuid();

    // ── Notifier ─────────────────────────────────────────────────────────────────

    private readonly IRealtimeNotifier _notifier = Substitute.For<IRealtimeNotifier>();

    // ── Loggers ──────────────────────────────────────────────────────────────────

    private readonly ILogger<DismissRequestEndpoint> _dismissLogger =
        Substitute.For<ILogger<DismissRequestEndpoint>>();
    private readonly ILogger<SubmitRequestEndpoint> _submitLogger =
        Substitute.For<ILogger<SubmitRequestEndpoint>>();

    // ── Shared entity builders ───────────────────────────────────────────────────

    private ApplicationUser MakeProfessionalUser() => new()
    {
        Id = _professionalId,
        FirstName = "Jana",
        LastName = "Novakova",
        Email = "jana@example.com",
        UserName = "jana@example.com",
    };

    private ApplicationUser MakeClientUser() => new()
    {
        Id = _clientId,
        FirstName = "Petr",
        LastName = "Novak",
        Email = "petr@example.com",
        UserName = "petr@example.com",
    };

    private ProfessionalProfile MakeProfessionalProfile(ApplicationUser user) => new()
    {
        Id = 1,
        UserId = _professionalId,
        User = user,
    };

    private ClientProfile MakeClientProfile(ApplicationUser user) => new()
    {
        Id = 2,
        UserId = _clientId,
        PublicId = _clientId,
        User = user,
    };

    private ClientProfessionalLink MakeLink(ProfessionalProfile prof, ClientProfile client) => new()
    {
        Id = 1,
        ProfessionalProfileId = prof.Id,
        ClientProfileId = client.Id,
        ClientProfile = client,
        ConnectionState = ConnectionState.Connected,
        ProfessionalRole = UserRole.Nutritionist,
        PublicId = Guid.NewGuid(),
        DateCreated = DateTime.UtcNow,
    };

    /// <summary>Builds a pending diary request attached to a link.</summary>
    private ApplicationPhotoRequest MakeDiaryRequest(
        ClientProfessionalLink link,
        PhotoDiaryStatus status = PhotoDiaryStatus.Pending,
        DateTimeOffset? acceptedAt = null) => new()
    {
        Id = Guid.NewGuid(),
        ProfessionalId = _professionalId,
        LinkId = link.Id,
        Link = link,
        DurationDays = 7,
        Status = status,
        Mode = status is PhotoDiaryStatus.Accepted or PhotoDiaryStatus.InProgress or PhotoDiaryStatus.Completed
            ? PhotoDiaryMode.Bulk : null,
        AcceptedAt = acceptedAt ?? (status is PhotoDiaryStatus.Accepted or PhotoDiaryStatus.InProgress or PhotoDiaryStatus.Completed
            ? DateTimeOffset.UtcNow : null),
        CompletedAt = status == PhotoDiaryStatus.Completed ? DateTimeOffset.UtcNow : null,
        DismissReason = status == PhotoDiaryStatus.Dismissed ? "pre-dismissed" : null,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    // ── DismissRequest ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Dismiss_EmitsPhotoDiaryDismissed_ToProfessional()
    {
        var clientUser = MakeClientUser();
        var clientProfile = MakeClientProfile(clientUser);
        var profProfile = MakeProfessionalProfile(MakeProfessionalUser());
        var link = MakeLink(profProfile, clientProfile);
        var diaryReq = MakeDiaryRequest(link, PhotoDiaryStatus.Pending);

        var db = new MockDbBuilder()
            .With(clientUser)
            .With(clientProfile)
            .With(link)
            .With(diaryReq)
            .Build();

        var ep = Factory.Create<DismissRequestEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(_clientId, AppRoles.Client))),
            db, _notifier, _dismissLogger);

        await ep.HandleAsync(new DismissRequestRequest
        {
            Id = diaryReq.Id,
            Reason = null,
        }, TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(200);

        // The professional must receive exactly one photoDiaryDismissed event
        await _notifier.Received(1).NotifyAsync(
            _professionalId,        // recipient = professional group
            "photodiarydismissed",
            Arg.Is<PhotoDiaryDismissedEvent>(e =>
                e.RequestId == diaryReq.Id &&
                e.ClientName == "Petr Novak" &&
                e.DismissReason == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Dismiss_WithReason_EventContainsReason()
    {
        var clientUser = MakeClientUser();
        var clientProfile = MakeClientProfile(clientUser);
        var profProfile = MakeProfessionalProfile(MakeProfessionalUser());
        var link = MakeLink(profProfile, clientProfile);
        var diaryReq = MakeDiaryRequest(link, PhotoDiaryStatus.Pending);

        var db = new MockDbBuilder()
            .With(clientUser)
            .With(clientProfile)
            .With(link)
            .With(diaryReq)
            .Build();

        const string reason = "I prefer not to share photos.";

        var ep = Factory.Create<DismissRequestEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(_clientId, AppRoles.Client))),
            db, _notifier, _dismissLogger);

        await ep.HandleAsync(new DismissRequestRequest
        {
            Id = diaryReq.Id,
            Reason = reason,
        }, TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(200);

        await _notifier.Received(1).NotifyAsync(
            _professionalId,
            "photodiarydismissed",
            Arg.Is<PhotoDiaryDismissedEvent>(e =>
                e.RequestId == diaryReq.Id &&
                e.DismissReason == reason),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Dismiss_NotifiesProfessional_NotClient()
    {
        var clientUser = MakeClientUser();
        var clientProfile = MakeClientProfile(clientUser);
        var profProfile = MakeProfessionalProfile(MakeProfessionalUser());
        var link = MakeLink(profProfile, clientProfile);
        var diaryReq = MakeDiaryRequest(link, PhotoDiaryStatus.Pending);

        var db = new MockDbBuilder()
            .With(clientUser)
            .With(clientProfile)
            .With(link)
            .With(diaryReq)
            .Build();

        var ep = Factory.Create<DismissRequestEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(_clientId, AppRoles.Client))),
            db, _notifier, _dismissLogger);

        await ep.HandleAsync(new DismissRequestRequest
        {
            Id = diaryReq.Id,
        }, TestContext.Current.CancellationToken);

        // Client must NOT receive the event
        await _notifier.DidNotReceive().NotifyAsync(
            _clientId,
            Arg.Any<string>(),
            Arg.Any<object>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Dismiss_BroadcastThrows_MutationStillSucceeds()
    {
        _notifier
            .NotifyAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("hub unavailable")));

        var clientUser = MakeClientUser();
        var clientProfile = MakeClientProfile(clientUser);
        var profProfile = MakeProfessionalProfile(MakeProfessionalUser());
        var link = MakeLink(profProfile, clientProfile);
        var diaryReq = MakeDiaryRequest(link, PhotoDiaryStatus.Pending);

        var db = new MockDbBuilder()
            .With(clientUser)
            .With(clientProfile)
            .With(link)
            .With(diaryReq)
            .Build();

        var ep = Factory.Create<DismissRequestEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(_clientId, AppRoles.Client))),
            db, _notifier, _dismissLogger);

        var act = () => ep.HandleAsync(new DismissRequestRequest
        {
            Id = diaryReq.Id,
        }, TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
        ep.HttpContext.Response.StatusCode.Should().Be(200);
    }

    // ── SubmitRequest ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Submit_EmitsPhotoDiarySubmitted_ToProfessional()
    {
        var clientUser = MakeClientUser();
        var clientProfile = MakeClientProfile(clientUser);
        var profProfile = MakeProfessionalProfile(MakeProfessionalUser());
        var link = MakeLink(profProfile, clientProfile);
        var diaryReq = MakeDiaryRequest(link, PhotoDiaryStatus.InProgress);

        var db = new MockDbBuilder()
            .With(clientUser)
            .With(clientProfile)
            .With(link)
            .With(diaryReq)
            .Build();

        var ep = Factory.Create<SubmitRequestEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(_clientId, AppRoles.Client))),
            db, _notifier, Substitute.For<INotificationService>(), Substitute.For<INotificationEmailSender>(), _submitLogger);

        await ep.HandleAsync(new SubmitRequestRequest
        {
            Id = diaryReq.Id,
        }, TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(200);

        // The professional must receive exactly one photoDiarySubmitted event
        await _notifier.Received(1).NotifyAsync(
            _professionalId,       // recipient = professional group
            "photodiarysubmitted",
            Arg.Is<PhotoDiarySubmittedEvent>(e =>
                e.RequestId == diaryReq.Id &&
                e.ClientName == "Petr Novak"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Submit_EventContainsPhotoCount()
    {
        var clientUser = MakeClientUser();
        var clientProfile = MakeClientProfile(clientUser);
        var profProfile = MakeProfessionalProfile(MakeProfessionalUser());
        var link = MakeLink(profProfile, clientProfile);
        var diaryReq = MakeDiaryRequest(link, PhotoDiaryStatus.Accepted);

        // Pre-seed 3 plan photos linked to this diary request
        var photos = Enumerable.Range(0, 3).Select(_ => new PlanPhoto
        {
            PublicId = Guid.NewGuid(),
            ClientProfileId = clientProfile.Id,
            PlanId = Guid.NewGuid(),
            PlanType = PlanPhotoType.Nutrition,
            Category = PlanPhotoCategory.Body,
            BlobUrl = $"plan-photos/{Guid.NewGuid()}.jpg",
            TakenAt = DateTime.UtcNow,
            UploadedByUserId = _clientId,
            DiaryRequestId = diaryReq.Id,
            DateCreated = DateTime.UtcNow,
            DateUpdated = DateTime.UtcNow,
        }).ToList();

        var db = new MockDbBuilder()
            .With(clientUser)
            .With(clientProfile)
            .With(link)
            .With(diaryReq)
            .With(photos[0])
            .With(photos[1])
            .With(photos[2])
            .Build();

        var ep = Factory.Create<SubmitRequestEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(_clientId, AppRoles.Client))),
            db, _notifier, Substitute.For<INotificationService>(), Substitute.For<INotificationEmailSender>(), _submitLogger);

        await ep.HandleAsync(new SubmitRequestRequest
        {
            Id = diaryReq.Id,
        }, TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(200);

        await _notifier.Received(1).NotifyAsync(
            _professionalId,
            "photodiarysubmitted",
            Arg.Is<PhotoDiarySubmittedEvent>(e =>
                e.RequestId == diaryReq.Id &&
                e.PhotoCount == 3),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Submit_NotifiesProfessional_NotClient()
    {
        var clientUser = MakeClientUser();
        var clientProfile = MakeClientProfile(clientUser);
        var profProfile = MakeProfessionalProfile(MakeProfessionalUser());
        var link = MakeLink(profProfile, clientProfile);
        var diaryReq = MakeDiaryRequest(link, PhotoDiaryStatus.Accepted);

        var db = new MockDbBuilder()
            .With(clientUser)
            .With(clientProfile)
            .With(link)
            .With(diaryReq)
            .Build();

        var ep = Factory.Create<SubmitRequestEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(_clientId, AppRoles.Client))),
            db, _notifier, Substitute.For<INotificationService>(), Substitute.For<INotificationEmailSender>(), _submitLogger);

        await ep.HandleAsync(new SubmitRequestRequest
        {
            Id = diaryReq.Id,
        }, TestContext.Current.CancellationToken);

        // Client must NOT receive the event
        await _notifier.DidNotReceive().NotifyAsync(
            _clientId,
            Arg.Any<string>(),
            Arg.Any<object>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Submit_BroadcastThrows_MutationStillSucceeds()
    {
        _notifier
            .NotifyAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("hub unavailable")));

        var clientUser = MakeClientUser();
        var clientProfile = MakeClientProfile(clientUser);
        var profProfile = MakeProfessionalProfile(MakeProfessionalUser());
        var link = MakeLink(profProfile, clientProfile);
        var diaryReq = MakeDiaryRequest(link, PhotoDiaryStatus.InProgress);

        var db = new MockDbBuilder()
            .With(clientUser)
            .With(clientProfile)
            .With(link)
            .With(diaryReq)
            .Build();

        var ep = Factory.Create<SubmitRequestEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(_clientId, AppRoles.Client))),
            db, _notifier, Substitute.For<INotificationService>(), Substitute.For<INotificationEmailSender>(), _submitLogger);

        var act = () => ep.HandleAsync(new SubmitRequestRequest
        {
            Id = diaryReq.Id,
        }, TestContext.Current.CancellationToken);

        await act.Should().NotThrowAsync();
        ep.HttpContext.Response.StatusCode.Should().Be(200);
    }
}
