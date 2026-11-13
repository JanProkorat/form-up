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
/// Integration tests for POST /client/photo-diary-requests/{id}/dismiss.
/// </summary>
[Collection(TestCollection.Name)]
public class DismissRequestEndpointTests(FitnessApiFactory factory)
{
    private static string UniqueEmail(string tag = "dismiss") =>
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

    private async Task<(HttpClient Http, Guid UserId)> SetupClientAsync()
    {
        var actor = await TestActors.Client(factory)
            .WithEmail(UniqueEmail("client"))
            .CreateAsync(TestContext.Current.CancellationToken);

        return (actor.Http, actor.UserId);
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
            ConnectionState = isActive ? ConnectionState.Connected : ConnectionState.Ended,
            CanViewNutritionPlans = true,
            CanViewTrainingPlans = false,
            PublicId = Guid.NewGuid(),
            DateCreated = DateTime.UtcNow,
        };
        db.ClientProfessionalLinks.Add(link);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return link.Id;
    }

    private async Task<Guid> InsertDiaryRequestAsync(
        Guid profUserId, long linkId,
        PhotoDiaryStatus status = PhotoDiaryStatus.Pending)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var request = new PhotoDiaryRequest
        {
            Id = Guid.NewGuid(),
            ProfessionalId = profUserId,
            LinkId = linkId,
            DurationDays = 7,
            Status = status,
            Mode = status is PhotoDiaryStatus.Accepted or PhotoDiaryStatus.InProgress or PhotoDiaryStatus.Completed
                ? PhotoDiaryMode.Bulk : null,
            AcceptedAt = status is PhotoDiaryStatus.Accepted or PhotoDiaryStatus.InProgress or PhotoDiaryStatus.Completed
                ? DateTimeOffset.UtcNow : null,
            CompletedAt = status == PhotoDiaryStatus.Completed ? DateTimeOffset.UtcNow : null,
            DismissReason = status == PhotoDiaryStatus.Dismissed ? "pre-dismissed" : null,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.PhotoDiaryRequests.Add(request);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return request.Id;
    }

    // ── Auth ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Dismiss_Unauthenticated_Returns401()
    {
        var http = factory.CreateClient();
        var response = await http.PostAsJsonAsync(
            $"/client/photo-diary-requests/{Guid.NewGuid()}/dismiss",
            new { },
            TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── Not found / IDOR ──────────────────────────────────────────────────────

    [Fact]
    public async Task Dismiss_UnknownId_Returns404()
    {
        var (http, _) = await SetupClientAsync();
        var response = await http.PostAsJsonAsync(
            $"/client/photo-diary-requests/{Guid.NewGuid()}/dismiss",
            new { },
            TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Dismiss_OtherClientsRequest_Returns404()
    {
        var (_, profId) = await SetupProfessionalAsync();
        var (_, client1Id) = await SetupClientAsync();
        var (http, _) = await SetupClientAsync();

        var linkId = await InsertLinkAsync(client1Id, profId);
        var requestId = await InsertDiaryRequestAsync(profId, linkId);

        var response = await http.PostAsJsonAsync(
            $"/client/photo-diary-requests/{requestId}/dismiss",
            new { },
            TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Dismiss_ViaDeactivatedLink_Returns404()
    {
        var (_, profId) = await SetupProfessionalAsync();
        var (http, clientId) = await SetupClientAsync();

        var linkId = await InsertLinkAsync(clientId, profId, isActive: false);
        var requestId = await InsertDiaryRequestAsync(profId, linkId);

        var response = await http.PostAsJsonAsync(
            $"/client/photo-diary-requests/{requestId}/dismiss",
            new { },
            TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Dismiss_ViaDeactivatedLinkAlreadyAccepted_Returns404_NotConflict()
    {
        // Ordering guard: ownership must be checked BEFORE the status check, otherwise a
        // 409 on a non-transitionable status would leak the request's existence to a
        // client whose link has since been deactivated.
        var (_, profId) = await SetupProfessionalAsync();
        var (http, clientId) = await SetupClientAsync();

        var linkId = await InsertLinkAsync(clientId, profId, isActive: false);
        var requestId = await InsertDiaryRequestAsync(profId, linkId, PhotoDiaryStatus.Accepted);

        var response = await http.PostAsJsonAsync(
            $"/client/photo-diary-requests/{requestId}/dismiss",
            new { },
            TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Happy path ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Dismiss_Pending_Returns200_AndTransitions()
    {
        var (_, profId) = await SetupProfessionalAsync();
        var (http, clientId) = await SetupClientAsync();

        var linkId = await InsertLinkAsync(clientId, profId);
        var requestId = await InsertDiaryRequestAsync(profId, linkId);

        var response = await http.PostAsJsonAsync(
            $"/client/photo-diary-requests/{requestId}/dismiss",
            new { },
            TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<DismissResponseBody>(
            JsonOptions, TestContext.Current.CancellationToken);
        body!.Status.Should().Be(PhotoDiaryStatus.Dismissed);
        body.DismissReason.Should().BeNull();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var persisted = await db.PhotoDiaryRequests
            .FirstAsync(r => r.Id == requestId, TestContext.Current.CancellationToken);
        persisted.Status.Should().Be(PhotoDiaryStatus.Dismissed);
        persisted.DismissReason.Should().BeNull();
    }

    [Fact]
    public async Task Dismiss_WithReason_ReasonIsPersisted()
    {
        var (_, profId) = await SetupProfessionalAsync();
        var (http, clientId) = await SetupClientAsync();

        var linkId = await InsertLinkAsync(clientId, profId);
        var requestId = await InsertDiaryRequestAsync(profId, linkId);

        const string reason = "I prefer not to share photos.";

        var response = await http.PostAsJsonAsync(
            $"/client/photo-diary-requests/{requestId}/dismiss",
            new { Reason = reason },
            TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var persisted = await db.PhotoDiaryRequests
            .FirstAsync(r => r.Id == requestId, TestContext.Current.CancellationToken);
        persisted.DismissReason.Should().Be(reason);
    }

    // ── Conflict — wrong status ────────────────────────────────────────────────

    [Fact]
    public async Task Dismiss_AlreadyAccepted_Returns409()
    {
        var (_, profId) = await SetupProfessionalAsync();
        var (http, clientId) = await SetupClientAsync();

        var linkId = await InsertLinkAsync(clientId, profId);
        var requestId = await InsertDiaryRequestAsync(profId, linkId, PhotoDiaryStatus.Accepted);

        var response = await http.PostAsJsonAsync(
            $"/client/photo-diary-requests/{requestId}/dismiss",
            new { },
            TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Dismiss_AlreadyDismissed_Returns409()
    {
        var (_, profId) = await SetupProfessionalAsync();
        var (http, clientId) = await SetupClientAsync();

        var linkId = await InsertLinkAsync(clientId, profId);
        var requestId = await InsertDiaryRequestAsync(profId, linkId, PhotoDiaryStatus.Dismissed);

        var response = await http.PostAsJsonAsync(
            $"/client/photo-diary-requests/{requestId}/dismiss",
            new { },
            TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ── Mutually-exclusive guard: dismiss then accept → 409 ──────────────────

    [Fact]
    public async Task DismissThenAccept_Returns409()
    {
        var (_, profId) = await SetupProfessionalAsync();
        var (http, clientId) = await SetupClientAsync();

        var linkId = await InsertLinkAsync(clientId, profId);
        var requestId = await InsertDiaryRequestAsync(profId, linkId);

        // Dismiss first
        var dismissResponse = await http.PostAsJsonAsync(
            $"/client/photo-diary-requests/{requestId}/dismiss",
            new { Reason = "Changed my mind" },
            TestContext.Current.CancellationToken);
        dismissResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Now try to accept — must be 409 because it's no longer Pending
        var acceptResponse = await http.PostAsJsonAsync(
            $"/client/photo-diary-requests/{requestId}/accept",
            new { Mode = PhotoDiaryMode.Bulk },
            TestContext.Current.CancellationToken);
        acceptResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ── Response shape helpers ─────────────────────────────────────────────────

    private record DismissResponseBody(Guid Id, PhotoDiaryStatus Status, string? DismissReason);
}
