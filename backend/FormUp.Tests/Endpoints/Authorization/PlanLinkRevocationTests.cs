using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Builders;
using FormUp.Tests.Endpoints.TrainingPlans;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace FormUp.Tests.Endpoints.Authorization;

/// <summary>
/// Security regression tests for the plan-addressed routes, which authorized on the plan
/// document's author field (<c>NutritionistId</c> / <c>TrainerId</c>) alone. Authorship is
/// permanent; the <see cref="ClientProfessionalLink"/> is not, so deactivating the link left a
/// former professional with full read and write access for as long as the plan stayed Active.
///
/// <para>
/// Three shapes are covered per domain: a revoked link is denied on a read AND on a write; a
/// single-capability link cannot reach the other domain's plan routes; and a currently-linked
/// professional is unaffected. The last one is what stops the fix from degenerating into
/// "deny everything".
/// </para>
///
/// <para>
/// Also covers the publish-week sibling archival, whose filter matched every overlapping Active
/// plan for the client with no author predicate — so publishing archived a different
/// professional's live plan.
/// </para>
/// </summary>
[Collection(TestCollection.Name)]
public class PlanLinkRevocationTests(FitnessApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static string UniqueEmail(string tag) => $"{Guid.NewGuid():N}@plan-link-{tag}.com";

    // ── fixture helpers (mirrors CrossDomainPlanAccessTests) ─────────────────
    // #1104: actors are built directly via TestActors, bypassing the /auth/register +
    // /auth/login HTTP round trip. Signatures/return shapes are unchanged.

    private async Task<(HttpClient Http, long ProfessionalProfileId, Guid ProfessionalUserId)> RegisterProfessionalAsync(
        string tag, params string[] roles)
    {
        var actor = await TestActors.Professional(
                factory, roles.Select(r => Enum.Parse<UserRole>(r, ignoreCase: true)).ToArray())
            .WithEmail(UniqueEmail(tag))
            .CreateAsync(TestContext.Current.CancellationToken);

        return (actor.Http, actor.ProfileId, actor.UserId);
    }

    private async Task<(Guid ClientPublicId, long ClientProfileId, Guid ClientUserId)> RegisterClientAsync(string tag)
    {
        var actor = await TestActors.Client(factory)
            .WithEmail(UniqueEmail(tag))
            .CreateAsync(TestContext.Current.CancellationToken);

        return (actor.PublicId, actor.ProfileId, actor.UserId);
    }

    /// <summary>
    /// Registers a client and returns their authenticated <see cref="HttpClient"/> alongside the
    /// ids. <see cref="RegisterClientAsync"/> discards the session because most tests here drive
    /// the professional's side; the end-collaboration route is client-invoked, so it needs one.
    /// </summary>
    private async Task<(HttpClient Http, long ClientProfileId, Guid ClientUserId)> RegisterClientWithSessionAsync(
        string tag)
    {
        var actor = await TestActors.Client(factory)
            .WithEmail(UniqueEmail(tag))
            .CreateAsync(TestContext.Current.CancellationToken);

        return (actor.Http, actor.ProfileId, actor.UserId);
    }

    private async Task<Guid> LinkAsync(
        long professionalProfileId,
        long clientProfileId,
        bool canViewNutritionPlans = true,
        bool canViewTrainingPlans = true,
        bool isActive = true)
    {
        var link = TestActors.Link(factory, professionalProfileId, clientProfileId)
            .CanViewNutritionPlans(canViewNutritionPlans)
            .CanViewTrainingPlans(canViewTrainingPlans);

        if (!isActive)
        {
            link.Inactive();
        }

        await link.CreateAsync(TestContext.Current.CancellationToken);
        return link.PublicId;
    }

    /// <summary>
    /// Flips an existing link to inactive — the exact state <c>EndCollaborationEndpoint</c>
    /// leaves behind. It touches nothing else: the plan keeps Status=Active and its original
    /// author id, which is what made authorship-only authorization exploitable.
    /// </summary>
    private async Task DeactivateLinkAsync(Guid linkPublicId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var link = await db.ClientProfessionalLinks.FirstAsync(
            l => l.PublicId == linkPublicId, TestContext.Current.CancellationToken);
        link.ConnectionState = ConnectionState.Ended;

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<Guid> SeedNutritionPlanAsync(
        Guid clientUserId,
        Guid nutritionistUserId,
        NutritionPlanStatus status = NutritionPlanStatus.Active,
        DateTime? startDate = null,
        int weekCount = 4,
        WeekStatus weekStatus = WeekStatus.Draft)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        var externalId = Guid.NewGuid();

        await mongo.NutritionPlans.InsertOneAsync(new NutritionPlan
        {
            Id = ObjectId.GenerateNewId(),
            ExternalId = externalId,
            ClientId = clientUserId,
            NutritionistId = nutritionistUserId,
            Name = "Plan-link nutrition plan",
            Status = status,
            StartDate = startDate,
            Weeks = Enumerable.Range(1, weekCount)
                .Select(w => new PlanWeek { WeekNumber = w, Status = weekStatus, Days = [] })
                .ToList(),
            Version = 1,
            DateCreated = DateTime.UtcNow,
        }, cancellationToken: TestContext.Current.CancellationToken);

        return externalId;
    }

    private async Task<Guid> SeedTrainingPlanAsync(
        Guid clientUserId,
        Guid trainerUserId,
        TrainingPlanStatus status = TrainingPlanStatus.Active)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        var externalId = Guid.NewGuid();

        await mongo.TrainingPlans.InsertOneAsync(new TrainingPlan
        {
            Id = ObjectId.GenerateNewId(),
            ExternalId = externalId,
            ClientId = clientUserId,
            TrainerId = trainerUserId,
            Name = "Plan-link training plan",
            Status = status,
            StartDate = DateTime.UtcNow.Date,
            Weeks = [],
            Version = 1,
            DateCreated = DateTime.UtcNow,
        }, cancellationToken: TestContext.Current.CancellationToken);

        return externalId;
    }

    /// <summary>
    /// Seeds a training plan carrying one published week with a single real session, and returns
    /// both ids. The session must actually exist for an unlock attempt to be able to succeed —
    /// with a fabricated session id the endpoint's session-existence guard returns the same 404
    /// the plan-level guard does, and the test cannot tell which one denied it.
    /// </summary>
    private async Task<(Guid PlanId, Guid SessionId)> SeedTrainingPlanWithSessionAsync(
        Guid clientUserId, Guid trainerUserId)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        var externalId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        await mongo.TrainingPlans.InsertOneAsync(new TrainingPlan
        {
            Id = ObjectId.GenerateNewId(),
            ExternalId = externalId,
            ClientId = clientUserId,
            TrainerId = trainerUserId,
            Name = "Plan-link training plan with session",
            Status = TrainingPlanStatus.Active,
            StartDate = TrainingPlanTestHelpers.LastMonday(),
            Weeks =
            [
                new TrainingWeek
                {
                    WeekNumber = 1,
                    Status = WeekStatus.Published,
                    Days = TrainingPlanTestHelpers.MaterializeDays(
                        (1, new TrainingSession
                        {
                            SessionId = sessionId,
                            Name = "Plan-link session",
                            Order = 1,
                            Workouts = []
                        }))
                }
            ],
            Version = 1,
            DateCreated = DateTime.UtcNow,
        }, cancellationToken: TestContext.Current.CancellationToken);

        return (externalId, sessionId);
    }

    private async Task<NutritionPlan> ReadNutritionPlanAsync(Guid planExternalId)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        return await mongo.NutritionPlans
            .Find(Builders<NutritionPlan>.Filter.Eq(p => p.ExternalId, planExternalId))
            .FirstAsync(TestContext.Current.CancellationToken);
    }

    private async Task<TrainingPlanStatus> ReadTrainingPlanStatusAsync(Guid planExternalId)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        var plan = await mongo.TrainingPlans
            .Find(Builders<TrainingPlan>.Filter.Eq(p => p.ExternalId, planExternalId))
            .FirstAsync(TestContext.Current.CancellationToken);

        return plan.Status;
    }

    private async Task<NutritionPlanStatus> ReadNutritionPlanStatusAsync(Guid planExternalId)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        var plan = await mongo.NutritionPlans
            .Find(Builders<NutritionPlan>.Filter.Eq(p => p.ExternalId, planExternalId))
            .FirstAsync(TestContext.Current.CancellationToken);

        return plan.Status;
    }

    // ── nutrition: revoked link denied on read and on write ──────────────────

    [Fact]
    public async Task RevokedLink_GetNutritionPlan_Returns404()
    {
        var (professional, professionalProfileId, professionalUserId) = await RegisterProfessionalAsync(
            "revoked-nut-read", "Nutritionist");
        var (_, clientProfileId, clientUserId) = await RegisterClientAsync("revoked-nut-read");
        var linkPublicId = await LinkAsync(professionalProfileId, clientProfileId);

        var planId = await SeedNutritionPlanAsync(clientUserId, professionalUserId);

        var beforeRevocation = await professional.GetAsync($"/nutrition/plans/{planId}");
        beforeRevocation.StatusCode.Should().Be(
            HttpStatusCode.OK, "the professional is still linked at this point");

        await DeactivateLinkAsync(linkPublicId);

        var afterRevocation = await professional.GetAsync($"/nutrition/plans/{planId}");
        afterRevocation.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "ending the collaboration must end read access even though plan.NutritionistId still names the caller");
    }

    [Fact]
    public async Task RevokedLink_CompleteNutritionPlan_Returns404_AndDoesNotMutate()
    {
        var (professional, professionalProfileId, professionalUserId) = await RegisterProfessionalAsync(
            "revoked-nut-write", "Nutritionist");
        var (_, clientProfileId, clientUserId) = await RegisterClientAsync("revoked-nut-write");
        var linkPublicId = await LinkAsync(professionalProfileId, clientProfileId);

        var planId = await SeedNutritionPlanAsync(clientUserId, professionalUserId);
        await DeactivateLinkAsync(linkPublicId);

        var response = await professional.PostAsJsonAsync(
            $"/nutrition/plans/{planId}/complete", new { Version = 1 });

        response.StatusCode.Should().Be(
            HttpStatusCode.NotFound, "ending the collaboration must end write access too");

        var status = await ReadNutritionPlanStatusAsync(planId);
        status.Should().Be(
            NutritionPlanStatus.Active, "the denied write must not have reached the document");
    }

    [Fact]
    public async Task RevokedLink_DeleteNutritionPlan_Returns404_AndDoesNotArchive()
    {
        var (professional, professionalProfileId, professionalUserId) = await RegisterProfessionalAsync(
            "revoked-nut-delete", "Nutritionist");
        var (_, clientProfileId, clientUserId) = await RegisterClientAsync("revoked-nut-delete");
        var linkPublicId = await LinkAsync(professionalProfileId, clientProfileId);

        var planId = await SeedNutritionPlanAsync(clientUserId, professionalUserId);
        await DeactivateLinkAsync(linkPublicId);

        var response = await professional.DeleteAsync($"/nutrition/plans/{planId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var status = await ReadNutritionPlanStatusAsync(planId);
        status.Should().Be(NutritionPlanStatus.Active, "the denied delete must not have archived the plan");
    }

    [Fact]
    public async Task RevokedLink_ListNutritionPlans_OmitsThePlan()
    {
        var (professional, professionalProfileId, professionalUserId) = await RegisterProfessionalAsync(
            "revoked-nut-list", "Nutritionist");
        var (_, clientProfileId, clientUserId) = await RegisterClientAsync("revoked-nut-list");
        var linkPublicId = await LinkAsync(professionalProfileId, clientProfileId);

        var planId = await SeedNutritionPlanAsync(clientUserId, professionalUserId);

        var before = await professional.GetAsync("/nutrition/plans?page=1&pageSize=100");
        var beforeBody = await before.Content.ReadFromJsonAsync<PlanListResponse>(
            JsonOptions, TestContext.Current.CancellationToken);
        beforeBody!.Plans.Should().Contain(p => p.PlanId == planId);

        await DeactivateLinkAsync(linkPublicId);

        var after = await professional.GetAsync("/nutrition/plans?page=1&pageSize=100");
        var afterBody = await after.Content.ReadFromJsonAsync<PlanListResponse>(
            JsonOptions, TestContext.Current.CancellationToken);

        afterBody!.Plans.Should().NotContain(
            p => p.PlanId == planId,
            "the list route re-served the ex-client's plan id on demand, which is how an attacker rediscovers it");
    }

    // ── training: revoked link denied on read and on write ───────────────────

    [Fact]
    public async Task RevokedLink_GetTrainingPlan_Returns404()
    {
        var (professional, professionalProfileId, professionalUserId) = await RegisterProfessionalAsync(
            "revoked-trn-read", "Trainer");
        var (_, clientProfileId, clientUserId) = await RegisterClientAsync("revoked-trn-read");
        var linkPublicId = await LinkAsync(professionalProfileId, clientProfileId);

        var planId = await SeedTrainingPlanAsync(clientUserId, professionalUserId);

        var beforeRevocation = await professional.GetAsync($"/training/plans/{planId}");
        beforeRevocation.StatusCode.Should().Be(HttpStatusCode.OK, "the trainer is still linked at this point");

        await DeactivateLinkAsync(linkPublicId);

        var afterRevocation = await professional.GetAsync($"/training/plans/{planId}");
        afterRevocation.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "ending the collaboration must end read access even though plan.TrainerId still names the caller");
    }

    [Fact]
    public async Task RevokedLink_CompleteTrainingPlan_Returns404_AndDoesNotMutate()
    {
        var (professional, professionalProfileId, professionalUserId) = await RegisterProfessionalAsync(
            "revoked-trn-write", "Trainer");
        var (_, clientProfileId, clientUserId) = await RegisterClientAsync("revoked-trn-write");
        var linkPublicId = await LinkAsync(professionalProfileId, clientProfileId);

        var planId = await SeedTrainingPlanAsync(clientUserId, professionalUserId);
        await DeactivateLinkAsync(linkPublicId);

        var response = await professional.PostAsJsonAsync(
            $"/training/plans/{planId}/complete", new { Version = 1 });

        response.StatusCode.Should().Be(
            HttpStatusCode.NotFound, "ending the collaboration must end write access too");

        var status = await ReadTrainingPlanStatusAsync(planId);
        status.Should().Be(
            TrainingPlanStatus.Active, "the denied write must not have reached the document");
    }

    /// <summary>
    /// A revoked caller must not be able to tell a version conflict from a missing plan. The
    /// version-guarded routes fetch by ExternalId + author, and authorship is permanent — so if
    /// the link check ran after the guard's version comparison, a stale version would come back
    /// 409 ("exists, and you wrote it") while a fabricated plan id came back 404. Probing versions
    /// would then also reveal how often the replacement professional is editing the plan.
    /// </summary>
    [Fact]
    public async Task RevokedLink_CompleteWithStaleVersion_Returns404_NotAVersionConflict()
    {
        var (professional, professionalProfileId, professionalUserId) = await RegisterProfessionalAsync(
            "revoked-nut-oracle", "Nutritionist");
        var (_, clientProfileId, clientUserId) = await RegisterClientAsync("revoked-nut-oracle");
        var linkPublicId = await LinkAsync(professionalProfileId, clientProfileId);

        var planId = await SeedNutritionPlanAsync(clientUserId, professionalUserId);
        await DeactivateLinkAsync(linkPublicId);

        // Version 999 is deliberately wrong — the seeded plan is at version 1.
        var staleVersion = await professional.PostAsJsonAsync(
            $"/nutrition/plans/{planId}/complete", new { Version = 999 });

        staleVersion.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "authorization must be decided before the version comparison, or the 409/404 split " +
            "becomes an existence oracle");

        var fabricatedPlan = await professional.PostAsJsonAsync(
            $"/nutrition/plans/{Guid.NewGuid()}/complete", new { Version = 999 });

        fabricatedPlan.StatusCode.Should().Be(
            staleVersion.StatusCode,
            "a plan the caller lost access to must be indistinguishable from one that never existed");
    }

    [Fact]
    public async Task RevokedLink_UnlockTrainingSession_Returns404()
    {
        var (professional, professionalProfileId, professionalUserId) = await RegisterProfessionalAsync(
            "revoked-trn-unlock", "Trainer");
        var (_, clientProfileId, clientUserId) = await RegisterClientAsync("revoked-trn-unlock");
        var linkPublicId = await LinkAsync(professionalProfileId, clientProfileId);

        var (planId, sessionId) = await SeedTrainingPlanWithSessionAsync(clientUserId, professionalUserId);

        var beforeRevocation = await professional.PostAsJsonAsync(
            $"/training/plans/{planId}/sessions/{sessionId}/unlock", new { });
        beforeRevocation.StatusCode.Should().Be(
            HttpStatusCode.NoContent, "the professional is still linked at this point");

        await DeactivateLinkAsync(linkPublicId);

        var afterRevocation = await professional.PostAsJsonAsync(
            $"/training/plans/{planId}/sessions/{sessionId}/unlock", new { });
        afterRevocation.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "ending the collaboration must deny the unlock even though the session exists and " +
            "plan.TrainerId still names the caller");
    }

    [Fact]
    public async Task RevokedLink_ListTrainingPlans_OmitsThePlan()
    {
        var (professional, professionalProfileId, professionalUserId) = await RegisterProfessionalAsync(
            "revoked-trn-list", "Trainer");
        var (_, clientProfileId, clientUserId) = await RegisterClientAsync("revoked-trn-list");
        var linkPublicId = await LinkAsync(professionalProfileId, clientProfileId);

        var planId = await SeedTrainingPlanAsync(clientUserId, professionalUserId);

        var before = await professional.GetAsync("/training/plans?page=1&pageSize=100");
        var beforeBody = await before.Content.ReadFromJsonAsync<PlanListResponse>(
            JsonOptions, TestContext.Current.CancellationToken);
        beforeBody!.Plans.Should().Contain(p => p.PlanId == planId);

        await DeactivateLinkAsync(linkPublicId);

        var after = await professional.GetAsync("/training/plans?page=1&pageSize=100");
        var afterBody = await after.Content.ReadFromJsonAsync<PlanListResponse>(
            JsonOptions, TestContext.Current.CancellationToken);

        afterBody!.Plans.Should().NotContain(p => p.PlanId == planId);
    }

    [Fact]
    public async Task RevokedLink_PublishWeek_Returns404_AndDoesNotPublish()
    {
        var (professional, professionalProfileId, professionalUserId) = await RegisterProfessionalAsync(
            "revoked-nut-publish", "Nutritionist");
        var (_, clientProfileId, clientUserId) = await RegisterClientAsync("revoked-nut-publish");
        var linkPublicId = await LinkAsync(professionalProfileId, clientProfileId);

        var today = DateTime.SpecifyKind(DateTime.UtcNow.Date, DateTimeKind.Utc);
        var planId = await SeedNutritionPlanAsync(
            clientUserId, professionalUserId, NutritionPlanStatus.Draft, today);

        await DeactivateLinkAsync(linkPublicId);

        var response = await professional.PostAsJsonAsync(
            $"/nutrition/plans/{planId}/weeks/1/publish", new { Version = 1 });

        response.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "publishing pushes content into the client's app with a notification and a realtime event");

        var status = await ReadNutritionPlanStatusAsync(planId);
        status.Should().Be(
            NutritionPlanStatus.Draft, "the denied publish must not have flipped the plan to Active");
    }

    // ── per-domain: one capability flag never reaches the other domain ───────

    [Fact]
    public async Task NutritionOnlyLink_CannotReachTrainingPlanRoute()
    {
        var (professional, professionalProfileId, professionalUserId) = await RegisterProfessionalAsync(
            "nut-only-cross", "Trainer", "Nutritionist");
        var (_, clientProfileId, clientUserId) = await RegisterClientAsync("nut-only-cross");
        await LinkAsync(professionalProfileId, clientProfileId,
            canViewNutritionPlans: true, canViewTrainingPlans: false);

        var nutritionPlanId = await SeedNutritionPlanAsync(clientUserId, professionalUserId);
        var trainingPlanId = await SeedTrainingPlanAsync(clientUserId, professionalUserId);

        var nutritionResponse = await professional.GetAsync($"/nutrition/plans/{nutritionPlanId}");
        nutritionResponse.StatusCode.Should().Be(
            HttpStatusCode.OK, "the link grants the nutrition capability");

        var trainingResponse = await professional.GetAsync($"/training/plans/{trainingPlanId}");
        trainingResponse.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "authoring a training plan does not survive a link that never granted training access");
    }

    [Fact]
    public async Task TrainingOnlyLink_CannotReachNutritionPlanRoute()
    {
        var (professional, professionalProfileId, professionalUserId) = await RegisterProfessionalAsync(
            "trn-only-cross", "Trainer", "Nutritionist");
        var (_, clientProfileId, clientUserId) = await RegisterClientAsync("trn-only-cross");
        await LinkAsync(professionalProfileId, clientProfileId,
            canViewNutritionPlans: false, canViewTrainingPlans: true);

        var nutritionPlanId = await SeedNutritionPlanAsync(clientUserId, professionalUserId);
        var trainingPlanId = await SeedTrainingPlanAsync(clientUserId, professionalUserId);

        var trainingResponse = await professional.GetAsync($"/training/plans/{trainingPlanId}");
        trainingResponse.StatusCode.Should().Be(
            HttpStatusCode.OK, "the link grants the training capability");

        var nutritionResponse = await professional.GetAsync($"/nutrition/plans/{nutritionPlanId}");
        nutritionResponse.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "authoring a nutrition plan does not survive a link that never granted nutrition access");
    }

    // ── positive control: a live link is unaffected ──────────────────────────

    [Fact]
    public async Task ActiveLink_CompleteNutritionPlan_Succeeds()
    {
        var (professional, professionalProfileId, professionalUserId) = await RegisterProfessionalAsync(
            "active-nut-write", "Nutritionist");
        var (_, clientProfileId, clientUserId) = await RegisterClientAsync("active-nut-write");
        await LinkAsync(professionalProfileId, clientProfileId);

        var planId = await SeedNutritionPlanAsync(clientUserId, professionalUserId);

        var response = await professional.PostAsJsonAsync(
            $"/nutrition/plans/{planId}/complete", new { Version = 1 });

        response.StatusCode.Should().Be(
            HttpStatusCode.OK, "a currently-linked nutritionist must still be able to complete their plan");

        var status = await ReadNutritionPlanStatusAsync(planId);
        status.Should().Be(NutritionPlanStatus.Completed);
    }

    [Fact]
    public async Task ActiveLink_GetTrainingPlan_Succeeds()
    {
        var (professional, professionalProfileId, professionalUserId) = await RegisterProfessionalAsync(
            "active-trn-read", "Trainer");
        var (_, clientProfileId, clientUserId) = await RegisterClientAsync("active-trn-read");
        await LinkAsync(professionalProfileId, clientProfileId);

        var planId = await SeedTrainingPlanAsync(clientUserId, professionalUserId);

        var response = await professional.GetAsync($"/training/plans/{planId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── "save the client's plan into my library" routes ──────────────────────
    // Four routes copy a client's plan content into a permanent, caller-owned template. They are
    // the same threat as a plain read — the copy outlives the collaboration and, with a non-private
    // Visibility, becomes readable by other professionals. Two of the four deny through the
    // sharing-library problem body rather than a bodiless 404, so they are also the denial shape
    // most likely to drift.

    [Fact]
    public async Task RevokedLink_CreateNutritionTemplateFromPlan_IsDenied()
    {
        var (professional, professionalProfileId, professionalUserId) = await RegisterProfessionalAsync(
            "revoked-nut-template", "Nutritionist");
        var (_, clientProfileId, clientUserId) = await RegisterClientAsync("revoked-nut-template");
        var linkPublicId = await LinkAsync(professionalProfileId, clientProfileId);

        var planId = await SeedNutritionPlanAsync(clientUserId, professionalUserId);

        var beforeRevocation = await professional.PostAsJsonAsync(
            "/nutrition/plan-templates/from-plan",
            new { PlanId = planId, Name = "Before revocation" });
        beforeRevocation.StatusCode.Should().Be(
            HttpStatusCode.Created, "the professional is still linked at this point");

        await DeactivateLinkAsync(linkPublicId);

        var afterRevocation = await professional.PostAsJsonAsync(
            "/nutrition/plan-templates/from-plan",
            new { PlanId = planId, Name = "After revocation" });

        afterRevocation.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "a revoked professional must not be able to copy the client's plan into a permanent " +
            "template they keep after the collaboration ends");
    }

    [Fact]
    public async Task RevokedLink_SaveSessionTemplateFromPlan_IsDenied()
    {
        var (professional, professionalProfileId, professionalUserId) = await RegisterProfessionalAsync(
            "revoked-trn-template", "Trainer");
        var (_, clientProfileId, clientUserId) = await RegisterClientAsync("revoked-trn-template");
        var linkPublicId = await LinkAsync(professionalProfileId, clientProfileId);

        var (planId, sessionId) = await SeedTrainingPlanWithSessionAsync(clientUserId, professionalUserId);

        var beforeRevocation = await professional.PostAsJsonAsync(
            "/training/session-templates/from-plan",
            new { PlanId = planId, WeekNumber = 1, DayOfWeek = 1, SessionId = sessionId, Name = "Before" });
        beforeRevocation.StatusCode.Should().Be(
            HttpStatusCode.Created, "the professional is still linked at this point");

        await DeactivateLinkAsync(linkPublicId);

        var afterRevocation = await professional.PostAsJsonAsync(
            "/training/session-templates/from-plan",
            new { PlanId = planId, WeekNumber = 1, DayOfWeek = 1, SessionId = sessionId, Name = "After" });

        afterRevocation.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "a revoked trainer must not be able to copy the client's session into a permanent " +
            "template they keep after the collaboration ends");
    }

    // ── ending a collaboration retires that professional's plans ─────────────
    // Author-scoping the publish-week archival removed the only thing that used to retire a
    // departing professional's plan — via the missing author predicate that was itself the
    // vulnerability. Ending the collaboration now retires those plans explicitly, so they cannot
    // linger Active and unreachable: the departed professional can no longer Complete or Delete
    // them, and the client-facing resolver picks among Active plans by latest StartDate with no
    // author predicate.

    [Fact]
    public async Task EndCollaboration_ArchivesTheDepartingProfessionalsPlans()
    {
        var (_, professionalProfileId, professionalUserId) = await RegisterProfessionalAsync(
            "end-collab-archive", "Nutritionist", "Trainer");
        var (clientHttp, clientProfileId, clientUserId) = await RegisterClientWithSessionAsync("end-collab-archive");
        var linkPublicId = await LinkAsync(professionalProfileId, clientProfileId);

        var nutritionPlanId = await SeedNutritionPlanAsync(clientUserId, professionalUserId);
        var trainingPlanId = await SeedTrainingPlanAsync(clientUserId, professionalUserId);

        var response = await clientHttp.DeleteAsync($"/client/collaborations/{linkPublicId}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await ReadNutritionPlanStatusAsync(nutritionPlanId)).Should().Be(
            NutritionPlanStatus.Archived,
            "a plan whose author can no longer reach it must not stay Active and unreachable");
        (await ReadTrainingPlanStatusAsync(trainingPlanId)).Should().Be(
            TrainingPlanStatus.Archived,
            "both domains retire, since the whole collaboration ended");
    }

    /// <summary>
    /// Draft plans retire too. A draft is never served to the client, so leaving one behind is not
    /// a disclosure — but the plan routes now gate on the live link, so it would be equally
    /// undeletable, and the document would accumulate with nobody able to reach it.
    /// </summary>
    /// <remarks>
    /// The Version bump is asserted alongside because it is load-bearing rather than bookkeeping:
    /// the version-gated replace matches on ExternalId + Version, so without the bump an update
    /// that had already passed authorization would still match after the archival and write the
    /// whole pre-archival document back, resurrecting the plan as Active.
    /// </remarks>
    [Fact]
    public async Task EndCollaboration_ArchivesDraftPlansAndBumpsVersion()
    {
        var (_, professionalProfileId, professionalUserId) = await RegisterProfessionalAsync(
            "end-collab-draft", "Nutritionist");
        var (clientHttp, clientProfileId, clientUserId) = await RegisterClientWithSessionAsync("end-collab-draft");
        var linkPublicId = await LinkAsync(professionalProfileId, clientProfileId);

        var draftPlanId = await SeedNutritionPlanAsync(
            clientUserId, professionalUserId, NutritionPlanStatus.Draft);

        var response = await clientHttp.DeleteAsync($"/client/collaborations/{linkPublicId}");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var plan = await ReadNutritionPlanAsync(draftPlanId);

        plan.Status.Should().Be(
            NutritionPlanStatus.Archived,
            "a draft the author can no longer delete must not linger unreachable");
        plan.Version.Should().Be(
            2,
            "the archival bumps Version from its seeded 1, so a racing version-gated replace " +
            "conflicts instead of resurrecting the plan");
    }

    [Fact]
    public async Task EndCollaboration_LeavesAnotherProfessionalsPlanActive()
    {
        var (_, departingProfileId, departingUserId) = await RegisterProfessionalAsync(
            "end-collab-departing", "Nutritionist");
        var (_, incumbentProfileId, incumbentUserId) = await RegisterProfessionalAsync(
            "end-collab-incumbent", "Nutritionist");
        var (clientHttp, clientProfileId, clientUserId) = await RegisterClientWithSessionAsync("end-collab-other");

        var departingLinkPublicId = await LinkAsync(departingProfileId, clientProfileId);
        await LinkAsync(incumbentProfileId, clientProfileId);

        var departingPlanId = await SeedNutritionPlanAsync(clientUserId, departingUserId);
        var incumbentPlanId = await SeedNutritionPlanAsync(clientUserId, incumbentUserId);

        var response = await clientHttp.DeleteAsync($"/client/collaborations/{departingLinkPublicId}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await ReadNutritionPlanStatusAsync(departingPlanId)).Should().Be(
            NutritionPlanStatus.Archived, "the departing professional's plan retires");
        (await ReadNutritionPlanStatusAsync(incumbentPlanId)).Should().Be(
            NutritionPlanStatus.Active,
            "a client may hold plans from more than one professional — ending one collaboration " +
            "must not touch another's live plan");
    }

    /// <summary>
    /// A publish must not resurrect an archived plan. The publish write filter has no version
    /// comparison, so the Version bump an archival applies is invisible to it — only an explicit
    /// Ne(Archived) predicate stops the unconditional <c>Status = Active</c> in that update from
    /// taking effect.
    /// </summary>
    /// <remarks>
    /// The race this guards is narrow — a publish whose link check passed microseconds before the
    /// client ended the collaboration — and a race is not directly testable. This asserts the
    /// mechanism the race depends on instead: with the plan already Archived and the caller's link
    /// fully live, the publish must still refuse to set it back to Active. Remove the predicate and
    /// this test fails.
    /// </remarks>
    [Fact]
    public async Task PublishWeek_OnAnArchivedPlan_DoesNotResurrectIt()
    {
        var (professional, professionalProfileId, professionalUserId) = await RegisterProfessionalAsync(
            "publish-archived", "Nutritionist");
        var (_, clientProfileId, clientUserId) = await RegisterClientAsync("publish-archived");
        await LinkAsync(professionalProfileId, clientProfileId);

        var today = DateTime.SpecifyKind(DateTime.UtcNow.Date, DateTimeKind.Utc);
        var monday = today.AddDays(((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7);

        // Archived, but with an unpublished week — so only the status predicate can stop the write.
        var planId = await SeedNutritionPlanAsync(
            clientUserId, professionalUserId, NutritionPlanStatus.Archived, monday);

        var response = await professional.PostAsJsonAsync(
            $"/nutrition/plans/{planId}/weeks/1/publish", new { });

        response.StatusCode.Should().NotBe(
            HttpStatusCode.OK, "publishing must not succeed against an archived plan");

        (await ReadNutritionPlanStatusAsync(planId)).Should().Be(
            NutritionPlanStatus.Archived,
            "the unconditional Status = Active in the publish update must not reach an archived plan");
    }

    // ── publish-week sibling archival must be owner-scoped ───────────────────

    [Fact]
    public async Task PublishWeek_DoesNotArchiveAnotherProfessionalsOverlappingPlan()
    {
        var (publisher, publisherProfileId, publisherUserId) = await RegisterProfessionalAsync(
            "archive-publisher", "Nutritionist");
        var (_, incumbentProfileId, incumbentUserId) = await RegisterProfessionalAsync(
            "archive-incumbent", "Nutritionist");
        var (_, clientProfileId, clientUserId) = await RegisterClientAsync("archive");

        await LinkAsync(publisherProfileId, clientProfileId);
        await LinkAsync(incumbentProfileId, clientProfileId);

        var today = DateTime.SpecifyKind(DateTime.UtcNow.Date, DateTimeKind.Utc);

        // The incumbent's live plan: same client, same date window, different author.
        var incumbentPlanId = await SeedNutritionPlanAsync(
            clientUserId, incumbentUserId,
            NutritionPlanStatus.Active, today, weekCount: 4, weekStatus: WeekStatus.Published);

        // The publisher's own overlapping plan, plus a second one to prove the archival still
        // fires for plans the publisher DOES own.
        var publisherOwnSupersededId = await SeedNutritionPlanAsync(
            clientUserId, publisherUserId,
            NutritionPlanStatus.Active, today, weekCount: 4, weekStatus: WeekStatus.Published);
        var publisherPlanId = await SeedNutritionPlanAsync(
            clientUserId, publisherUserId,
            NutritionPlanStatus.Draft, today, weekCount: 4);

        var response = await publisher.PostAsJsonAsync(
            $"/nutrition/plans/{publisherPlanId}/weeks/1/publish", new { Version = 1 });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var incumbentStatus = await ReadNutritionPlanStatusAsync(incumbentPlanId);
        incumbentStatus.Should().Be(
            NutritionPlanStatus.Active,
            "the sibling archival had no author predicate, so publishing destroyed the other professional's live plan");

        var ownSupersededStatus = await ReadNutritionPlanStatusAsync(publisherOwnSupersededId);
        ownSupersededStatus.Should().Be(
            NutritionPlanStatus.Archived,
            "the publisher's own overlapping Active plan must still be superseded");
    }

    // ── local response DTOs ──────────────────────────────────────────────────

    private record PlanListResponse(List<PlanListItem> Plans, long TotalCount);

    private record PlanListItem(Guid PlanId, string Name, string Status);
}
