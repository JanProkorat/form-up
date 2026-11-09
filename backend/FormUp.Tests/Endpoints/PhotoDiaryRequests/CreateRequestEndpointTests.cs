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
using MongoDB.Driver;

namespace FormUp.Tests.Endpoints.PhotoDiaryRequests;

/// <summary>
/// Integration tests for POST /trainer/photo-diary-requests.
/// </summary>
[Collection(TestCollection.Name)]
public class CreateRequestEndpointTests(FitnessApiFactory factory)
{
    private static string UniqueEmail(string tag = "trainer") =>
        $"{Guid.NewGuid():N}@{tag}-create-diary.com";

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

    private async Task<(HttpClient Http, Guid UserId, string Email)> SetupClientAsync()
    {
        var actor = await TestActors.Client(factory)
            .WithEmail(UniqueEmail("client"))
            .CreateAsync(TestContext.Current.CancellationToken);

        return (actor.Http, actor.UserId, actor.Email);
    }

    private async Task<(long LinkId, Guid ClientPublicId)> InsertLinkAsync(
        Guid clientUserId,
        Guid professionalUserId,
        bool canViewNutritionPlans = true,
        bool canViewTrainingPlans = false,
        bool isActive = true)
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

    /// <summary>
    /// ClientId is ApplicationUser.Id (#840) — CreateRequestEndpoint resolves
    /// <c>link.ClientProfile.UserId</c> and compares it against
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
    /// Same rule as <see cref="InsertNutritionPlanAsync"/>.
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

    // ── Validation ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_BothLinkAndInvite_Returns400()
    {
        var (http, _) = await SetupProfessionalAsync();
        var response = await http.PostAsJsonAsync(
            "/trainer/photo-diary-requests",
            new { ClientId = Guid.NewGuid(), PendingInviteId = 2L, Mode = "Workflow", DurationDays = 7 },
            TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_NeitherLinkNorInvite_Returns400()
    {
        var (http, _) = await SetupProfessionalAsync();
        var response = await http.PostAsJsonAsync(
            "/trainer/photo-diary-requests",
            new { Mode = "Workflow", DurationDays = 7 },
            TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_DurationDaysZero_Returns400()
    {
        var (http, _) = await SetupProfessionalAsync();
        var response = await http.PostAsJsonAsync(
            "/trainer/photo-diary-requests",
            new { ClientId = Guid.NewGuid(), Mode = "Workflow", DurationDays = 0 },
            TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_DurationDays31_Returns400()
    {
        var (http, _) = await SetupProfessionalAsync();
        var response = await http.PostAsJsonAsync(
            "/trainer/photo-diary-requests",
            new { ClientId = Guid.NewGuid(), Mode = "Workflow", DurationDays = 31 },
            TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Ownership ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_UnknownClientId_Returns404()
    {
        var (http, _) = await SetupProfessionalAsync();

        var response = await http.PostAsJsonAsync(
            "/trainer/photo-diary-requests",
            new { ClientId = Guid.NewGuid(), Mode = "Workflow", DurationDays = 7 },
            TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Create_LinkOwnedByAnotherProfessional_Returns404()
    {
        var (http, _) = await SetupProfessionalAsync();
        var (_, otherProfId) = await SetupProfessionalAsync();
        var (_, clientUserId, _) = await SetupClientAsync();

        // Link belongs to otherProf, not http's user
        var (_, clientPublicId) = await InsertLinkAsync(clientUserId, otherProfId);

        var response = await http.PostAsJsonAsync(
            "/trainer/photo-diary-requests",
            new { ClientId = clientPublicId, Mode = "Workflow", DurationDays = 7 },
            TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Create_LinkArchived_Returns404()
    {
        var (http, profId) = await SetupProfessionalAsync();
        var (_, clientUserId, _) = await SetupClientAsync();

        // Link exists and is owned by the caller, but has been archived (IsActive = false) —
        // must collapse to the same 404 as an unknown or foreign clientId, never a distinguishable
        // error, so an archived client's public id (readable off the Archived tab) cannot be used
        // to create a new request.
        var (_, clientPublicId) = await InsertLinkAsync(clientUserId, profId, isActive: false);

        var response = await http.PostAsJsonAsync(
            "/trainer/photo-diary-requests",
            new { ClientId = clientPublicId, Mode = "Workflow", DurationDays = 7 },
            TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Create_InviteOwnedByAnotherProfessional_Returns404()
    {
        var (http, _) = await SetupProfessionalAsync();
        var (_, otherProfId) = await SetupProfessionalAsync();

        var inviteId = await InsertPendingInviteAsync(otherProfId, "someone@example.com");

        var response = await http.PostAsJsonAsync(
            "/trainer/photo-diary-requests",
            new { PendingInviteId = inviteId, Mode = "Workflow", DurationDays = 7 },
            TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Cross-domain capability on plan-scoped requests ────────────────────────
    // Residual from #916/#931-#932 review: the plan-ownership check here validated that the
    // plan belongs to the client but never checked whether the caller's own link grants the
    // capability matching the plan's domain, so a training-only professional could scope a
    // diary request to the client's nutrition plan (and vice versa).

    [Fact]
    public async Task Create_LinkLacksNutritionCapability_PlanIsNutrition_Returns404()
    {
        var (http, profId) = await SetupProfessionalAsync();
        var (_, clientUserId, _) = await SetupClientAsync();
        // Active link, but scoped to training only — no nutrition capability.
        var (_, clientPublicId) = await InsertLinkAsync(
            clientUserId, profId, canViewNutritionPlans: false, canViewTrainingPlans: true);
        var planId = await InsertNutritionPlanAsync(clientUserId, profId);

        var response = await http.PostAsJsonAsync(
            "/trainer/photo-diary-requests",
            new { ClientId = clientPublicId, PlanId = planId, Mode = "Workflow", DurationDays = 7 },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Create_LinkLacksTrainingCapability_PlanIsTraining_Returns404()
    {
        var (http, profId) = await SetupProfessionalAsync("Trainer");
        var (_, clientUserId, _) = await SetupClientAsync();
        // Active link, but scoped to nutrition only (the default) — no training capability.
        var (_, clientPublicId) = await InsertLinkAsync(clientUserId, profId);
        var planId = await InsertTrainingPlanAsync(clientUserId, profId);

        var response = await http.PostAsJsonAsync(
            "/trainer/photo-diary-requests",
            new { ClientId = clientPublicId, PlanId = planId, Mode = "Workflow", DurationDays = 7 },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Create_LinkGrantsNutritionCapability_PlanIsNutrition_Returns200()
    {
        var (http, profId) = await SetupProfessionalAsync();
        var (_, clientUserId, _) = await SetupClientAsync();
        // Active link with the matching capability — positive control for the two 404
        // cases above, proving the gate discriminates rather than denying everything.
        var (_, clientPublicId) = await InsertLinkAsync(clientUserId, profId, canViewNutritionPlans: true);
        var planId = await InsertNutritionPlanAsync(clientUserId, profId);

        var response = await http.PostAsJsonAsync(
            "/trainer/photo-diary-requests",
            new { ClientId = clientPublicId, PlanId = planId, Mode = "Workflow", DurationDays = 7 },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<CreateResponseBody>(
            JsonOptions, TestContext.Current.CancellationToken);
        body.Should().NotBeNull();
        body!.PlanId.Should().Be(planId);
    }

    [Fact]
    public async Task Create_LinkGrantsTrainingCapability_PlanIsTraining_Returns200()
    {
        var (http, profId) = await SetupProfessionalAsync("Trainer");
        var (_, clientUserId, _) = await SetupClientAsync();
        var (_, clientPublicId) = await InsertLinkAsync(
            clientUserId, profId, canViewNutritionPlans: false, canViewTrainingPlans: true);
        var planId = await InsertTrainingPlanAsync(clientUserId, profId);

        var response = await http.PostAsJsonAsync(
            "/trainer/photo-diary-requests",
            new { ClientId = clientPublicId, PlanId = planId, Mode = "Workflow", DurationDays = 7 },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<CreateResponseBody>(
            JsonOptions, TestContext.Current.CancellationToken);
        body.Should().NotBeNull();
        body!.PlanId.Should().Be(planId);
    }

    // ── Happy path — link-based ───────────────────────────────────────────────

    [Fact]
    public async Task Create_WithValidLink_Returns200_AndPersists()
    {
        var (http, profId) = await SetupProfessionalAsync();
        var (_, clientUserId, _) = await SetupClientAsync();
        var (linkId, clientPublicId) = await InsertLinkAsync(clientUserId, profId);

        var response = await http.PostAsJsonAsync(
            "/trainer/photo-diary-requests",
            new { ClientId = clientPublicId, Mode = "Workflow", DurationDays = 14 },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<CreateResponseBody>(
            JsonOptions, TestContext.Current.CancellationToken);
        body.Should().NotBeNull();
        body!.Status.Should().Be(PhotoDiaryStatus.Pending);
        body.DurationDays.Should().Be(14);
        body.LinkId.Should().Be(linkId);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var persisted = await db.PhotoDiaryRequests
            .FirstOrDefaultAsync(r => r.Id == body.Id, TestContext.Current.CancellationToken);
        persisted.Should().NotBeNull();
        persisted!.ProfessionalId.Should().Be(profId);
        persisted.Status.Should().Be(PhotoDiaryStatus.Pending);
    }

    // ── Happy path — invite-based ─────────────────────────────────────────────

    [Fact]
    public async Task Create_WithValidInvite_Returns200_AndPersists()
    {
        var (http, profId) = await SetupProfessionalAsync();
        var inviteId = await InsertPendingInviteAsync(profId, "invite-test@example.com");

        var response = await http.PostAsJsonAsync(
            "/trainer/photo-diary-requests",
            new { PendingInviteId = inviteId, Mode = "Workflow", DurationDays = 7 },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<CreateResponseBody>(
            JsonOptions, TestContext.Current.CancellationToken);
        body.Should().NotBeNull();
        body!.PendingInviteId.Should().Be(inviteId);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var persisted = await db.PhotoDiaryRequests
            .FirstOrDefaultAsync(r => r.Id == body.Id, TestContext.Current.CancellationToken);
        persisted.Should().NotBeNull();
        persisted!.LinkId.Should().BeNull();
        persisted.PendingInviteId.Should().Be(inviteId);
    }

    // ── Mode, duration and message ─────────────────────────────────────────────

    [Fact]
    public async Task Create_OnceMode_StoresOneDay_WhateverDurationIsSent()
    {
        var (http, profId) = await SetupProfessionalAsync();
        var (_, clientUserId, _) = await SetupClientAsync();
        var (_, clientPublicId) = await InsertLinkAsync(clientUserId, profId);

        var response = await http.PostAsJsonAsync(
            "/trainer/photo-diary-requests",
            new { ClientId = clientPublicId, Mode = "Bulk", DurationDays = 20, Message = "Send one photo of today" },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<CreateResponseBody>(
            JsonOptions, TestContext.Current.CancellationToken);
        body!.Mode.Should().Be(PhotoDiaryMode.Bulk);
        body.DurationDays.Should().Be(1);
        body.Message.Should().Be("Send one photo of today");
    }

    [Fact]
    public async Task Create_EveryDayMode_DefaultsToSevenDays()
    {
        var (http, profId) = await SetupProfessionalAsync();
        var (_, clientUserId, _) = await SetupClientAsync();
        var (_, clientPublicId) = await InsertLinkAsync(clientUserId, profId);

        var response = await http.PostAsJsonAsync(
            "/trainer/photo-diary-requests",
            new { ClientId = clientPublicId, Mode = "Workflow" },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<CreateResponseBody>(
            JsonOptions, TestContext.Current.CancellationToken);
        body!.Mode.Should().Be(PhotoDiaryMode.Workflow);
        body.DurationDays.Should().Be(7);
        body.Message.Should().BeNull();
    }

    [Fact]
    public async Task Create_MissingMode_Returns400()
    {
        var (http, _) = await SetupProfessionalAsync();

        var response = await http.PostAsJsonAsync(
            "/trainer/photo-diary-requests",
            new { ClientId = Guid.NewGuid() },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Plan creator check ─────────────────────────────────────────────────────

    [Fact]
    public async Task Create_LinkPath_PlanCreatedByAnotherCoach_Returns404_PlanNotOwned()
    {
        var (http, profId) = await SetupProfessionalAsync();
        var (_, otherProfId) = await SetupProfessionalAsync();
        var (_, clientUserId, _) = await SetupClientAsync();
        var (_, clientPublicId) = await InsertLinkAsync(clientUserId, profId);
        // The client's plan, but authored by a different coach.
        var planId = await InsertNutritionPlanAsync(clientUserId, otherProfId);

        var response = await http.PostAsJsonAsync(
            "/trainer/photo-diary-requests",
            new { ClientId = clientPublicId, PlanId = planId, Mode = "Workflow" },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain("PHOTO_DIARY_REQUEST_PLAN_NOT_OWNED");
    }

    [Fact]
    public async Task Create_InvitePath_PlanCreatedByAnotherCoach_Returns404_PlanNotOwned()
    {
        var (http, profId) = await SetupProfessionalAsync();
        var (_, otherProfId) = await SetupProfessionalAsync();
        var inviteId = await InsertPendingInviteAsync(profId, "plan-owner-test@example.com");
        var planId = await InsertNutritionPlanAsync(Guid.NewGuid(), otherProfId);

        var response = await http.PostAsJsonAsync(
            "/trainer/photo-diary-requests",
            new { PendingInviteId = inviteId, PlanId = planId, Mode = "Workflow" },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain("PHOTO_DIARY_REQUEST_PLAN_NOT_OWNED");
    }

    [Fact]
    public async Task Create_InvitePath_PlanCreatedByCaller_Returns200()
    {
        var (http, profId) = await SetupProfessionalAsync();
        var inviteId = await InsertPendingInviteAsync(profId, "plan-owner-ok@example.com");
        var planId = await InsertNutritionPlanAsync(Guid.NewGuid(), profId);

        var response = await http.PostAsJsonAsync(
            "/trainer/photo-diary-requests",
            new { PendingInviteId = inviteId, PlanId = planId, Mode = "Workflow" },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── One open request per coach and client ──────────────────────────────────

    [Theory]
    [InlineData(PhotoDiaryStatus.Pending)]
    [InlineData(PhotoDiaryStatus.Accepted)]
    [InlineData(PhotoDiaryStatus.InProgress)]
    public async Task Create_WhileAnOpenRequestExistsOnTheLink_Returns409_AlreadyOpen(PhotoDiaryStatus openStatus)
    {
        var (http, profId) = await SetupProfessionalAsync();
        var (_, clientUserId, _) = await SetupClientAsync();
        var (linkId, clientPublicId) = await InsertLinkAsync(clientUserId, profId);
        await InsertRequestAsync(profId, linkId: linkId, status: openStatus);

        var response = await http.PostAsJsonAsync(
            "/trainer/photo-diary-requests",
            new { ClientId = clientPublicId, Mode = "Workflow" },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain("PHOTO_DIARY_REQUEST_ALREADY_OPEN");
    }

    [Fact]
    public async Task Create_WhileAnOpenRequestExistsOnTheInvite_Returns409_AlreadyOpen()
    {
        var (http, profId) = await SetupProfessionalAsync();
        var inviteId = await InsertPendingInviteAsync(profId, "already-open@example.com");
        await InsertRequestAsync(profId, inviteId: inviteId, status: PhotoDiaryStatus.Pending);

        var response = await http.PostAsJsonAsync(
            "/trainer/photo-diary-requests",
            new { PendingInviteId = inviteId, Mode = "Bulk" },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain("PHOTO_DIARY_REQUEST_ALREADY_OPEN");
    }

    [Theory]
    [InlineData(PhotoDiaryStatus.Completed)]
    [InlineData(PhotoDiaryStatus.Dismissed)]
    [InlineData(PhotoDiaryStatus.Cancelled)]
    public async Task Create_WhenThePreviousRequestIsClosed_Returns200(PhotoDiaryStatus closedStatus)
    {
        var (http, profId) = await SetupProfessionalAsync();
        var (_, clientUserId, _) = await SetupClientAsync();
        var (linkId, clientPublicId) = await InsertLinkAsync(clientUserId, profId);
        await InsertRequestAsync(profId, linkId: linkId, status: closedStatus);

        var response = await http.PostAsJsonAsync(
            "/trainer/photo-diary-requests",
            new { ClientId = clientPublicId, Mode = "Workflow" },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Create_WhenThePreviousRequestExpired_Returns200()
    {
        var (http, profId) = await SetupProfessionalAsync();
        var (_, clientUserId, _) = await SetupClientAsync();
        var (linkId, clientPublicId) = await InsertLinkAsync(clientUserId, profId);
        // Pending, but its 7-day window ended a day ago.
        await InsertRequestAsync(
            profId, linkId: linkId, status: PhotoDiaryStatus.Pending, createdAt: DateTimeOffset.UtcNow.AddDays(-8));

        var response = await http.PostAsJsonAsync(
            "/trainer/photo-diary-requests",
            new { ClientId = clientPublicId, Mode = "Workflow" },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Create_AnotherCoachsOpenRequestForTheSameClient_DoesNotBlock()
    {
        var (http, profId) = await SetupProfessionalAsync();
        var (_, otherProfId) = await SetupProfessionalAsync();
        var (_, clientUserId, _) = await SetupClientAsync();
        var (_, clientPublicId) = await InsertLinkAsync(clientUserId, profId);
        var (otherLinkId, _) = await InsertLinkAsync(clientUserId, otherProfId);
        await InsertRequestAsync(otherProfId, linkId: otherLinkId, status: PhotoDiaryStatus.Pending);

        var response = await http.PostAsJsonAsync(
            "/trainer/photo-diary-requests",
            new { ClientId = clientPublicId, Mode = "Workflow" },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task InsertRequestAsync(
        Guid professionalId,
        long? linkId = null,
        long? inviteId = null,
        PhotoDiaryStatus status = PhotoDiaryStatus.Pending,
        DateTimeOffset? createdAt = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var created = createdAt ?? DateTimeOffset.UtcNow;
        var accepted = status is PhotoDiaryStatus.Accepted or PhotoDiaryStatus.InProgress or PhotoDiaryStatus.Completed;
        db.PhotoDiaryRequests.Add(new PhotoDiaryRequest
        {
            Id = Guid.NewGuid(),
            ProfessionalId = professionalId,
            LinkId = linkId,
            PendingInviteId = inviteId,
            Mode = PhotoDiaryMode.Workflow,
            DurationDays = 7,
            Status = status,
            AcceptedAt = accepted ? created : null,
            CompletedAt = status == PhotoDiaryStatus.Completed ? created : null,
            DismissReason = status == PhotoDiaryStatus.Dismissed ? "No thanks" : null,
            CreatedAt = created,
            UpdatedAt = created,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    // ── Response shape helper ──────────────────────────────────────────────────

    private record CreateResponseBody(
        Guid Id,
        Guid ProfessionalId,
        long? LinkId,
        long? PendingInviteId,
        Guid? PlanId,
        int DurationDays,
        PhotoDiaryMode? Mode,
        string? Message,
        PhotoDiaryStatus Status,
        DateTimeOffset CreatedAt);
}
