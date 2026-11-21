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
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;

namespace FormUp.Tests.Endpoints.Authorization;

/// <summary>
/// Regression tests for #916 — three trainer-facing endpoints
/// (<c>ListClientPlansEndpoint</c>, <c>GetClientTimelineEndpoint</c>,
/// <c>GetClientDashboardEndpoint</c>) gated on <see cref="ClientProfessionalLink.IsActive"/>
/// alone and returned both nutrition- and training-domain data regardless of the link's
/// <c>CanViewNutritionPlans</c> / <c>CanViewTrainingPlans</c> flags. A professional whose
/// link carries only one capability flag must never receive the other domain's data.
/// <para>
/// Mirrors the fixture shape of <see cref="CrossRoleLinkAccessTests"/> (#903) — register a
/// professional and a client, stamp a <see cref="ClientProfessionalLink"/> with explicit
/// capability flags, then assert what each endpoint returns.
/// </para>
/// </summary>
[Collection(TestCollection.Shard2)]
public class CrossDomainPlanAccessTests(FitnessApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static string UniqueEmail(string tag) => $"{Guid.NewGuid():N}@cross-domain-{tag}.com";

    // ── shared fixture helpers (mirrors CrossRoleLinkAccessTests) ────────────
    // #1104: actors are built directly via TestActors (UserManager/ApplicationDbContext),
    // bypassing the /auth/register + /auth/login HTTP round trip. Signatures/return shapes are
    // unchanged so every call site below is untouched.

    private async Task<(HttpClient Http, long ProfessionalProfileId, Guid ProfessionalUserId)> RegisterProfessionalAsync(string tag)
    {
        var actor = await TestActors.Trainer(factory)
            .WithEmail(UniqueEmail(tag))
            .CreateAsync(TestContext.Current.CancellationToken);

        return (actor.Http, actor.ProfileId, actor.UserId);
    }

    /// <summary>
    /// Registers a professional holding the given global roles. Needed for the escalation cases:
    /// the whole point is that holding a role must NOT widen what a narrowed link grants, so the
    /// test has to be able to hold both roles while carrying a single-flag link.
    /// </summary>
    private async Task<(HttpClient Http, long ProfessionalProfileId, Guid ProfessionalUserId)> RegisterProfessionalWithRolesAsync(
        string tag, params string[] roles)
    {
        var actor = await TestActors.Professional(factory, roles.Select(r => Enum.Parse<UserRole>(r, ignoreCase: true)).ToArray())
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
    /// Inserts an active <see cref="ClientProfessionalLink"/> directly with the given
    /// capability flags and returns the created link's internal id (needed for
    /// <see cref="QuestionnaireResponse.LinkId"/> seeding).
    /// </summary>
    private async Task<long> LinkAsync(
        long professionalProfileId, long clientProfileId, bool canViewNutritionPlans, bool canViewTrainingPlans) =>
        await TestActors.Link(factory, professionalProfileId, clientProfileId)
            .CanViewNutritionPlans(canViewNutritionPlans)
            .CanViewTrainingPlans(canViewTrainingPlans)
            .CreateAsync(TestContext.Current.CancellationToken);

    /// <summary>
    /// Seeds one nutrition plan, one training plan, a meal log, a completed workout
    /// (SessionExecution), a personal record, a body measurement, and a submitted
    /// questionnaire response for the given client — enough source data for every
    /// timeline entry type and both ListClientPlans plan types to be exercised.
    /// </summary>
    private async Task SeedBothDomainsAsync(
        Guid clientUserId, long clientProfileId, Guid professionalUserId, long linkId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        var start = DateTime.UtcNow.Date.AddDays(-14);

        await mongo.NutritionPlans.InsertOneAsync(new NutritionPlan
        {
            Id = ObjectId.GenerateNewId(),
            ExternalId = Guid.NewGuid(),
            ClientId = clientUserId,
            NutritionistId = professionalUserId,
            Name = "Cross-Domain Nutrition Plan",
            Status = NutritionPlanStatus.Active,
            StartDate = start,
            // #1014: publish never writes the plan-level DatePublished field — only
            // weeks[].datePublished. The timeline's plan-publish derivation reads a published
            // week, so this fixture must carry one instead of the plan-level field.
            Weeks = [new PlanWeek { WeekNumber = 1, Status = WeekStatus.Published, DatePublished = start }],
            Version = 1,
            DateCreated = start,
        }, cancellationToken: TestContext.Current.CancellationToken);

        await mongo.TrainingPlans.InsertOneAsync(new TrainingPlan
        {
            Id = ObjectId.GenerateNewId(),
            ExternalId = Guid.NewGuid(),
            ClientId = clientUserId,
            TrainerId = professionalUserId,
            Name = "Cross-Domain Training Plan",
            Status = TrainingPlanStatus.Active,
            StartDate = start,
            Weeks = [new TrainingWeek { WeekNumber = 1, Status = WeekStatus.Published, DatePublished = start, Days = [] }],
            Version = 1,
            DateCreated = start,
        }, cancellationToken: TestContext.Current.CancellationToken);

        await mongo.MealLogs.InsertOneAsync(new MealLog
        {
            Id = ObjectId.GenerateNewId(),
            ClientId = clientUserId,
            PlanId = Guid.NewGuid(),
            MealId = Guid.NewGuid(),
            EatenAt = DateTime.UtcNow.AddDays(-1),
            FoodsEaten = [],
        }, cancellationToken: TestContext.Current.CancellationToken);

        await mongo.SessionExecutions.InsertOneAsync(new SessionExecution
        {
            Id = ObjectId.GenerateNewId(),
            ExternalId = Guid.NewGuid(),
            ClientId = clientUserId,
            Date = SessionExecution.ToCompletionDateUtc(DateTime.UtcNow.AddDays(-2)),
            Status = SessionExecutionStatus.Completed,
            Performance = new SessionExecutionPerformance
            {
                StartedAt = DateTime.UtcNow.AddDays(-2).AddMinutes(-30),
                CompletedAt = DateTime.UtcNow.AddDays(-2),
                Workouts = [],
            },
            DateCreated = DateTime.UtcNow,
        }, cancellationToken: TestContext.Current.CancellationToken);

        await mongo.PersonalRecords.InsertOneAsync(new PersonalRecord
        {
            Id = ObjectId.GenerateNewId(),
            ExternalId = Guid.NewGuid(),
            ClientId = clientUserId,
            ExerciseExternalId = Guid.NewGuid(),
            ExerciseName = "Bench Press",
            WeightKg = 100m,
            Reps = 5,
            AchievedAt = DateTime.UtcNow.AddDays(-3),
            WorkoutLogId = Guid.NewGuid(),
            SetNumber = 1,
            Version = 1,
            DateCreated = DateTime.UtcNow,
        }, cancellationToken: TestContext.Current.CancellationToken);

        db.BodyMeasurements.Add(new BodyMeasurement
        {
            PublicId = Guid.NewGuid(),
            ClientProfileId = clientProfileId,
            MeasuredAt = DateTime.UtcNow.AddDays(-4),
            WeightKg = 78m,
        });

        var questionnaire = new Questionnaire
        {
            PublicId = Guid.NewGuid(),
            ProfessionalId = professionalUserId,
            Title = "Cross-Domain Questionnaire",
            IsActive = true,
            DateCreated = DateTime.UtcNow,
        };
        db.Questionnaires.Add(questionnaire);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.QuestionnaireResponses.Add(new QuestionnaireResponse
        {
            PublicId = Guid.NewGuid(),
            QuestionnaireId = questionnaire.Id,
            ClientId = clientUserId,
            ProfessionalId = professionalUserId,
            LinkId = linkId,
            Status = QuestionnaireResponseStatus.Submitted,
            SubmittedAt = DateTime.UtcNow.AddDays(-5),
            DateCreated = DateTime.UtcNow,
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    // ── neither-flag denial (AC4) ─────────────────────────────────────────────

    [Fact]
    public async Task ActiveLinkWithNeitherCapability_ListClientPlans_Returns403()
    {
        var (professional, professionalProfileId, professionalUserId) = await RegisterProfessionalAsync("neither-list");
        var (clientPublicId, clientProfileId, _) = await RegisterClientAsync("neither-list");
        await LinkAsync(professionalProfileId, clientProfileId, canViewNutritionPlans: false, canViewTrainingPlans: false);

        var response = await professional.GetAsync($"/trainer/clients/{clientPublicId}/plans");

        response.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            "an active link granting neither capability flag must deny outright, matching HasAnyPlanAccessAsync semantics from #903");
    }

    [Fact]
    public async Task ActiveLinkWithNeitherCapability_GetClientTimeline_Returns403()
    {
        var (professional, professionalProfileId, professionalUserId) = await RegisterProfessionalAsync("neither-timeline");
        var (clientPublicId, clientProfileId, _) = await RegisterClientAsync("neither-timeline");
        await LinkAsync(professionalProfileId, clientProfileId, canViewNutritionPlans: false, canViewTrainingPlans: false);

        var response = await professional.GetAsync($"/trainer/clients/{clientPublicId}/timeline");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ActiveLinkWithNeitherCapability_GetClientDashboard_Returns403()
    {
        var (professional, professionalProfileId, professionalUserId) = await RegisterProfessionalAsync("neither-dashboard");
        var (clientPublicId, clientProfileId, _) = await RegisterClientAsync("neither-dashboard");
        await LinkAsync(professionalProfileId, clientProfileId, canViewNutritionPlans: false, canViewTrainingPlans: false);

        var response = await professional.GetAsync($"/trainer/clients/{clientPublicId}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── ListClientPlans — single-flag domain filtering ────────────────────────

    [Fact]
    public async Task NutritionOnlyLink_ListClientPlans_ReturnsOnlyNutritionPlans()
    {
        var (professional, professionalProfileId, professionalUserId) = await RegisterProfessionalWithRolesAsync("nutrition-only-list", "Trainer", "Nutritionist");
        var (clientPublicId, clientProfileId, clientUserId) = await RegisterClientAsync("nutrition-only-list");
        var linkId = await LinkAsync(professionalProfileId, clientProfileId, canViewNutritionPlans: true, canViewTrainingPlans: false);

        await SeedBothDomainsAsync(clientUserId, clientProfileId, professionalUserId, linkId);

        var response = await professional.GetAsync($"/trainer/clients/{clientPublicId}/plans");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<PlansResponse>(
            JsonOptions, TestContext.Current.CancellationToken);

        body!.CanViewNutritionPlans.Should().BeTrue();
        body.CanViewTrainingPlans.Should().BeFalse();
        body.Plans.Should().NotBeEmpty();
        body.Plans.Should().OnlyContain(p => p.PlanType == "Nutrition",
            "a nutrition-only link must never receive training plan items");
    }

    [Fact]
    public async Task TrainingOnlyLink_ListClientPlans_ReturnsOnlyTrainingPlans()
    {
        var (professional, professionalProfileId, professionalUserId) = await RegisterProfessionalAsync("training-only-list");
        var (clientPublicId, clientProfileId, clientUserId) = await RegisterClientAsync("training-only-list");
        var linkId = await LinkAsync(professionalProfileId, clientProfileId, canViewNutritionPlans: false, canViewTrainingPlans: true);

        await SeedBothDomainsAsync(clientUserId, clientProfileId, professionalUserId, linkId);

        var response = await professional.GetAsync($"/trainer/clients/{clientPublicId}/plans");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<PlansResponse>(
            JsonOptions, TestContext.Current.CancellationToken);

        body!.CanViewNutritionPlans.Should().BeFalse();
        body.CanViewTrainingPlans.Should().BeTrue();
        body.Plans.Should().NotBeEmpty();
        body.Plans.Should().OnlyContain(p => p.PlanType == "Training",
            "a training-only link must never receive nutrition plan items");
    }

    [Fact]
    public async Task BothFlagsLink_ListClientPlans_ReturnsBothPlanTypes()
    {
        // Regression guard — a fully-entitled caller's response must be additive/unchanged.
        var (professional, professionalProfileId, professionalUserId) = await RegisterProfessionalWithRolesAsync("both-flags-list", "Trainer", "Nutritionist");
        var (clientPublicId, clientProfileId, clientUserId) = await RegisterClientAsync("both-flags-list");
        var linkId = await LinkAsync(professionalProfileId, clientProfileId, canViewNutritionPlans: true, canViewTrainingPlans: true);

        await SeedBothDomainsAsync(clientUserId, clientProfileId, professionalUserId, linkId);

        var response = await professional.GetAsync($"/trainer/clients/{clientPublicId}/plans");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<PlansResponse>(
            JsonOptions, TestContext.Current.CancellationToken);

        body!.CanViewNutritionPlans.Should().BeTrue();
        body.CanViewTrainingPlans.Should().BeTrue();
        body.Plans.Should().Contain(p => p.PlanType == "Nutrition");
        body.Plans.Should().Contain(p => p.PlanType == "Training");
    }

    // ── GetClientTimeline — single-flag domain filtering + dual-readable classification ──

    [Fact]
    public async Task NutritionOnlyLink_GetClientTimeline_ExcludesTrainingEntries_KeepsDualReadableEntries()
    {
        var (professional, professionalProfileId, professionalUserId) = await RegisterProfessionalWithRolesAsync("nutrition-only-timeline", "Trainer", "Nutritionist");
        var (clientPublicId, clientProfileId, clientUserId) = await RegisterClientAsync("nutrition-only-timeline");
        var linkId = await LinkAsync(professionalProfileId, clientProfileId, canViewNutritionPlans: true, canViewTrainingPlans: false);

        await SeedBothDomainsAsync(clientUserId, clientProfileId, professionalUserId, linkId);

        var response = await professional.GetAsync($"/trainer/clients/{clientPublicId}/timeline?limit=100");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<TimelineResponse>(
            JsonOptions, TestContext.Current.CancellationToken);

        body!.CanViewNutritionPlans.Should().BeTrue();
        body.CanViewTrainingPlans.Should().BeFalse();

        var types = body.Items.Select(i => i.Type).ToList();

        types.Should().Contain("meal_day");
        types.Should().Contain("nutrition_plan_published");
        types.Should().NotContain("workout", "training-domain entries must not leak to a nutrition-only link");
        types.Should().NotContain("training_plan_published", "training-domain entries must not leak to a nutrition-only link");
        types.Should().NotContain("personal_record", "training-domain entries must not leak to a nutrition-only link");

        // Dual-readable standalone entries — classification rule (#916): these are NOT
        // attached to a nutrition or training item, so they survive in BOTH directions.
        types.Should().Contain("measurement", "measurements are dual-readable, not nutrition-scoped");
        types.Should().Contain("questionnaire", "questionnaire responses are dual-readable");
        types.Should().Contain("linked", "the link event is dual-readable");
    }

    [Fact]
    public async Task TrainingOnlyLink_GetClientTimeline_ExcludesNutritionEntries_KeepsDualReadableEntries()
    {
        var (professional, professionalProfileId, professionalUserId) = await RegisterProfessionalAsync("training-only-timeline");
        var (clientPublicId, clientProfileId, clientUserId) = await RegisterClientAsync("training-only-timeline");
        var linkId = await LinkAsync(professionalProfileId, clientProfileId, canViewNutritionPlans: false, canViewTrainingPlans: true);

        await SeedBothDomainsAsync(clientUserId, clientProfileId, professionalUserId, linkId);

        var response = await professional.GetAsync($"/trainer/clients/{clientPublicId}/timeline?limit=100");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<TimelineResponse>(
            JsonOptions, TestContext.Current.CancellationToken);

        body!.CanViewNutritionPlans.Should().BeFalse();
        body.CanViewTrainingPlans.Should().BeTrue();

        var types = body.Items.Select(i => i.Type).ToList();

        types.Should().Contain("workout");
        types.Should().Contain("training_plan_published");
        types.Should().Contain("personal_record");
        types.Should().NotContain("meal_day", "nutrition-domain entries must not leak to a training-only link");
        types.Should().NotContain("nutrition_plan_published", "nutrition-domain entries must not leak to a training-only link");

        // Dual-readable standalone entries — classification rule (#916): must remain
        // visible in this direction too.
        types.Should().Contain("measurement", "measurements are dual-readable, not training-scoped");
        types.Should().Contain("questionnaire", "questionnaire responses are dual-readable");
        types.Should().Contain("linked", "the link event is dual-readable");
    }

    [Fact]
    public async Task BothFlagsLink_GetClientTimeline_ReturnsAllEightEntryTypes()
    {
        // Regression guard — a fully-entitled caller's timeline must be unchanged.
        var (professional, professionalProfileId, professionalUserId) = await RegisterProfessionalWithRolesAsync("both-flags-timeline", "Trainer", "Nutritionist");
        var (clientPublicId, clientProfileId, clientUserId) = await RegisterClientAsync("both-flags-timeline");
        var linkId = await LinkAsync(professionalProfileId, clientProfileId, canViewNutritionPlans: true, canViewTrainingPlans: true);

        await SeedBothDomainsAsync(clientUserId, clientProfileId, professionalUserId, linkId);

        var response = await professional.GetAsync($"/trainer/clients/{clientPublicId}/timeline?limit=100");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<TimelineResponse>(
            JsonOptions, TestContext.Current.CancellationToken);

        body!.CanViewNutritionPlans.Should().BeTrue();
        body.CanViewTrainingPlans.Should().BeTrue();

        var types = body.Items.Select(i => i.Type).ToHashSet();
        types.Should().Contain(new[]
        {
            "meal_day", "nutrition_plan_published",
            "workout", "training_plan_published", "personal_record",
            "measurement", "questionnaire", "linked"
        });
    }

    // ── client verdict: itemised per-domain signals (F5) ──────────────────────
    // The endpoint gated on IsActive alone and carried no neither-flag deny, so it reported how
    // many sessions the client's trainer had programmed, how many they completed, and their
    // personal-record count to a link that denies training. The blended Verdict scalar itself
    // (#919) is now reduced to only the domains the caller's link grants: the denied domain's
    // read is skipped outright rather than computed and filtered afterward, so it cannot leak
    // through the headline verdict either. Weight and LastActiveAt remain dual-readable and
    // always contribute, per the classification rule at ClientVerdictService.cs:99-102.

    [Fact]
    public async Task ActiveLinkWithNeitherCapability_GetClientVerdict_Returns403()
    {
        var (professional, professionalProfileId, _) = await RegisterProfessionalAsync("neither-verdict");
        var (clientPublicId, clientProfileId, _) = await RegisterClientAsync("neither-verdict");
        await LinkAsync(professionalProfileId, clientProfileId, canViewNutritionPlans: false, canViewTrainingPlans: false);

        var response = await professional.GetAsync($"/trainer/clients/{clientPublicId}/verdict");

        response.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            "the three sibling routes all deny a link carrying neither flag; this one did not");
    }

    [Fact]
    public async Task NutritionOnlyLink_GetClientVerdict_OmitsTrainingSignals()
    {
        var (professional, professionalProfileId, professionalUserId) = await RegisterProfessionalAsync("nutrition-only-verdict");
        var (clientPublicId, clientProfileId, clientUserId) = await RegisterClientAsync("nutrition-only-verdict");
        var linkId = await LinkAsync(professionalProfileId, clientProfileId, canViewNutritionPlans: true, canViewTrainingPlans: false);

        await SeedBothDomainsAsync(clientUserId, clientProfileId, professionalUserId, linkId);
        await SeedPersonalRecordThisMonthAsync(clientUserId);

        var response = await professional.GetAsync($"/trainer/clients/{clientPublicId}/verdict");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<VerdictResponse>(
            JsonOptions, TestContext.Current.CancellationToken);

        body!.PrCountThisMonth.Should().BeNull(
            "the client's personal-record count is training-domain data");
        body.TrainingFrequencyPrescribed.Should().BeNull(
            "how many sessions the client's trainer programmed is training-domain data");
        body.TrainingFrequencyActual.Should().BeNull(
            "how many the client completed is training-domain data");
        body.LastActiveAt.Should().NotBeNull(
            "activity timestamps stay dual-readable regardless of which domain the link denies");
    }

    [Fact]
    public async Task TrainingOnlyLink_GetClientVerdict_OmitsNutritionCompliance()
    {
        var (professional, professionalProfileId, professionalUserId) = await RegisterProfessionalAsync("training-only-verdict");
        var (clientPublicId, clientProfileId, clientUserId) = await RegisterClientAsync("training-only-verdict");
        var linkId = await LinkAsync(professionalProfileId, clientProfileId, canViewNutritionPlans: false, canViewTrainingPlans: true);

        await SeedBothDomainsAsync(clientUserId, clientProfileId, professionalUserId, linkId);
        await SeedPersonalRecordThisMonthAsync(clientUserId);

        var response = await professional.GetAsync($"/trainer/clients/{clientPublicId}/verdict");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<VerdictResponse>(
            JsonOptions, TestContext.Current.CancellationToken);

        body!.CompliancePercent.Should().BeNull(
            "the compliance figure this endpoint returns is specifically the nutrition one");
        body.PrCountThisMonth.Should().NotBeNull(
            "the gate is per domain — a training-only link must still receive training signals, " +
            "or the fix has degenerated into denying everything");
        body.LastActiveAt.Should().NotBeNull(
            "activity timestamps stay dual-readable regardless of which domain the link denies");
    }

    // ── client verdict: the blended scalar reduces to the visible domain (#919) ──────────
    // Prior to #919, ClientVerdictService.ComputeVerdict consumed compliance and training-frequency
    // signals from BOTH domains for every caller, regardless of the link's capability flags. A
    // denied domain's bad signal could still flip the headline Verdict, letting a caller infer facts
    // about a domain they cannot see. The fix skips the denied domain's read outright, so its signal
    // never reaches ComputeVerdict at all.

    [Fact]
    public async Task TrainingOnlyLink_GetClientVerdict_ReducesScalarToVisibleDomain()
    {
        // The client has an active nutrition plan with 0% compliance — a hard OffTrack signal
        // under the old blended computation — but the caller's link denies nutrition entirely.
        // No training plan exists, so the visible domain contributes nothing either. Recent
        // activity (a body measurement) keeps the inactivity branch from separately triggering.
        var (professional, professionalProfileId, professionalUserId) = await RegisterProfessionalAsync("training-only-verdict-scalar");
        var (clientPublicId, clientProfileId, clientUserId) = await RegisterClientAsync("training-only-verdict-scalar");
        await LinkAsync(professionalProfileId, clientProfileId, canViewNutritionPlans: false, canViewTrainingPlans: true);

        await SeedNutritionPlanForComplianceAsync(clientUserId, professionalUserId, logMeal: false);
        await SeedRecentBodyMeasurementAsync(clientProfileId);

        var response = await professional.GetAsync($"/trainer/clients/{clientPublicId}/verdict");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<VerdictResponse>(
            JsonOptions, TestContext.Current.CancellationToken);

        body!.Verdict.Should().Be(
            ClientVerdict.OnTrack,
            "the 0% nutrition compliance must not reach the scalar for a caller whose link denies " +
            "nutrition — the read is skipped, not computed and dropped");
        body.CompliancePercent.Should().BeNull();
    }

    [Fact]
    public async Task NutritionOnlyLink_GetClientVerdict_ReducesScalarToVisibleDomain()
    {
        // The client has an active training plan prescribing one session this week and completing
        // none of it — a soft NeedsAttention signal under the old blended computation — but the
        // caller's link denies training entirely. Nutrition compliance is a clean 100%, so the
        // visible domain alone would never produce anything but OnTrack.
        var (professional, professionalProfileId, professionalUserId) = await RegisterProfessionalAsync("nutrition-only-verdict-scalar");
        var (clientPublicId, clientProfileId, clientUserId) = await RegisterClientAsync("nutrition-only-verdict-scalar");
        await LinkAsync(professionalProfileId, clientProfileId, canViewNutritionPlans: true, canViewTrainingPlans: false);

        await SeedNutritionPlanForComplianceAsync(clientUserId, professionalUserId, logMeal: true);
        await SeedTrainingPlanWithUnmetFrequencyAsync(clientUserId, professionalUserId);

        var response = await professional.GetAsync($"/trainer/clients/{clientPublicId}/verdict");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<VerdictResponse>(
            JsonOptions, TestContext.Current.CancellationToken);

        body!.Verdict.Should().Be(
            ClientVerdict.OnTrack,
            "the unmet training frequency must not reach the scalar for a caller whose link denies " +
            "training — the read is skipped, not computed and dropped");
        body.TrainingFrequencyActual.Should().BeNull();
        body.TrainingFrequencyPrescribed.Should().BeNull();
    }

    [Fact]
    public async Task BothFlagsLink_GetClientVerdict_ScalarStaysFullyBlended()
    {
        // Regression guard — a fully-entitled caller's Verdict and itemised signals must be
        // byte-identical to what the (still-blended) signals actually are. Both domains carry a
        // failing signal here (0% compliance, 0-of-1 sessions), so a caller who is entitled to see
        // both must still see the compliance-driven OffTrack and the raw itemised numbers.
        var (professional, professionalProfileId, professionalUserId) = await RegisterProfessionalAsync("both-flags-verdict-scalar");
        var (clientPublicId, clientProfileId, clientUserId) = await RegisterClientAsync("both-flags-verdict-scalar");
        await LinkAsync(professionalProfileId, clientProfileId, canViewNutritionPlans: true, canViewTrainingPlans: true);

        await SeedNutritionPlanForComplianceAsync(clientUserId, professionalUserId, logMeal: false);
        await SeedTrainingPlanWithUnmetFrequencyAsync(clientUserId, professionalUserId);

        var response = await professional.GetAsync($"/trainer/clients/{clientPublicId}/verdict");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<VerdictResponse>(
            JsonOptions, TestContext.Current.CancellationToken);

        body!.Verdict.Should().Be(
            ClientVerdict.OffTrack,
            "a fully-entitled caller must still see the compliance-driven OffTrack — the fix must " +
            "be inert when nothing is denied");
        body.CompliancePercent.Should().Be(0m);
        body.TrainingFrequencyActual.Should().Be(0);
        body.TrainingFrequencyPrescribed.Should().Be(1);
    }

    [Fact]
    public async Task TrainingOnlyLink_GetClientVerdict_NutritionOnlyPlanExistence_DoesNotLeakViaOffTrack()
    {
        // ComputeVerdict's no-activity branch returns OffTrack when EITHER domain has an active
        // plan. Before #919 that check ran on the real hasActiveNutritionPlan value regardless of
        // capability, so a training-only caller could infer "this client has a nutrition plan" from
        // an OffTrack verdict alone, with zero activity of any kind. The client here has no
        // activity at all and only a nutrition plan; the caller's link is training-only.
        var (professional, professionalProfileId, professionalUserId) = await RegisterProfessionalAsync("training-only-verdict-existence");
        var (clientPublicId, clientProfileId, clientUserId) = await RegisterClientAsync("training-only-verdict-existence");
        await LinkAsync(professionalProfileId, clientProfileId, canViewNutritionPlans: false, canViewTrainingPlans: true);

        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
            await mongo.NutritionPlans.InsertOneAsync(new NutritionPlan
            {
                Id = ObjectId.GenerateNewId(),
                ExternalId = Guid.NewGuid(),
                ClientId = clientUserId,
                NutritionistId = professionalUserId,
                Name = "Existence-Only Nutrition Plan",
                Status = NutritionPlanStatus.Active,
                Weeks = [],
                Version = 1,
                DateCreated = DateTime.UtcNow,
            }, cancellationToken: TestContext.Current.CancellationToken);
        }

        var response = await professional.GetAsync($"/trainer/clients/{clientPublicId}/verdict");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<VerdictResponse>(
            JsonOptions, TestContext.Current.CancellationToken);

        body!.Verdict.Should().NotBe(
            ClientVerdict.OffTrack,
            "a training-only caller must not be able to infer that the client has a nutrition plan " +
            "from an OffTrack verdict driven purely by plan existence");
    }

    // ── client photos: domain-tagged categories follow the flags (F7) ─────────
    // The caller supplies the category filter, so an IsActive-only check let them select precisely
    // the domain their link denies. Body and free-form photos stay dual-readable: they carry a null
    // plan id and hang off nothing, exactly how the timeline endpoint treats body measurements.

    [Fact]
    public async Task ActiveLinkWithNeitherCapability_GetTrainerClientPhotos_Returns404()
    {
        var (professional, professionalProfileId, _) = await RegisterProfessionalAsync("neither-photos");
        var (clientPublicId, clientProfileId, _) = await RegisterClientAsync("neither-photos");
        await LinkAsync(professionalProfileId, clientProfileId, canViewNutritionPlans: false, canViewTrainingPlans: false);

        var response = await professional.GetAsync($"/trainer/clients/{clientPublicId}/photos");

        response.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "this route denies with 404 rather than 403 — matching its existing no-link response " +
            "so a denial does not disclose that the client record exists");
    }

    [Fact]
    public async Task TrainingOnlyLink_GetTrainerClientPhotos_ExcludesFoodPhotos_KeepsDualReadable()
    {
        var (professional, professionalProfileId, professionalUserId) = await RegisterProfessionalAsync("training-only-photos");
        var (clientPublicId, clientProfileId, _) = await RegisterClientAsync("training-only-photos");
        await LinkAsync(professionalProfileId, clientProfileId, canViewNutritionPlans: false, canViewTrainingPlans: true);

        await SeedAllPhotoCategoriesAsync(clientProfileId, professionalUserId);

        var response = await professional.GetAsync($"/trainer/clients/{clientPublicId}/photos?pageSize=100");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var photos = await ReadPhotosAsync(response);
        var categories = photos.Select(p => p.Category).ToList();

        categories.Should().NotContain("Food", "a food photo hangs off a meal log in a nutrition plan");
        categories.Should().Contain("Training");
        categories.Should().Contain("FreeForm", "free-form photos are standalone and stay dual-readable");

        // Asserted by URL, not by category: two Body rows are seeded and only one of them carries
        // PlanType = Nutrition, so a category-level `Contain("Body")` is satisfied by the other row
        // and passes even with a PlanType-keyed predicate that wrongly hides this one.
        photos.Select(p => p.BlobUrl).Should().Contain(
            url => url.Contains("body-via-nutrition-screen"),
            "a body photo uploaded through the nutrition day-photo screen carries " +
            "PlanType = Nutrition, and must still reach a training-only coach — body photos are " +
            "dual-readable, so the scoping keys on Category, never on PlanType");

        // The caller picks the filter, so the excluded domain must stay excluded when they ask for
        // it by name — this is the exploit path, not a hypothetical.
        var targeted = await professional.GetAsync(
            $"/trainer/clients/{clientPublicId}/photos?category=Food&pageSize=100");
        targeted.StatusCode.Should().Be(HttpStatusCode.OK);

        (await ReadPhotosAsync(targeted)).Should().BeEmpty(
            "asking for the denied domain by name must return nothing, not everything in it");
    }

    [Fact]
    public async Task NutritionOnlyLink_GetTrainerClientPhotos_ExcludesTrainingPhotos_KeepsDualReadable()
    {
        var (professional, professionalProfileId, professionalUserId) = await RegisterProfessionalAsync("nutrition-only-photos");
        var (clientPublicId, clientProfileId, _) = await RegisterClientAsync("nutrition-only-photos");
        await LinkAsync(professionalProfileId, clientProfileId, canViewNutritionPlans: true, canViewTrainingPlans: false);

        await SeedAllPhotoCategoriesAsync(clientProfileId, professionalUserId);

        var response = await professional.GetAsync($"/trainer/clients/{clientPublicId}/photos?pageSize=100");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var categories = (await ReadPhotosAsync(response)).Select(p => p.Category).ToList();

        categories.Should().NotContain("Training");
        categories.Should().Contain("Food");
        categories.Should().Contain("Body");
        categories.Should().Contain("FreeForm");

        var targeted = await professional.GetAsync(
            $"/trainer/clients/{clientPublicId}/photos?category=Training&pageSize=100");
        targeted.StatusCode.Should().Be(HttpStatusCode.OK);

        (await ReadPhotosAsync(targeted)).Should().BeEmpty();
    }

    /// <summary>
    /// Reads the photo list once. The response content stream is consumed by the first
    /// ReadFromJsonAsync, so a caller needing both the categories and the urls must project from a
    /// single read rather than calling two readers over the same response.
    /// </summary>
    private static async Task<List<PhotoItem>> ReadPhotosAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<PhotosResponse>(
            JsonOptions, TestContext.Current.CancellationToken);

        return body!.Photos;
    }

    /// <summary>
    /// Seeds one photo per category. The two domain-tagged ones also carry the matching
    /// <c>PlanType</c> and a plan id, as the production write paths do; the two standalone ones
    /// leave both null — which is what makes them the regression guard for the nullable-PlanType
    /// predicate, since a naive <c>PlanType != X</c> would drop them.
    /// </summary>
    private async Task SeedAllPhotoCategoriesAsync(long clientProfileId, Guid uploadedByUserId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var takenAt = DateTime.UtcNow.AddDays(-1);

        PlanPhoto[] photos =
        [
            new PlanPhoto
            {
                ClientProfileId = clientProfileId,
                Category = PlanPhotoCategory.Food,
                PlanType = PlanPhotoType.Nutrition,
                PlanId = Guid.NewGuid(),
                MealLogId = ObjectId.GenerateNewId().ToString(),
                BlobUrl = "https://example.invalid/food.jpg",
                TakenAt = takenAt,
                UploadedByUserId = uploadedByUserId,
            },
            new PlanPhoto
            {
                ClientProfileId = clientProfileId,
                Category = PlanPhotoCategory.Training,
                PlanType = PlanPhotoType.Training,
                PlanId = Guid.NewGuid(),
                BlobUrl = "https://example.invalid/session.jpg",
                TakenAt = takenAt,
                UploadedByUserId = uploadedByUserId,
            },
            new PlanPhoto
            {
                ClientProfileId = clientProfileId,
                Category = PlanPhotoCategory.Body,
                BlobUrl = "https://example.invalid/body.jpg",
                TakenAt = takenAt,
                UploadedByUserId = uploadedByUserId,
            },
            // The shape SaveDayPhotosEndpoint actually writes: EVERY day photo, Body and FreeForm
            // included, carries PlanType = Nutrition and the plan's id, because day photos are
            // uploaded through a nutrition-plan screen. Scoping on PlanType rather than Category
            // therefore hid this row from a training-only coach — a real loss of dual-readable
            // content. Seeded so that regression cannot come back.
            new PlanPhoto
            {
                ClientProfileId = clientProfileId,
                Category = PlanPhotoCategory.Body,
                PlanType = PlanPhotoType.Nutrition,
                PlanId = Guid.NewGuid(),
                BlobUrl = "https://example.invalid/body-via-nutrition-screen.jpg",
                TakenAt = takenAt,
                UploadedByUserId = uploadedByUserId,
            },
            new PlanPhoto
            {
                ClientProfileId = clientProfileId,
                Category = PlanPhotoCategory.FreeForm,
                BlobUrl = "https://example.invalid/free.jpg",
                TakenAt = takenAt,
                UploadedByUserId = uploadedByUserId,
            },
        ];

        db.PlanPhotos.AddRange(photos);

        // The viewer rows the upload-time writer would have produced for the linked professional.
        db.PlanPhotoViewers.AddRange(photos.Select(photo => EntityBuilder.PlanPhotoViewer
            .ForPhoto(photo)
            .WithProfessionalUserId(uploadedByUserId)
            .Build()));

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Seeds a personal record achieved now, so the month-scoped count is deterministic regardless
    /// of which day of the month the suite runs on.
    /// </summary>
    private async Task SeedPersonalRecordThisMonthAsync(Guid clientUserId)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        await mongo.PersonalRecords.InsertOneAsync(new PersonalRecord
        {
            Id = ObjectId.GenerateNewId(),
            ExternalId = Guid.NewGuid(),
            ClientId = clientUserId,
            ExerciseExternalId = Guid.NewGuid(),
            ExerciseName = "Deadlift",
            WeightKg = 140m,
            Reps = 3,
            AchievedAt = DateTime.UtcNow,
            WorkoutLogId = Guid.NewGuid(),
            SetNumber = 1,
            Version = 1,
            DateCreated = DateTime.UtcNow,
        }, cancellationToken: TestContext.Current.CancellationToken);
    }

    // ── dashboard summary: per-link scope, never per-role (F3) ────────────────
    // Two defects in one endpoint. The nutrition fields were gated by nothing at all, and the
    // compliance discipline was derived from User.IsInRole rather than from the link — so holding a
    // role widened what a deliberately narrowed link returned.
    //
    // These assert null vs non-null rather than specific values, and so need no seeded plan data: a
    // permitted caller gets 0 (or false) for a client with no data, a denied caller gets null. That
    // distinction is exactly the property under test, and it is also why the endpoint sends null
    // instead of 0 — 0 asserts the client ate nothing.

    [Fact]
    public async Task TrainingOnlyLink_GetDashboardSummary_OmitsNutritionFields()
    {
        var (professional, professionalProfileId, professionalUserId) = await RegisterProfessionalAsync("training-only-summary");
        var (_, clientProfileId, clientUserId) = await RegisterClientAsync("training-only-summary");
        await LinkAsync(professionalProfileId, clientProfileId, canViewNutritionPlans: false, canViewTrainingPlans: true);

        // Seeded so KcalGoal and TodayKcal would be NON-null for a permitted caller — otherwise
        // asserting them null here proves nothing.
        await SeedActiveNutritionPlanWithTodayAsync(clientUserId, professionalUserId);

        var response = await professional.GetAsync("/trainer/dashboard-summary");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var client = await ReadSingleDashboardClientAsync(response);

        // All three discriminate against this seed. Verified by granting the nutrition flag: a
        // permitted caller receives AvgDailyKcal 0, TodayKcal 0 and KcalGoal 2200, so each BeNull
        // below fails. Without the seeded plan, KcalGoal and TodayKcal are null for a permitted
        // caller too and these two assertions would have proven nothing.
        client.AvgDailyKcal.Should().BeNull("the seven-day average is read off the client's nutrition plan");
        client.TodayKcal.Should().BeNull("today's consumed calories are nutrition data");
        client.KcalGoal.Should().BeNull("the daily target is read off the client's nutrition plan");
        client.ActiveNutritionPlansCount.Should().BeNull(
            "the count discloses that the client has a nutrition plan at all");

        client.WorkoutsPlanned.Should().NotBeNull(
            "the caller's own domain must still be populated — otherwise the gate is deny-all");
        client.HasActiveTrainingPlan.Should().NotBeNull();
    }

    [Fact]
    public async Task NutritionOnlyLink_GetDashboardSummary_OmitsTrainingFields()
    {
        var (professional, professionalProfileId, _) = await RegisterProfessionalAsync("nutrition-only-summary");
        var (_, clientProfileId, _) = await RegisterClientAsync("nutrition-only-summary");
        await LinkAsync(professionalProfileId, clientProfileId, canViewNutritionPlans: true, canViewTrainingPlans: false);

        var response = await professional.GetAsync("/trainer/dashboard-summary");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var client = await ReadSingleDashboardClientAsync(response);

        client.WorkoutsCompleted.Should().BeNull("today's completed sessions are training data");
        client.WorkoutsPlanned.Should().BeNull("what the trainer programmed is training data");
        client.HasActiveTrainingPlan.Should().BeNull(
            "false would assert the client has no training plan — a claim this caller has not earned");

        client.ActiveNutritionPlansCount.Should().NotBeNull("the caller's own domain stays populated");
    }

    /// <summary>
    /// The escalation case. A professional holding BOTH global roles whose link was deliberately
    /// narrowed to training-only still satisfies <c>User.IsInRole(Nutritionist)</c>, so the old
    /// role-derived discipline resolved to combined and returned the nutrition figures for a link
    /// that denies nutrition. Scope must come from the link, never from role state.
    /// </summary>
    [Fact]
    public async Task DualRoleProfessional_WithNarrowedLink_GetDashboardSummary_StillOmitsDeniedDomain()
    {
        var (professional, professionalProfileId, _) = await RegisterProfessionalWithRolesAsync(
            "dual-role-narrowed", "Trainer", "Nutritionist");
        var (_, clientProfileId, _) = await RegisterClientAsync("dual-role-narrowed");
        await LinkAsync(professionalProfileId, clientProfileId, canViewNutritionPlans: false, canViewTrainingPlans: true);

        var response = await professional.GetAsync("/trainer/dashboard-summary");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var client = await ReadSingleDashboardClientAsync(response);

        client.AvgDailyKcal.Should().BeNull(
            "holding the Nutritionist role must not widen a link stamped nutrition-denied");
        client.ActiveNutritionPlansCount.Should().BeNull(
            "global role state must never widen a per-link capability");
    }

    /// <summary>
    /// Seeds an active nutrition plan whose published week resolves to a day for today, carrying a
    /// daily calorie target. Without this a permitted caller also receives null KcalGoal and
    /// TodayKcal — no plan means no target — so asserting those two are null for a DENIED caller
    /// would pass for a reason unrelated to the gate.
    /// </summary>
    private async Task SeedActiveNutritionPlanWithTodayAsync(Guid clientUserId, Guid nutritionistUserId)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        // Start on the Monday of the current week so day-index resolution lands inside week 1.
        var today = DateTime.SpecifyKind(DateTime.UtcNow.Date, DateTimeKind.Utc);
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));

        await mongo.NutritionPlans.InsertOneAsync(new NutritionPlan
        {
            Id = ObjectId.GenerateNewId(),
            ExternalId = Guid.NewGuid(),
            ClientId = clientUserId,
            NutritionistId = nutritionistUserId,
            Name = "Dashboard Gate Nutrition Plan",
            Status = NutritionPlanStatus.Active,
            StartDate = monday,
            GlobalSettings = new GlobalNutritionSettings { DailyKcal = 2200m },
            Weeks =
            [
                new PlanWeek
                {
                    WeekNumber = 1,
                    Status = WeekStatus.Published,
                    Days = Enumerable.Range(1, 7)
                        .Select(dayOfWeek => new PlanDay
                        {
                            DayOfWeek = dayOfWeek,
                            DayTotals = new NutrientTotals { Kcal = 2100m },
                            Meals = [],
                        })
                        .ToList(),
                }
            ],
            Version = 1,
            DateCreated = monday,
        }, cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Seeds an active nutrition plan whose published week resolves to exactly one planned meal
    /// for today, and optionally logs a matching meal so <c>NutritionCompliancePercent</c> lands
    /// deterministically at either 0% (<paramref name="logMeal"/> false) or 100% (true) — both
    /// values needed to isolate the verdict-scalar reduction from the actual compliance
    /// calculation, which is otherwise sensitive to plan-week/window alignment.
    /// </summary>
    private async Task SeedNutritionPlanForComplianceAsync(Guid clientUserId, Guid nutritionistUserId, bool logMeal)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        var today = DateTime.SpecifyKind(DateTime.UtcNow.Date, DateTimeKind.Utc);
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var todayDayOfWeek = (int)today.DayOfWeek == 0 ? 7 : (int)today.DayOfWeek;

        await mongo.NutritionPlans.InsertOneAsync(new NutritionPlan
        {
            Id = ObjectId.GenerateNewId(),
            ExternalId = Guid.NewGuid(),
            ClientId = clientUserId,
            NutritionistId = nutritionistUserId,
            Name = "Verdict-Scalar Nutrition Plan",
            Status = NutritionPlanStatus.Active,
            StartDate = monday,
            Weeks =
            [
                new PlanWeek
                {
                    WeekNumber = 1,
                    Status = WeekStatus.Published,
                    Days = Enumerable.Range(1, 7)
                        .Select(dayOfWeek => new PlanDay
                        {
                            DayOfWeek = dayOfWeek,
                            Meals = dayOfWeek == todayDayOfWeek
                                ? [new PlanMeal { MealId = Guid.NewGuid(), Kind = MealKind.Breakfast, Order = 1 }]
                                : [],
                        })
                        .ToList(),
                }
            ],
            Version = 1,
            DateCreated = monday,
        }, cancellationToken: TestContext.Current.CancellationToken);

        if (logMeal)
        {
            await mongo.MealLogs.InsertOneAsync(new MealLog
            {
                Id = ObjectId.GenerateNewId(),
                ClientId = clientUserId,
                PlanId = Guid.NewGuid(),
                MealId = Guid.NewGuid(),
                EatenAt = today.AddHours(8),
                FoodsEaten = [],
            }, cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// Seeds an active training plan prescribing exactly one session this week (one standalone
    /// exercise, so the session counts toward the plan's prescribed total) with no matching
    /// <see cref="SessionExecution"/> — deterministically actual=0, prescribed=1.
    /// </summary>
    private async Task SeedTrainingPlanWithUnmetFrequencyAsync(Guid clientUserId, Guid trainerUserId)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        var today = DateTime.SpecifyKind(DateTime.UtcNow.Date, DateTimeKind.Utc);
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var todayDayOfWeek = (int)today.DayOfWeek == 0 ? 7 : (int)today.DayOfWeek;

        await mongo.TrainingPlans.InsertOneAsync(new TrainingPlan
        {
            Id = ObjectId.GenerateNewId(),
            ExternalId = Guid.NewGuid(),
            ClientId = clientUserId,
            TrainerId = trainerUserId,
            Name = "Verdict-Scalar Training Plan",
            Status = TrainingPlanStatus.Active,
            StartDate = monday,
            Weeks =
            [
                new TrainingWeek
                {
                    WeekNumber = 1,
                    Status = WeekStatus.Published,
                    Days = Enumerable.Range(1, 7)
                        .Select(dayOfWeek => new TrainingDay
                        {
                            DayOfWeek = dayOfWeek,
                            Sessions = dayOfWeek == todayDayOfWeek
                                ?
                                [
                                    new TrainingSession
                                    {
                                        SessionId = Guid.NewGuid(),
                                        Name = "Verdict-Scalar Session",
                                        Order = 1,
                                        StandaloneExercises =
                                        [
                                            new SessionExercise
                                            {
                                                ExerciseId = Guid.NewGuid(),
                                                ExerciseExternalId = Guid.NewGuid(),
                                                ExerciseName = "Verdict-Scalar Exercise",
                                                Order = 1,
                                            }
                                        ],
                                    }
                                ]
                                : [],
                        })
                        .ToList(),
                }
            ],
            Version = 1,
            DateCreated = monday,
        }, cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Seeds a body measurement dated yesterday, with no weight target set anywhere for this
    /// client — supplies a dual-readable <c>LastActiveAt</c> without contributing a weight signal
    /// or belonging to either the nutrition or training domain.
    /// </summary>
    private async Task SeedRecentBodyMeasurementAsync(long clientProfileId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.BodyMeasurements.Add(new BodyMeasurement
        {
            PublicId = Guid.NewGuid(),
            ClientProfileId = clientProfileId,
            MeasuredAt = DateTime.UtcNow.AddDays(-1),
            WeightKg = 80m,
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<DashboardClient> ReadSingleDashboardClientAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<DashboardSummaryResponse>(
            JsonOptions, TestContext.Current.CancellationToken);

        return body!.Clients.Should().ContainSingle().Subject;
    }

    // ── client progress: body is not domain-neutral (F4) ──────────────────────
    // Either flag admits the caller and that stays — but the meal counts and macro averages are
    // nutrition data, and the compliance percentage and streak were the combined cross-domain
    // figures, so the leak ran in both directions.

    [Fact]
    public async Task TrainingOnlyLink_GetClientProgress_OmitsNutritionFigures()
    {
        var (professional, professionalProfileId, _) = await RegisterProfessionalAsync("training-only-progress");
        var (clientPublicId, clientProfileId, _) = await RegisterClientAsync("training-only-progress");
        await LinkAsync(professionalProfileId, clientProfileId, canViewNutritionPlans: false, canViewTrainingPlans: true);

        var response = await professional.GetAsync($"/trainer/clients/{clientPublicId}/progress");
        response.StatusCode.Should().Be(
            HttpStatusCode.OK, "either flag admits this route — it is dual-readable by design");

        var body = await response.Content.ReadFromJsonAsync<ClientProgressResponse>(
            JsonOptions, TestContext.Current.CancellationToken);

        body!.MealsPlanned.Should().BeNull("the client's planned meal count is nutrition data");
        body.MealsLogged.Should().BeNull("the client's logged meal count is nutrition data");
        body.AverageDailyMacros.Should().BeNull(
            "the full macro breakdown is nutrition data and is not even computed now");
    }

    [Fact]
    public async Task NutritionOnlyLink_GetClientProgress_KeepsNutritionFigures()
    {
        // Positive control for the pair above: the gate is per domain, not deny-all.
        var (professional, professionalProfileId, _) = await RegisterProfessionalAsync("nutrition-only-progress");
        var (clientPublicId, clientProfileId, _) = await RegisterClientAsync("nutrition-only-progress");
        await LinkAsync(professionalProfileId, clientProfileId, canViewNutritionPlans: true, canViewTrainingPlans: false);

        var response = await professional.GetAsync($"/trainer/clients/{clientPublicId}/progress");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<ClientProgressResponse>(
            JsonOptions, TestContext.Current.CancellationToken);

        body!.MealsPlanned.Should().NotBeNull();
        body.MealsLogged.Should().NotBeNull();
        body.AverageDailyMacros.Should().NotBeNull();
    }

    [Fact]
    public async Task ActiveLinkWithNeitherCapability_GetClientProgress_Returns404()
    {
        var (professional, professionalProfileId, _) = await RegisterProfessionalAsync("neither-progress");
        var (clientPublicId, clientProfileId, _) = await RegisterClientAsync("neither-progress");
        await LinkAsync(professionalProfileId, clientProfileId, canViewNutritionPlans: false, canViewTrainingPlans: false);

        var response = await professional.GetAsync($"/trainer/clients/{clientPublicId}/progress");

        response.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "matches the route's existing no-link response rather than introducing a 403 here");
    }

    // ── local response DTOs ────────────────────────────────────────────────────

    private record DashboardSummaryResponse(List<DashboardClient> Clients);

    private record DashboardClient(
        decimal? AvgDailyKcal,
        decimal? TodayKcal,
        decimal? KcalGoal,
        int? WorkoutsCompleted,
        int? WorkoutsPlanned,
        int? ActiveNutritionPlansCount,
        bool? HasActiveTrainingPlan);

    private record ClientProgressResponse(
        int? MealsPlanned,
        int? MealsLogged,
        NutrientTotalsDto? AverageDailyMacros);

    private record NutrientTotalsDto(decimal Kcal);


    private record VerdictResponse(
        ClientVerdict Verdict,
        decimal? CompliancePercent,
        int? TrainingFrequencyActual,
        int? TrainingFrequencyPrescribed,
        int? PrCountThisMonth,
        DateTime? LastActiveAt);

    private record PhotosResponse(List<PhotoItem> Photos);

    private record PhotoItem(string Category, string BlobUrl);

    private record PlansResponse(List<PlanItem> Plans, bool CanViewNutritionPlans, bool CanViewTrainingPlans);

    private record PlanItem(Guid PlanId, string PlanType, string Name, string Status);

    private record TimelineResponse(List<TimelineItem> Items, bool CanViewNutritionPlans, bool CanViewTrainingPlans);

    private record TimelineItem(string Id, string Type, DateTime OccurredAt);
}
