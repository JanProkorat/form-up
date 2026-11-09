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
/// Integration tests for GET /client/photo-diary-requests/{Id}.
/// </summary>
[Collection(TestCollection.Name)]
public class GetClientPhotoDiaryRequestEndpointTests(FitnessApiFactory factory)
{
    private static string UniqueEmail(string tag = "getbyid") =>
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
        PhotoDiaryStatus status = PhotoDiaryStatus.Pending, Guid? planId = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var request = new PhotoDiaryRequest
        {
            Id = Guid.NewGuid(),
            ProfessionalId = profUserId,
            LinkId = linkId,
            PendingInviteId = inviteId,
            PlanId = planId,
            DurationDays = 7,
            Status = status,
            Mode = status is PhotoDiaryStatus.Accepted or PhotoDiaryStatus.InProgress or PhotoDiaryStatus.Completed
                ? PhotoDiaryMode.Bulk : null,
            AcceptedAt = status is PhotoDiaryStatus.Accepted or PhotoDiaryStatus.InProgress or PhotoDiaryStatus.Completed
                ? DateTimeOffset.UtcNow : null,
            CompletedAt = status == PhotoDiaryStatus.Completed ? DateTimeOffset.UtcNow : null,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.PhotoDiaryRequests.Add(request);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return request.Id;
    }

    // ── Not found / IDOR ──────────────────────────────────────────────────────

    [Fact]
    public async Task Get_UnknownId_Returns404()
    {
        var (http, _, _) = await SetupClientAsync();
        var response = await http.GetAsync(
            $"/client/photo-diary-requests/{Guid.NewGuid()}",
            TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Get_OtherClientsRequest_Returns404()
    {
        var (_, profId) = await SetupProfessionalAsync();
        var (_, client1Id, _) = await SetupClientAsync();
        var (http, _, _) = await SetupClientAsync(); // client 2

        var linkId = await InsertLinkAsync(client1Id, profId);
        var requestId = await InsertDiaryRequestAsync(profId, linkId: linkId);

        var response = await http.GetAsync(
            $"/client/photo-diary-requests/{requestId}",
            TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Get_ViaDeactivatedLink_Returns404()
    {
        var (_, profId) = await SetupProfessionalAsync();
        var (http, clientId, _) = await SetupClientAsync();

        var linkId = await InsertLinkAsync(clientId, profId, isActive: false);
        var requestId = await InsertDiaryRequestAsync(profId, linkId: linkId);

        var response = await http.GetAsync(
            $"/client/photo-diary-requests/{requestId}",
            TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Get_InviteForDifferentEmail_Returns404()
    {
        var (_, profId) = await SetupProfessionalAsync();
        var (http, _, _) = await SetupClientAsync();
        var otherEmail = UniqueEmail("other-invite");

        var inviteId = await InsertPendingInviteAsync(profId, otherEmail);
        var requestId = await InsertDiaryRequestAsync(profId, inviteId: inviteId);

        var response = await http.GetAsync(
            $"/client/photo-diary-requests/{requestId}",
            TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Happy path ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_OwnLinkBasedRequest_Returns200_WithPlanId()
    {
        var (_, profId) = await SetupProfessionalAsync();
        var (http, clientId, _) = await SetupClientAsync();

        var linkId = await InsertLinkAsync(clientId, profId);
        var planId = Guid.NewGuid();
        var requestId = await InsertDiaryRequestAsync(profId, linkId: linkId, planId: planId);

        var response = await http.GetAsync(
            $"/client/photo-diary-requests/{requestId}",
            TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<GetResponseBody>(
            JsonOptions, TestContext.Current.CancellationToken);
        body.Should().NotBeNull();
        body!.Id.Should().Be(requestId);
        body.PlanId.Should().Be(planId);
    }

    [Fact]
    public async Task Get_OwnLinkBasedRequest_WithoutPlan_Returns200_WithNullPlanId()
    {
        var (_, profId) = await SetupProfessionalAsync();
        var (http, clientId, _) = await SetupClientAsync();

        var linkId = await InsertLinkAsync(clientId, profId);
        var requestId = await InsertDiaryRequestAsync(profId, linkId: linkId);

        var response = await http.GetAsync(
            $"/client/photo-diary-requests/{requestId}",
            TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<GetResponseBody>(
            JsonOptions, TestContext.Current.CancellationToken);
        body!.PlanId.Should().BeNull();
    }

    [Fact]
    public async Task Get_OwnInviteBasedRequest_Returns200()
    {
        var (_, profId) = await SetupProfessionalAsync();
        var (http, _, clientEmail) = await SetupClientAsync();

        var inviteId = await InsertPendingInviteAsync(profId, clientEmail);
        var requestId = await InsertDiaryRequestAsync(profId, inviteId: inviteId);

        var response = await http.GetAsync(
            $"/client/photo-diary-requests/{requestId}",
            TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<GetResponseBody>(
            JsonOptions, TestContext.Current.CancellationToken);
        body!.Id.Should().Be(requestId);
    }

    // ── Response shape helpers ─────────────────────────────────────────────────

    private record GetResponseBody(Guid Id, Guid ProfessionalId, Guid? PlanId, PhotoDiaryStatus Status);
}
