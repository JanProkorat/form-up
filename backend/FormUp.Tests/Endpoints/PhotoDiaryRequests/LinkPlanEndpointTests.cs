using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.PhotoDiaryRequests;

/// <summary>
/// Integration tests for POST /trainer/photo-diary-requests/{RequestId}/link — retroactively
/// linking an existing photo diary request to a nutrition or training plan (#778 AC5).
/// </summary>
[Collection(TestCollection.Name)]
public class LinkPlanEndpointTests(FitnessApiFactory factory)
{
    private static string UniqueEmail(string tag = "trainer") =>
        $"{Guid.NewGuid():N}@{tag}-link-plan.com";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    // ── Setup helpers ──────────────────────────────────────────────────────────

    private async Task<(HttpClient Http, Guid UserId)> SetupProfessionalAsync(string role = "Nutritionist")
    {
        var actor = await TestActors.Professional(factory, Enum.Parse<UserRole>(role, ignoreCase: true))
            .WithEmail(UniqueEmail(role.ToLower()))
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

    private async Task<(long LinkId, Guid ClientPublicId)> InsertLinkAsync(
        Guid clientUserId,
        Guid professionalUserId,
        bool isActive = true,
        bool canViewNutritionPlans = true,
        bool canViewTrainingPlans = false)
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
            CanViewNutritionPlans = canViewNutritionPlans,
            CanViewTrainingPlans = canViewTrainingPlans,
            PublicId = Guid.NewGuid(),
            DateCreated = DateTime.UtcNow,
        };
        db.ClientProfessionalLinks.Add(link);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (link.Id, clientProfile.PublicId);
    }

    private async Task<Guid> InsertDiaryRequestAsync(Guid profUserId, long linkId, PhotoDiaryStatus status = PhotoDiaryStatus.Pending)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var request = new Application.Domain.Entities.PhotoDiaryRequest
        {
            Id = Guid.NewGuid(),
            ProfessionalId = profUserId,
            LinkId = linkId,
            DurationDays = 7,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.PhotoDiaryRequests.Add(request);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return request.Id;
    }

    /// <summary>
    /// ClientId is ApplicationUser.Id (#840), NOT ClientProfile.PublicId — LinkPlanEndpoint
    /// resolves <c>request.Link.ClientProfile.UserId</c> and compares it against
    /// <c>NutritionPlan.ClientId</c>/<c>TrainingPlan.ClientId</c> directly.
    /// </summary>
    private async Task<Guid> InsertNutritionPlanAsync(Guid clientUserId, Guid professionalId)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        var planId = Guid.NewGuid();
        await mongo.NutritionPlans.InsertOneAsync(new NutritionPlan
        {
            ExternalId = planId,
            ClientId = clientUserId,
            NutritionistId = professionalId,
            Status = NutritionPlanStatus.Active,
        }, cancellationToken: TestContext.Current.CancellationToken);
        return planId;
    }

    /// <summary>
    /// ClientId is ApplicationUser.Id (#840), NOT ClientProfile.PublicId — same rule as
    /// <see cref="InsertNutritionPlanAsync"/>.
    /// </summary>
    private async Task<Guid> InsertTrainingPlanAsync(Guid clientUserId, Guid professionalId)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        var planId = Guid.NewGuid();
        await mongo.TrainingPlans.InsertOneAsync(new TrainingPlan
        {
            ExternalId = planId,
            ClientId = clientUserId,
            TrainerId = professionalId,
            Status = TrainingPlanStatus.Active,
        }, cancellationToken: TestContext.Current.CancellationToken);
        return planId;
    }

    // ── Validation ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Link_PlanIdMissing_Returns400()
    {
        var (http, profId) = await SetupProfessionalAsync();
        var (_, clientId) = await SetupClientAsync();
        var (linkId, _) = await InsertLinkAsync(clientId, profId);
        var requestId = await InsertDiaryRequestAsync(profId, linkId);

        var response = await http.PostAsJsonAsync(
            $"/trainer/photo-diary-requests/{requestId}/link",
            new { PlanId = Guid.Empty },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── 404 — diary request not found ──────────────────────────────────────────

    [Fact]
    public async Task Link_DiaryRequestNotFound_Returns404()
    {
        var (http, _) = await SetupProfessionalAsync();

        var response = await http.PostAsJsonAsync(
            $"/trainer/photo-diary-requests/{Guid.NewGuid()}/link",
            new { PlanId = Guid.NewGuid() },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── 403 — diary request owned by a different professional ─────────────────

    [Fact]
    public async Task Link_DiaryRequestOwnedByAnotherProfessional_Returns403()
    {
        var (http, _) = await SetupProfessionalAsync();
        var (_, otherProfId) = await SetupProfessionalAsync();
        var (_, clientId) = await SetupClientAsync();
        var (linkId, _) = await InsertLinkAsync(clientId, otherProfId);
        var requestId = await InsertDiaryRequestAsync(otherProfId, linkId);
        var planId = await InsertNutritionPlanAsync(clientId, otherProfId);

        var response = await http.PostAsJsonAsync(
            $"/trainer/photo-diary-requests/{requestId}/link",
            new { PlanId = planId },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── 404 — target plan not found / not owned by the diary's client ─────────

    [Fact]
    public async Task Link_PlanDoesNotExist_Returns404()
    {
        var (http, profId) = await SetupProfessionalAsync();
        var (_, clientId) = await SetupClientAsync();
        var (linkId, _) = await InsertLinkAsync(clientId, profId);
        var requestId = await InsertDiaryRequestAsync(profId, linkId);

        var response = await http.PostAsJsonAsync(
            $"/trainer/photo-diary-requests/{requestId}/link",
            new { PlanId = Guid.NewGuid() },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Link_PlanBelongsToAnotherClient_Returns404()
    {
        var (http, profId) = await SetupProfessionalAsync();
        var (_, clientId) = await SetupClientAsync();
        var (linkId, _) = await InsertLinkAsync(clientId, profId);
        var requestId = await InsertDiaryRequestAsync(profId, linkId);

        // Plan belongs to a completely different client
        var (_, otherClientId) = await SetupClientAsync();
        await InsertLinkAsync(otherClientId, profId);
        var planId = await InsertNutritionPlanAsync(otherClientId, profId);

        var response = await http.PostAsJsonAsync(
            $"/trainer/photo-diary-requests/{requestId}/link",
            new { PlanId = planId },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── 404 — link revoked or lacking the plan's domain capability ────────────
    // Residual from #916/#931-#932 review: this endpoint traversed request.Link with no
    // IsActive check and no capability check, so a departed professional (or one whose link
    // was narrowed to the other domain) could still retroactively attach a plan to a diary
    // request they already own. Both cases must 404 identically to "plan not owned" — the
    // caller must not be able to distinguish "revoked" from "never had access" from "wrong
    // domain".

    [Fact]
    public async Task Link_LinkDeactivated_Returns404()
    {
        var (http, profId) = await SetupProfessionalAsync();
        var (_, clientId) = await SetupClientAsync();
        var (linkId, _) = await InsertLinkAsync(clientId, profId, isActive: false);
        var requestId = await InsertDiaryRequestAsync(profId, linkId);
        var planId = await InsertNutritionPlanAsync(clientId, profId);

        var response = await http.PostAsJsonAsync(
            $"/trainer/photo-diary-requests/{requestId}/link",
            new { PlanId = planId },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Link_ActiveLinkLacksNutritionCapability_Returns404()
    {
        var (http, profId) = await SetupProfessionalAsync();
        var (_, clientId) = await SetupClientAsync();
        // Active link, but scoped to training only — no nutrition capability.
        var (linkId, _) = await InsertLinkAsync(
            clientId, profId, canViewNutritionPlans: false, canViewTrainingPlans: true);
        var requestId = await InsertDiaryRequestAsync(profId, linkId);
        var planId = await InsertNutritionPlanAsync(clientId, profId);

        var response = await http.PostAsJsonAsync(
            $"/trainer/photo-diary-requests/{requestId}/link",
            new { PlanId = planId },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Link_ActiveLinkLacksTrainingCapability_Returns404()
    {
        var (http, profId) = await SetupProfessionalAsync("Trainer");
        var (_, clientId) = await SetupClientAsync();
        // Active link, but scoped to nutrition only (the default) — no training capability.
        var (linkId, _) = await InsertLinkAsync(clientId, profId);
        var requestId = await InsertDiaryRequestAsync(profId, linkId);
        var planId = await InsertTrainingPlanAsync(clientId, profId);

        var response = await http.PostAsJsonAsync(
            $"/trainer/photo-diary-requests/{requestId}/link",
            new { PlanId = planId },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Happy path — nutrition plan ────────────────────────────────────────────

    [Fact]
    public async Task Link_WithValidNutritionPlan_Returns200_AndPersists()
    {
        var (http, profId) = await SetupProfessionalAsync();
        var (_, clientId) = await SetupClientAsync();
        var (linkId, _) = await InsertLinkAsync(clientId, profId);
        var requestId = await InsertDiaryRequestAsync(profId, linkId);
        var planId = await InsertNutritionPlanAsync(clientId, profId);

        var response = await http.PostAsJsonAsync(
            $"/trainer/photo-diary-requests/{requestId}/link",
            new { PlanId = planId },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<LinkResponseBody>(
            JsonOptions, TestContext.Current.CancellationToken);
        body.Should().NotBeNull();
        body!.PlanId.Should().Be(planId);
        body.Id.Should().Be(requestId);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var persisted = await db.PhotoDiaryRequests
            .FirstOrDefaultAsync(r => r.Id == requestId, TestContext.Current.CancellationToken);
        persisted.Should().NotBeNull();
        persisted!.PlanId.Should().Be(planId);
    }

    // ── Happy path — training plan ─────────────────────────────────────────────

    [Fact]
    public async Task Link_WithValidTrainingPlan_Returns200_AndPersists()
    {
        var (http, profId) = await SetupProfessionalAsync("Trainer");
        var (_, clientId) = await SetupClientAsync();
        var (linkId, _) = await InsertLinkAsync(
            clientId, profId, canViewNutritionPlans: false, canViewTrainingPlans: true);
        var requestId = await InsertDiaryRequestAsync(profId, linkId);
        var planId = await InsertTrainingPlanAsync(clientId, profId);

        var response = await http.PostAsJsonAsync(
            $"/trainer/photo-diary-requests/{requestId}/link",
            new { PlanId = planId },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<LinkResponseBody>(
            JsonOptions, TestContext.Current.CancellationToken);
        body.Should().NotBeNull();
        body!.PlanId.Should().Be(planId);
    }

    // ── Response shape helper ──────────────────────────────────────────────────

    private record LinkResponseBody(
        Guid Id,
        Guid ProfessionalId,
        long? LinkId,
        long? PendingInviteId,
        Guid? PlanId,
        int DurationDays,
        PhotoDiaryStatus Status,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);
}
