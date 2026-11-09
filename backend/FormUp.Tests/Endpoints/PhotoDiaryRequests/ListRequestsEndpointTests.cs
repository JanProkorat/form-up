using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.PhotoDiaryRequests;

/// <summary>
/// Integration tests for GET /trainer/photo-diary-requests and GET /client/photo-diary-requests.
/// </summary>
[Collection(TestCollection.Name)]
public class ListRequestsEndpointTests(FitnessApiFactory factory)
{
    private static string UniqueEmail(string tag = "list") =>
        $"{Guid.NewGuid():N}@{tag}-diary.com";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    // ── Setup helpers ──────────────────────────────────────────────────────────

    private async Task<(HttpClient Http, Guid UserId)> SetupProfessionalAsync()
    {
        var actor = await TestActors.Nutritionist(factory)
            .WithEmail(UniqueEmail("prof"))
            .CreateAsync(TestContext.Current.CancellationToken);

        return (actor.Http, actor.UserId);
    }

    private async Task<(HttpClient Http, Guid UserId, string Email)> SetupClientAsync()
    {
        var actor = await TestActors.Client(factory)
            .WithEmail(UniqueEmail("client"))
            .CreateAsync(TestContext.Current.CancellationToken);

        return (actor.Http, actor.UserId, actor.Email);
    }

    private async Task<long> InsertLinkAsync(Guid clientUserId, Guid professionalUserId, bool isActive = true)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var profProfile = await db.ProfessionalProfiles
            .FirstAsync(p => p.UserId == professionalUserId, TestContext.Current.CancellationToken);
        var clientProfile = await db.ClientProfiles
            .FirstAsync(p => p.UserId == clientUserId, TestContext.Current.CancellationToken);

        var link = new ClientProfessionalLink
        {
            ClientProfileId = clientProfile.Id,
            ProfessionalProfileId = profProfile.Id,
            ProfessionalRole = UserRole.Nutritionist,
            IsActive = isActive,
            CanViewNutritionPlans = true,
            CanViewTrainingPlans = false,
            PublicId = Guid.NewGuid(),
            DateCreated = DateTime.UtcNow,
        };
        db.ClientProfessionalLinks.Add(link);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return link.Id;
    }

    private async Task<long> InsertPendingInviteAsync(Guid professionalUserId, string inviteeEmail)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var profProfile = await db.ProfessionalProfiles
            .FirstAsync(p => p.UserId == professionalUserId, TestContext.Current.CancellationToken);

        var invite = new PendingInvite
        {
            ProfessionalProfileId = profProfile.Id,
            Email = inviteeEmail,
            SentAt = DateTime.UtcNow,
            Status = PendingInviteStatus.Pending,
            PublicId = Guid.NewGuid(),
            DateCreated = DateTime.UtcNow,
        };
        db.PendingInvites.Add(invite);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return invite.Id;
    }

    private async Task<Guid> InsertDiaryRequestAsync(
        Guid profUserId, long? linkId = null, long? inviteId = null,
        PhotoDiaryStatus status = PhotoDiaryStatus.Pending,
        string? message = null,
        DateTimeOffset? createdAt = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var created = createdAt ?? DateTimeOffset.UtcNow;
        var request = new PhotoDiaryRequest
        {
            Id = Guid.NewGuid(),
            ProfessionalId = profUserId,
            LinkId = linkId,
            PendingInviteId = inviteId,
            DurationDays = 7,
            Status = status,
            Mode = PhotoDiaryMode.Workflow,
            Message = message,
            AcceptedAt = status is PhotoDiaryStatus.Accepted or PhotoDiaryStatus.InProgress or PhotoDiaryStatus.Completed
                ? DateTimeOffset.UtcNow : null,
            CompletedAt = status == PhotoDiaryStatus.Completed ? DateTimeOffset.UtcNow : null,
            CreatedAt = created,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.PhotoDiaryRequests.Add(request);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return request.Id;
    }

    // ── Trainer list ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ListTrainer_ReturnsOnlyOwnRequests()
    {
        var (http, profId) = await SetupProfessionalAsync();
        var (_, otherProfId) = await SetupProfessionalAsync();
        var (_, clientUserId, _) = await SetupClientAsync();

        var linkId = await InsertLinkAsync(clientUserId, profId);
        var otherLinkId = await InsertLinkAsync(clientUserId, otherProfId);

        var myRequestId = await InsertDiaryRequestAsync(profId, linkId: linkId);
        await InsertDiaryRequestAsync(otherProfId, linkId: otherLinkId);

        var response = await http.GetAsync(
            "/trainer/photo-diary-requests",
            TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<ListResponseBody>(
            JsonOptions, TestContext.Current.CancellationToken);
        body.Should().NotBeNull();
        body!.Items.Should().Contain(i => i.Id == myRequestId);
        body.Items.Should().NotContain(i => i.ProfessionalId == otherProfId);
    }

    [Fact]
    public async Task ListTrainer_StatusFilter_Works()
    {
        var (http, profId) = await SetupProfessionalAsync();
        var (_, clientUserId, _) = await SetupClientAsync();

        var linkId = await InsertLinkAsync(clientUserId, profId);

        await InsertDiaryRequestAsync(profId, linkId: linkId, status: PhotoDiaryStatus.Pending);
        await InsertDiaryRequestAsync(profId, linkId: linkId, status: PhotoDiaryStatus.Accepted);

        var response = await http.GetAsync(
            "/trainer/photo-diary-requests?status=1", // Pending = 1
            TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<ListResponseBody>(
            JsonOptions, TestContext.Current.CancellationToken);
        body!.Items.Should().OnlyContain(i => i.Status == PhotoDiaryStatus.Pending);
    }

    [Fact]
    public async Task ListTrainer_XTotalCountHeader_IsSet()
    {
        var (http, profId) = await SetupProfessionalAsync();
        var (_, clientUserId, _) = await SetupClientAsync();
        var linkId = await InsertLinkAsync(clientUserId, profId);

        await InsertDiaryRequestAsync(profId, linkId: linkId);
        await InsertDiaryRequestAsync(profId, linkId: linkId);

        var response = await http.GetAsync(
            "/trainer/photo-diary-requests?pageSize=1",
            TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains("X-Total-Count").Should().BeTrue();
        var total = int.Parse(response.Headers.GetValues("X-Total-Count").First());
        total.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task ListTrainer_ClientIdFilter_ReturnsOnlyThatClientsRequests()
    {
        var (http, profId) = await SetupProfessionalAsync();
        var (_, clientUserId, _) = await SetupClientAsync();
        var (_, otherClientUserId, _) = await SetupClientAsync();

        var linkId = await InsertLinkAsync(clientUserId, profId);
        var otherLinkId = await InsertLinkAsync(otherClientUserId, profId);
        var clientPublicId = await GetClientPublicIdAsync(clientUserId);

        var wantedId = await InsertDiaryRequestAsync(profId, linkId: linkId);
        var otherClientsId = await InsertDiaryRequestAsync(profId, linkId: otherLinkId);

        var response = await http.GetAsync(
            $"/trainer/photo-diary-requests?clientId={clientPublicId}",
            TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<ListResponseBody>(
            JsonOptions, TestContext.Current.CancellationToken);
        body!.Items.Should().ContainSingle().Which.Id.Should().Be(wantedId);
        body.Items.Should().NotContain(i => i.Id == otherClientsId);
    }

    [Fact]
    public async Task ListTrainer_ClientIdFilter_NeverReturnsAnotherCoachsRequests()
    {
        var (http, _) = await SetupProfessionalAsync();
        var (_, otherProfId) = await SetupProfessionalAsync();
        var (_, clientUserId, _) = await SetupClientAsync();

        var otherLinkId = await InsertLinkAsync(clientUserId, otherProfId);
        var clientPublicId = await GetClientPublicIdAsync(clientUserId);
        await InsertDiaryRequestAsync(otherProfId, linkId: otherLinkId);

        var response = await http.GetAsync(
            $"/trainer/photo-diary-requests?clientId={clientPublicId}",
            TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<ListResponseBody>(
            JsonOptions, TestContext.Current.CancellationToken);
        body!.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task ListTrainer_SummaryCarriesMessageModeEndDateAndIsOpen()
    {
        var (http, profId) = await SetupProfessionalAsync();
        var (_, clientUserId, _) = await SetupClientAsync();
        var linkId = await InsertLinkAsync(clientUserId, profId);
        var clientPublicId = await GetClientPublicIdAsync(clientUserId);

        var createdAt = DateTimeOffset.UtcNow.AddDays(-1);
        var openId = await InsertDiaryRequestAsync(profId, linkId: linkId, message: "Hello", createdAt: createdAt);
        var expiredId = await InsertDiaryRequestAsync(
            profId, linkId: linkId, createdAt: DateTimeOffset.UtcNow.AddDays(-9));
        var cancelledId = await InsertDiaryRequestAsync(profId, linkId: linkId, status: PhotoDiaryStatus.Cancelled);

        var response = await http.GetAsync(
            $"/trainer/photo-diary-requests?clientId={clientPublicId}",
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<ListResponseBody>(
            JsonOptions, TestContext.Current.CancellationToken);

        var open = body!.Items.Single(i => i.Id == openId);
        open.Message.Should().Be("Hello");
        open.Mode.Should().Be(PhotoDiaryMode.Workflow);
        open.DurationDays.Should().Be(7);
        open.IsOpen.Should().BeTrue();
        open.EndsAt.Should().BeCloseTo(createdAt.AddDays(7), TimeSpan.FromSeconds(1));

        body.Items.Single(i => i.Id == expiredId).IsOpen.Should().BeFalse("its window ended");
        var cancelled = body.Items.Single(i => i.Id == cancelledId);
        cancelled.IsOpen.Should().BeFalse("cancelled requests are closed");
        cancelled.Status.Should().Be(PhotoDiaryStatus.Cancelled);
    }

    [Fact]
    public async Task ListTrainer_OldOpenBulkRequest_HasNoEndDate_AndIsStillOpen()
    {
        var (http, profId) = await SetupProfessionalAsync();
        var (_, clientUserId, _) = await SetupClientAsync();
        var linkId = await InsertLinkAsync(clientUserId, profId);
        var clientPublicId = await GetClientPublicIdAsync(clientUserId);

        var bulk = EntityBuilder.PhotoDiaryRequest
            .OnLink(profId, linkId)
            .WithMode(PhotoDiaryMode.Bulk)
            .WithStatus(PhotoDiaryStatus.Accepted)
            .CreatedAt(DateTimeOffset.UtcNow.AddDays(-5))
            .Build();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.PhotoDiaryRequests.Add(bulk);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var response = await http.GetAsync(
            $"/trainer/photo-diary-requests?clientId={clientPublicId}",
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadFromJsonAsync<ListResponseBody>(
            JsonOptions, TestContext.Current.CancellationToken);

        var item = body!.Items.Single(i => i.Id == bulk.Id);
        item.EndsAt.Should().BeNull("a one-time request has no end date");
        item.IsOpen.Should().BeTrue("a one-time request stays open until it is answered or cancelled");
    }

    private async Task<Guid> GetClientPublicIdAsync(Guid clientUserId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.ClientProfiles
            .Where(p => p.UserId == clientUserId)
            .Select(p => p.PublicId)
            .FirstAsync(TestContext.Current.CancellationToken);
    }

    // ── Client list ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListClient_ReturnsOwnRequestsViaLink()
    {
        var (_, profId) = await SetupProfessionalAsync();
        var (http, clientUserId, _) = await SetupClientAsync();
        var (_, otherClientId, _) = await SetupClientAsync();

        var myLinkId = await InsertLinkAsync(clientUserId, profId);
        var otherLinkId = await InsertLinkAsync(otherClientId, profId);

        var myRequestId = await InsertDiaryRequestAsync(profId, linkId: myLinkId);
        var otherRequestId = await InsertDiaryRequestAsync(profId, linkId: otherLinkId);

        var response = await http.GetAsync(
            "/client/photo-diary-requests",
            TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<ListResponseBody>(
            JsonOptions, TestContext.Current.CancellationToken);
        body!.Items.Should().Contain(i => i.Id == myRequestId);
        body.Items.Should().NotContain(i => i.Id == otherRequestId);
    }

    [Fact]
    public async Task ListClient_ViaDeactivatedLink_NotReturnedAndExcludedFromTotalCount()
    {
        var (_, profId) = await SetupProfessionalAsync();
        var (http, clientUserId, _) = await SetupClientAsync();

        var deactivatedLinkId = await InsertLinkAsync(clientUserId, profId, isActive: false);
        var requestId = await InsertDiaryRequestAsync(profId, linkId: deactivatedLinkId);

        var response = await http.GetAsync(
            "/client/photo-diary-requests",
            TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var total = int.Parse(response.Headers.GetValues("X-Total-Count").First());
        total.Should().Be(0);

        var body = await response.Content.ReadFromJsonAsync<ListResponseBody>(
            JsonOptions, TestContext.Current.CancellationToken);
        body!.Items.Should().NotContain(i => i.Id == requestId);
    }

    [Fact]
    public async Task ListClient_DeactivatedLinkWithMatchingInvite_OnlyInviteRoutedRequestReturned()
    {
        var (_, profId) = await SetupProfessionalAsync();
        var (http, clientUserId, clientEmail) = await SetupClientAsync();

        var deactivatedLinkId = await InsertLinkAsync(clientUserId, profId, isActive: false);
        var inviteId = await InsertPendingInviteAsync(profId, clientEmail);

        var linkRoutedRequestId = await InsertDiaryRequestAsync(profId, linkId: deactivatedLinkId);
        var inviteRoutedRequestId = await InsertDiaryRequestAsync(profId, inviteId: inviteId);

        var response = await http.GetAsync(
            "/client/photo-diary-requests",
            TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<ListResponseBody>(
            JsonOptions, TestContext.Current.CancellationToken);
        body!.Items.Should().Contain(i => i.Id == inviteRoutedRequestId);
        body.Items.Should().NotContain(i => i.Id == linkRoutedRequestId);
    }

    [Fact]
    public async Task ListClient_ReturnsOwnRequestsViaInvite()
    {
        var (_, profId) = await SetupProfessionalAsync();
        var (http, _, clientEmail) = await SetupClientAsync();
        var otherEmail = UniqueEmail("other-invite");

        var myInviteId = await InsertPendingInviteAsync(profId, clientEmail);
        var otherInviteId = await InsertPendingInviteAsync(profId, otherEmail);

        var myRequestId = await InsertDiaryRequestAsync(profId, inviteId: myInviteId);
        var otherRequestId = await InsertDiaryRequestAsync(profId, inviteId: otherInviteId);

        var response = await http.GetAsync(
            "/client/photo-diary-requests",
            TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<ListResponseBody>(
            JsonOptions, TestContext.Current.CancellationToken);
        body!.Items.Should().Contain(i => i.Id == myRequestId);
        body.Items.Should().NotContain(i => i.Id == otherRequestId);
    }

    // ── Response shape helpers ─────────────────────────────────────────────────

    private record ListItem(
        Guid Id,
        Guid ProfessionalId,
        PhotoDiaryStatus Status,
        PhotoDiaryMode? Mode = null,
        string? Message = null,
        int DurationDays = 0,
        DateTimeOffset? EndsAt = null,
        bool IsOpen = false);
    private record ListResponseBody(List<ListItem> Items);
}
