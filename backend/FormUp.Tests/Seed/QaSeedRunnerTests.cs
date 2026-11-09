using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Features.TrainingPlans.GetTrainingPlan;
using FormUp.Application.Features.Questionnaires.GetClientResponses;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Application.Seed;
using FormUp.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace FormUp.Tests.Seed;

// ---------------------------------------------------------------------------
// Factory — mirrors ResetEndpointFactoryBase but replaces FakeBlobStorageService
// with TrackingBlobStorageService so we can assert blob uploads without a real
// MinIO instance.  No MinIO Testcontainer is needed; the seed's Ensure*ImageAsync
// helpers call IBlobStorageService.UploadAsync which writes to the in-memory store.
// ---------------------------------------------------------------------------

/// <summary>
/// Blob storage stub that records uploaded keys so seed idempotency tests can
/// assert that each key was written exactly once per seed run.
/// </summary>
public sealed class TrackingBlobStorageService : IBlobStorageService
{
    private readonly HashSet<string> _objects = new(StringComparer.Ordinal);

    public List<string> UploadCalls { get; } = [];

    public Task<BlobUploadUrl> GenerateUploadUrlAsync(
        string containerPath, string contentType, TimeSpan expiresIn, CancellationToken ct)
    {
        var uploadUrl = $"https://fake-storage/upload/{containerPath}?token=test";
        return Task.FromResult(new BlobUploadUrl(uploadUrl, BuildPublicUrl(containerPath)));
    }

    /// <inheritdoc />
    /// <remarks>Mirrors <see cref="FormUp.Tests.Infrastructure.FakeBlobStorageService"/> — returns the bare container path (test double, no real host).</remarks>
    public string BuildPublicUrl(string containerPath) => containerPath;

    /// <inheritdoc />
    /// <remarks>
    /// Test double only — the seed-idempotency tests in this file never exercise the signing
    /// contract, so this does not need to mirror the real service's fail-closed behavior. Passes
    /// the stored value through unchanged.
    /// </remarks>
    public Task<string?> GenerateReadUrlAsync(string? storedBlobUrl, CancellationToken ct) =>
        Task.FromResult(storedBlobUrl);

    /// <inheritdoc />
    /// <remarks>Test double only — strips a query string, mirroring the real service's contract.</remarks>
    public string? NormalizeToCanonicalUrl(string blobUrl) =>
        string.IsNullOrWhiteSpace(blobUrl) ? null : blobUrl.Split('?', 2)[0];

    public Task UploadAsync(string containerPath, byte[] data, string contentType, CancellationToken ct)
    {
        _objects.Add(containerPath);
        UploadCalls.Add(containerPath);
        return Task.CompletedTask;
    }

    public Task<bool> ObjectExistsAsync(string containerPath, CancellationToken ct) =>
        Task.FromResult(_objects.Contains(containerPath));

    public Task DeleteAsync(string containerPath, CancellationToken ct)
    {
        _objects.Remove(containerPath);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Test double only — the seed-idempotency tests in this file never exercise the download
    /// contract, so this doesn't need real bytes. Reports existence via <see cref="_objects"/>
    /// with no content, matching no known seed-runner consumer.
    /// </remarks>
    public Task<BlobObject?> DownloadAsync(string containerPath, long maxBytesToDownload, CancellationToken ct) =>
        Task.FromResult(_objects.Contains(containerPath) ? new BlobObject(0, []) : null);

    /// <summary>
    /// Clears both the presence set and the call log. Part of #1104: with
    /// <see cref="QaSeedRunnerFactory"/> now shared across the whole test class instead of
    /// booted fresh per fact, clearing only <see cref="UploadCalls"/> left <c>_objects</c>
    /// carrying a prior fact's uploaded keys — <c>QaSeedRunner</c>'s own idempotency check
    /// (<c>ObjectExistsAsync</c>) then saw the blob as "already present" and skipped the
    /// upload the next fact expected to observe.
    /// </summary>
    public void Reset()
    {
        _objects.Clear();
        UploadCalls.Clear();
    }
}

/// <summary>
/// WebApplicationFactory that wires up real Postgres + Mongo via Testcontainers
/// and uses <see cref="TrackingBlobStorageService"/> in place of the real MinIO
/// blob service.  The TestingEnabled flag is set so QaSeedRunner's password check
/// is satisfied via the env var set in <see cref="ConfigureWebHost"/>.
/// </summary>
public class QaSeedRunnerFactory(SharedTestContainers sharedContainers) : WebApplicationFactory<Program>, IAsyncLifetime
{
    private string _postgresConnectionString = string.Empty;
    private string _mongoDatabaseName = string.Empty;
    private bool _defaultSeeded;

    public TrackingBlobStorageService BlobStorage { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("POSTGRES_PASSWORD", "test");
        builder.UseSetting("MONGO_PASSWORD", "test");
        builder.UseSetting("MINIO_ACCESS_KEY", "test");
        builder.UseSetting("MINIO_SECRET_KEY", "test");
        builder.UseSetting("JWT_SECRET", new string('x', 64));
        builder.UseSetting("RateLimiting:Disabled", "true");
        builder.UseSetting("Testing:Enabled", "true");

        // QaSeedRunner reads this via Environment.GetEnvironmentVariable.
        Environment.SetEnvironmentVariable("QA_SEED_PASSWORD", "TestSeed1!");

        builder.UseSetting("ConnectionStrings:PostgreSQl",
            "Host=localhost;Database=placeholder;Username=postgres");
        builder.UseSetting("ConnectionStrings:MongoDB",
            "mongodb://localhost:27017");

        builder.ConfigureServices(services =>
        {
            // Replace DbContext with Testcontainer-backed Postgres.
            var pgDesc = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<ApplicationDbContext>));
            if (pgDesc is not null) services.Remove(pgDesc);

            services.AddDbContext<ApplicationDbContext>(options =>
                options.UseNpgsql(_postgresConnectionString)
                    .ConfigureWarnings(w => w.Ignore(
                        Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));

            // Replace MongoDB with Testcontainer.
            var mongoDbDesc = services.SingleOrDefault(d => d.ServiceType == typeof(IMongoDatabase));
            if (mongoDbDesc is not null) services.Remove(mongoDbDesc);

            var mongoCtxDesc = services.SingleOrDefault(d => d.ServiceType == typeof(IMongoContext));
            if (mongoCtxDesc is not null) services.Remove(mongoCtxDesc);

            services.AddSingleton<IMongoDatabase>(_ =>
            {
                var client = new MongoClient(sharedContainers.MongoConnectionString);
                return client.GetDatabase(_mongoDatabaseName);
            });
            services.AddSingleton<IMongoContext, MongoContext>();

            // Replace external services with fakes.
            var emailDesc = services.SingleOrDefault(
                d => d.ServiceType == typeof(FormUp.Application.Domain.Interfaces.IEmailService));
            if (emailDesc is not null) services.Remove(emailDesc);
            services.AddSingleton<FakeEmailService>();
            services.AddSingleton<FormUp.Application.Domain.Interfaces.IEmailService>(
                sp => sp.GetRequiredService<FakeEmailService>());

            var notifierDesc = services.SingleOrDefault(
                d => d.ServiceType == typeof(FormUp.Application.Domain.Interfaces.IRealtimeNotifier));
            if (notifierDesc is not null) services.Remove(notifierDesc);
            services.AddSingleton<FakeRealtimeNotifier>();
            services.AddSingleton<FormUp.Application.Domain.Interfaces.IRealtimeNotifier>(
                sp => sp.GetRequiredService<FakeRealtimeNotifier>());

            // Replace blob storage with our tracking stub (shared instance so
            // assertions can inspect it after SeedAsync runs).
            var blobDesc = services.SingleOrDefault(
                d => d.ServiceType == typeof(IBlobStorageService));
            if (blobDesc is not null) services.Remove(blobDesc);
            services.AddSingleton<IBlobStorageService>(BlobStorage);

            var pushDesc = services.SingleOrDefault(
                d => d.ServiceType == typeof(FormUp.Application.Domain.Interfaces.IPushNotificationService));
            if (pushDesc is not null) services.Remove(pushDesc);
            services.AddSingleton<FakePushNotificationService>();
            services.AddSingleton<FormUp.Application.Domain.Interfaces.IPushNotificationService>(
                sp => sp.GetRequiredService<FakePushNotificationService>());

            // #726: prevent the background schedulers/worker from starting in this
            // test host — see TestHostedServiceExtensions for the root cause.
            services.RemoveBackgroundHostedServices();
        });

        builder.UseEnvironment("Development");
    }

    public async ValueTask InitializeAsync()
    {
        _postgresConnectionString = await sharedContainers.CreatePostgresDatabaseAsync("qa_seed_runner");
        _mongoDatabaseName = SharedTestContainers.CreateMongoDatabaseName("qa_seed_runner");

        // Apply migrations + seed roles.
        await ApplicationDbContextSeed.SeedAsync(Services);
    }

    /// <summary>
    /// Wipes Postgres, Mongo and the blob tracker, and marks the shared default-kind seed as gone.
    /// </summary>
    public async Task ResetAsync()
    {
        _defaultSeeded = false;
        await Services.ResetPostgresAndMongoAsync();
        BlobStorage.Reset();
    }

    /// <summary>
    /// Makes sure the database holds exactly one default-kind (rich) seed, seeding it only when a
    /// previous fact wiped or replaced it. Read-only facts share that one seed.
    /// </summary>
    public async Task EnsureDefaultSeedAsync()
    {
        if (_defaultSeeded)
        {
            return;
        }

        await ResetAsync();
        await QaSeedRunner.SeedAsync(Services);
        _defaultSeeded = true;
    }

    // Skip base.DisposeAsync() to avoid disposing the root IServiceProvider while other tests
    // may be holding a reference (see FitnessApiFactory comment). No Testcontainer to dispose
    // here anymore (#1104 Phase B); SharedTestContainers owns that.
    public new ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

// ---------------------------------------------------------------------------
// Test class
// ---------------------------------------------------------------------------

/// <summary>
/// Defines a separate collection for QaSeedRunner tests so they run serially
/// and don't contend with the shared Integration collection's Testcontainers.
/// Boots <see cref="QaSeedRunnerFactory"/> ONCE for the whole collection (mirrors
/// <see cref="Infrastructure.TestCollection"/>) rather than per test — see
/// <see cref="QaSeedRunnerFactory.EnsureDefaultSeedAsync"/> for how facts share one seed (#1104).
/// </summary>
[CollectionDefinition("SeedTests")]
public class SeedTestsCollection : ICollectionFixture<QaSeedRunnerFactory>;

/// <summary>
/// Idempotency integration tests for <see cref="QaSeedRunner.SeedAsync"/>.
/// All helpers must be individually idempotent: re-running over a partially or
/// fully seeded stack must converge without throwing on duplicate keys.
/// </summary>
/// <remarks>
/// Read-only shape facts share one default-kind seed (<see cref="QaSeedRunnerFactory.EnsureDefaultSeedAsync"/>).
/// Facts that need a pristine database — run-twice, minimal kind, unknown kind, Duo — wipe it first with
/// <see cref="QaSeedRunnerFactory.ResetAsync"/>, which also invalidates the shared seed.
/// </remarks>
[Collection("SeedTests")]
public class QaSeedRunnerTests(QaSeedRunnerFactory factory)
{
    private readonly QaSeedRunnerFactory _factory = factory;

    private async Task ResetAndSeedAsync()
    {
        await _factory.ResetAsync();
        await QaSeedRunner.SeedAsync(_factory.Services);
    }

    /// <summary>
    /// Running SeedAsync twice must be idempotent: every fixture stays at its first-run count,
    /// ids and links stay stable, and blob upload is not repeated after the first run.
    /// </summary>
    [Fact]
    public async Task SeedAsync_RunTwice_IsIdempotent()
    {
        var ct = TestContext.Current.CancellationToken;

        // Act — run twice over a pristine database.
        await _factory.ResetAsync();
        await QaSeedRunner.SeedAsync(_factory.Services);
        await QaSeedRunner.SeedAsync(_factory.Services);

        using var scope = _factory.Services.CreateScope();
        var sp    = scope.ServiceProvider;
        var db    = sp.GetRequiredService<ApplicationDbContext>();
        var mongo = sp.GetRequiredService<IMongoContext>();

        // PostgreSQL — exactly one user per role.
        var clientCount = await db.Users.CountAsync(
            u => u.Email == QaSeedRunner.ClientEmail, ct);
        clientCount.Should().Be(1, "QA client user must be created exactly once");

        var trainerCount = await db.Users.CountAsync(
            u => u.Email == QaSeedRunner.TrainerEmail, ct);
        trainerCount.Should().Be(1, "QA trainer user must be created exactly once");

        var nutriCount = await db.Users.CountAsync(
            u => u.Email == QaSeedRunner.NutriEmail, ct);
        nutriCount.Should().Be(1, "QA nutri user must be created exactly once");

        // MongoDB — foods.
        var foodExternalIds = new[]
        {
            QaSeedRunner.QaFood1ExternalId,
            QaSeedRunner.QaFood2ExternalId,
            QaSeedRunner.QaFood3ExternalId,
            QaSeedRunner.QaFood4ExternalId,
            QaSeedRunner.QaFood5ExternalId,
        };

        var foodCount = await mongo.Foods.CountDocumentsAsync(
            Builders<Food>.Filter.In(f => f.ExternalId, foodExternalIds),
            cancellationToken: ct);
        foodCount.Should().Be(5, "all five QA foods must be present with no duplicates");

        // MongoDB — recipes.
        var recipeExternalIds = new[]
        {
            QaSeedRunner.QaRecipe1ExternalId,
            QaSeedRunner.QaRecipe2ExternalId,
            QaSeedRunner.QaRecipe3ExternalId,
        };

        var recipeCount = await mongo.Recipes.CountDocumentsAsync(
            Builders<Recipe>.Filter.In(r => r.ExternalId, recipeExternalIds),
            cancellationToken: ct);
        recipeCount.Should().Be(3, "all three QA recipes must be present with no duplicates");

        // MongoDB — nutrition plan.
        var planCount = await mongo.NutritionPlans.CountDocumentsAsync(
            Builders<NutritionPlan>.Filter.Eq(p => p.ExternalId, QaSeedRunner.QaNutritionPlanExternalId),
            cancellationToken: ct);
        planCount.Should().Be(1, "the QA nutrition plan must be created exactly once");

        // Blobs — the TrackingBlobStorageService records ObjectExistsAsync-aware state.
        // After the first seed run the object is "present"; the second run should skip.
        // Upload count must be exactly 2 (avatar + food image) — not 4 (no re-upload on second run).
        _factory.BlobStorage.UploadCalls.Count(k => k == QaSeedRunner.QaAvatarBlobKey)
            .Should().Be(1, "avatar must be uploaded exactly once — idempotency guard prevents re-upload");
        _factory.BlobStorage.UploadCalls.Count(k => k == QaSeedRunner.QaFoodImageBlobKey)
            .Should().Be(1, "food image must be uploaded exactly once — idempotency guard prevents re-upload");

        // #1095 — third client (no conversation, no active plan) linked to qa.trainer exactly once.
        var client3Count = await db.Users.CountAsync(u => u.Email == QaSeedRunner.Client3Email, ct);
        client3Count.Should().Be(1, "the QA third-client (#1095) user must be created exactly once");

        var client3Profile = await db.ClientProfiles.FirstAsync(cp => cp.UserId == QaSeedRunner.Client3UserId, ct);
        var trainerProfile = await db.ProfessionalProfiles.FirstAsync(pp => pp.UserId == QaSeedRunner.TrainerUserId, ct);
        var client3LinkCount = await db.ClientProfessionalLinks.CountAsync(
            l => l.ClientProfileId == client3Profile.Id && l.ProfessionalProfileId == trainerProfile.Id, ct);
        client3LinkCount.Should().Be(1, "the qa.trainer <-> client3 link must be created exactly once");

        // #1095 — inbox conversation + its three messages, created exactly once.
        var conversationCount = await db.Conversations.CountAsync(
            c => c.PublicId == QaSeedRunner.QaInboxConversationId, ct);
        conversationCount.Should().Be(1, "the QA inbox conversation must be created exactly once");

        var inboxMessageIds = new[]
        {
            QaSeedRunner.QaInboxMessage1Id,
            QaSeedRunner.QaInboxMessage2Id,
            QaSeedRunner.QaInboxMessage3Id,
        };
        var inboxMessageCount = await db.ChatMessages.CountAsync(m => inboxMessageIds.Contains(m.PublicId), ct);
        inboxMessageCount.Should().Be(3, "all three QA inbox messages must be present with no duplicates");

        // #1095 — the two weekly check-in fixtures, created exactly once.
        var checkInIds = new[]
        {
            QaSeedRunner.QaWeeklyCheckInRespondedUnreviewedId,
            QaSeedRunner.QaWeeklyCheckInExpiredId,
        };
        var checkInCount = await db.QuestionnaireResponses.CountAsync(r => checkInIds.Contains(r.PublicId), ct);
        checkInCount.Should().Be(2, "both QA weekly check-in fixtures must be present with no duplicates");

        // #1101 — former client (ended link) + its conversation, created exactly once.
        (await db.Users.CountAsync(u => u.Email == QaSeedRunner.ClientFormerEmail, ct))
            .Should().Be(1, "the QA former-client (#1101) user must be created exactly once");

        var formerLinks = await db.ClientProfessionalLinks
            .Where(l => l.ClientProfile.UserId == QaSeedRunner.ClientFormerUserId)
            .ToListAsync(ct);
        formerLinks.Should().ContainSingle("the ended qa.trainer <-> former-client link must be created exactly once")
            .Which.ConnectionState.Should().Be(ConnectionState.Ended, "the link must stay ended across re-seeds");

        (await db.Conversations.CountAsync(c => c.PublicId == QaSeedRunner.QaEndedLinkConversationId, ct))
            .Should().Be(1, "the ended-link conversation must be created exactly once");

        var endedLinkMessageIds = new[] { QaSeedRunner.QaEndedLinkMessage1Id, QaSeedRunner.QaEndedLinkMessage2Id };
        (await db.ChatMessages.CountAsync(m => endedLinkMessageIds.Contains(m.PublicId), ct))
            .Should().Be(2, "both ended-link messages must be present with no duplicates");

        // Ending client — live link, no conversation, created exactly once.
        (await db.Users.CountAsync(u => u.Email == QaSeedRunner.ClientEndingEmail, ct))
            .Should().Be(1, "the ending-client user must be created exactly once");

        var endingLinks = await db.ClientProfessionalLinks
            .Where(l => l.ClientProfile.UserId == QaSeedRunner.ClientEndingUserId)
            .ToListAsync(ct);
        endingLinks.Should().ContainSingle("the qa.trainer <-> ending-client link must be created exactly once")
            .Which.ConnectionState.Should().Be(ConnectionState.Connected, "the ending client's link must be live so the e2e can end it");

        (await db.Conversations.CountAsync(
                c => c.ClientUserId == QaSeedRunner.ClientEndingUserId, ct))
            .Should().Be(0, "the ending client has no conversation");

        // #326 — past training plan and its two executions.
        (await mongo.TrainingPlans.CountDocumentsAsync(
                Builders<TrainingPlan>.Filter.Eq(p => p.ExternalId, QaSeedRunner.QaPastTrainingPlanExternalId),
                cancellationToken: ct))
            .Should().Be(1, "past training plan must not be duplicated on re-seed");
        (await mongo.SessionExecutions.CountDocumentsAsync(
                Builders<SessionExecution>.Filter.Eq(l => l.ExternalId, QaSeedRunner.QaPastCompletedExecutionId),
                cancellationToken: ct))
            .Should().Be(1, "completed SessionExecution must not be duplicated on re-seed");
        (await mongo.SessionExecutions.CountDocumentsAsync(
                Builders<SessionExecution>.Filter.Eq(l => l.ExternalId, QaSeedRunner.QaPastSkippedExecutionId),
                cancellationToken: ct))
            .Should().Be(1, "skipped SessionExecution must not be duplicated on re-seed");

        // #1303 — client Overview fixtures.
        var qaClientProfile = await db.ClientProfiles.FirstAsync(p => p.UserId == QaSeedRunner.ClientUserId, ct);
        (await db.BodyMeasurements.CountAsync(m => m.ClientProfileId == qaClientProfile.Id, ct)).Should().Be(8);
        (await db.ClientOnboardingData.CountAsync(o => o.ClientProfileId == qaClientProfile.Id, ct)).Should().Be(1);
        (await db.ClientNutritionTargets.CountAsync(t => t.ClientOnboardingData.ClientProfileId == qaClientProfile.Id, ct)).Should().Be(1);
        (await db.QuestionnaireResponses.CountAsync(r => r.Kind == QuestionnaireResponseKind.CheckIn && r.ClientId == QaSeedRunner.ClientUserId, ct)).Should().Be(7);
        (await mongo.MealLogs.CountDocumentsAsync(l => l.ClientId == QaSeedRunner.ClientUserId, cancellationToken: ct)).Should().Be(14);
        (await mongo.PersonalRecords.CountDocumentsAsync(r => r.ClientId == QaSeedRunner.ClientUserId, cancellationToken: ct)).Should().Be(1);
        (await FindMondayExecutionsAsync(mongo, ct)).Should().HaveCount(1);

        // #1311 — both-roles coach and QA Duo.
        (await db.Users.CountAsync(u => u.Email == QaSeedRunner.CoachEmail, ct)).Should().Be(1);
        (await db.Users.CountAsync(u => u.Email == QaSeedRunner.ClientDuoEmail, ct)).Should().Be(1);
        (await db.ClientProfessionalLinks.CountAsync(l => l.ProfessionalProfile.UserId == QaSeedRunner.CoachUserId, ct)).Should().Be(1);

        var duoProfile = await db.ClientProfiles.FirstAsync(p => p.UserId == QaSeedRunner.ClientDuoUserId, ct);
        (await db.BodyMeasurements.CountAsync(m => m.ClientProfileId == duoProfile.Id, ct)).Should().Be(8);
        (await db.ClientOnboardingData.CountAsync(o => o.ClientProfileId == duoProfile.Id, ct)).Should().Be(1);
        (await db.QuestionnaireResponses.CountAsync(r => r.Kind == QuestionnaireResponseKind.CheckIn && r.ClientId == QaSeedRunner.ClientDuoUserId, ct)).Should().Be(5);

        (await mongo.NutritionPlans.CountDocumentsAsync(p => p.ClientId == QaSeedRunner.ClientDuoUserId, cancellationToken: ct)).Should().Be(1);
        (await mongo.TrainingPlans.CountDocumentsAsync(p => p.ClientId == QaSeedRunner.ClientDuoUserId, cancellationToken: ct)).Should().Be(1);
        (await mongo.SessionExecutions.CountDocumentsAsync(e => e.ClientId == QaSeedRunner.ClientDuoUserId, cancellationToken: ct)).Should().Be(1);
        (await mongo.PersonalRecords.CountDocumentsAsync(r => r.ClientId == QaSeedRunner.ClientDuoUserId, cancellationToken: ct)).Should().Be(1);
        (await mongo.MealLogs.CountDocumentsAsync(l => l.ClientId == QaSeedRunner.ClientDuoUserId, cancellationToken: ct))
            .Should().BeGreaterThan(0);

        // #879 — dual-placement completion execution.
        (await mongo.SessionExecutions.CountDocumentsAsync(
                Builders<SessionExecution>.Filter.Eq(e => e.ExternalId, QaSeedRunner.QaDualPlacementSessionExecutionId),
                cancellationToken: ct))
            .Should().Be(1, "the dual-placement completion SessionExecution must not be duplicated on re-seed");

        // #946 — photo diary request rows.
        (await db.PhotoDiaryRequests.CountAsync(
                r => r.Id == QaSeedRunner.QaPhotoDiaryRequestWithPlanId || r.Id == QaSeedRunner.QaPhotoDiaryRequestPlanlessId, ct))
            .Should().Be(2, "the two PhotoDiaryRequest fixture rows must not be duplicated on re-seed");

        // Main-plan execution and the #474 multi-section plan + execution.
        (await mongo.SessionExecutions.CountDocumentsAsync(
                Builders<SessionExecution>.Filter.Eq(l => l.ExternalId, QaSeedRunner.QaMainPlanCompletedExecutionId),
                cancellationToken: ct))
            .Should().Be(1, "main-plan SessionExecution must not be duplicated on re-seed");
        (await mongo.TrainingPlans.CountDocumentsAsync(
                Builders<TrainingPlan>.Filter.Eq(p => p.ExternalId, QaSeedRunner.QaMultiSectionPlanExternalId),
                cancellationToken: ct))
            .Should().Be(1, "multi-section plan must not be duplicated on re-seed");
        (await mongo.SessionExecutions.CountDocumentsAsync(
                Builders<SessionExecution>.Filter.Eq(l => l.ExternalId, QaSeedRunner.QaMultiSectionExecutionId),
                cancellationToken: ct))
            .Should().Be(1, "multi-section SessionExecution must not be duplicated on re-seed");

        // #715 — trainer-owned questionnaire template, response, answers and training-plan link.
        (await db.Questionnaires.CountAsync(q => q.PublicId == QaSeedRunner.QaQuestionnaireExternalId, ct))
            .Should().Be(1, "the questionnaire template must not be duplicated on re-seed");
        (await db.QuestionnaireQuestions.CountAsync(q => q.Questionnaire.PublicId == QaSeedRunner.QaQuestionnaireExternalId, ct))
            .Should().Be(8, "the 8 template questions must not be duplicated on re-seed");
        (await db.QuestionnaireResponses.CountAsync(r => r.PublicId == QaSeedRunner.QaQuestionnaireResponseExternalId, ct))
            .Should().Be(1, "the submitted response must not be duplicated on re-seed");
        (await db.QuestionnaireAnswers.CountAsync(a => a.Response.PublicId == QaSeedRunner.QaQuestionnaireResponseExternalId, ct))
            .Should().Be(6, "the 6 answers must not be duplicated on re-seed");

        var trainingPlan = await mongo.TrainingPlans
            .Find(p => p.ExternalId == QaSeedRunner.QaTrainingPlanExternalId)
            .FirstOrDefaultAsync(ct);
        trainingPlan!.QuestionnaireResponseId.Should().Be(QaSeedRunner.QaQuestionnaireResponseExternalId,
            "the training plan link must remain stable across re-seeds");
        trainingPlan.Version.Should().Be(2, "the link update bumps Version exactly once, not once per seed run");

        // #720 — nutritionist-owned questionnaire template, response, answers and nutrition-plan link.
        (await db.Questionnaires.CountAsync(q => q.PublicId == QaSeedRunner.QaNutriQuestionnaireExternalId, ct))
            .Should().Be(1, "the nutritionist questionnaire template must not be duplicated on re-seed");
        (await db.QuestionnaireQuestions.CountAsync(q => q.Questionnaire.PublicId == QaSeedRunner.QaNutriQuestionnaireExternalId, ct))
            .Should().Be(8, "the 8 template questions must not be duplicated on re-seed");
        (await db.QuestionnaireResponses.CountAsync(r => r.PublicId == QaSeedRunner.QaNutriQuestionnaireResponseExternalId, ct))
            .Should().Be(1, "the submitted response must not be duplicated on re-seed");
        (await db.QuestionnaireAnswers.CountAsync(a => a.Response.PublicId == QaSeedRunner.QaNutriQuestionnaireResponseExternalId, ct))
            .Should().Be(6, "the 6 answers must not be duplicated on re-seed");

        // Total links: 2 trainer↔client (#474) + 1 nutritionist↔client (#720) + 1 qa.trainer↔client3 (#1095)
        // + 1 ended qa.trainer↔former-client (#1101) + 1 qa.coach↔QA Duo (#1311)
        // + 1 live qa.trainer↔QA Ending.
        (await db.ClientProfessionalLinks.CountAsync(ct)).Should().Be(7, "no duplicate links on re-seed");

        var nutritionPlan = await mongo.NutritionPlans
            .Find(p => p.ExternalId == QaSeedRunner.QaNutritionPlanExternalId)
            .FirstOrDefaultAsync(ct);
        nutritionPlan!.QuestionnaireResponseId.Should().Be(QaSeedRunner.QaNutriQuestionnaireResponseExternalId,
            "the nutrition plan link must remain stable across re-seeds");
        nutritionPlan.Version.Should().Be(2, "the link update bumps Version exactly once, not once per seed run");
    }

    /// <summary>
    /// QA_SEED_KIND=minimal must seed users + profiles + trainer↔client link only.
    /// All "rich" fixtures (training plan, foods, recipes, nutrition plan, blobs)
    /// must be absent.
    /// </summary>
    [Fact]
    public async Task SeedAsync_MinimalKind_SkipsRichFixtures()
    {
        var ct = TestContext.Current.CancellationToken;

        // Arrange — switch to minimal mode for this seed run.
        await _factory.ResetAsync();
        Environment.SetEnvironmentVariable("QA_SEED_KIND", "minimal");
        try
        {
            await QaSeedRunner.SeedAsync(_factory.Services);
        }
        finally
        {
            // Clear so other tests run with the default (rich).
            Environment.SetEnvironmentVariable("QA_SEED_KIND", null);
        }

        using var scope = _factory.Services.CreateScope();
        var sp    = scope.ServiceProvider;
        var db    = sp.GetRequiredService<ApplicationDbContext>();
        var mongo = sp.GetRequiredService<IMongoContext>();

        // Users + profiles + link must all exist.
        (await db.Users.CountAsync(u => u.Email == QaSeedRunner.ClientEmail, ct))
            .Should().Be(1);
        (await db.Users.CountAsync(u => u.Email == QaSeedRunner.TrainerEmail, ct))
            .Should().Be(1);
        (await db.Users.CountAsync(u => u.Email == QaSeedRunner.NutriEmail, ct))
            .Should().Be(1);
        // Both trainer↔client links are seeded outside the Rich guard (so both
        // pairs are available for auth in any seed mode). Minimal mode creates
        // both links but no plans/foods/blobs.
        (await db.ClientProfessionalLinks.CountAsync(ct))
            .Should().Be(2, "both trainer↔client links are part of the minimal fixture (#474 adds a second pair)");

        // Rich fixtures must be absent.
        (await mongo.TrainingPlans.CountDocumentsAsync(
            Builders<TrainingPlan>.Filter.Eq(p => p.ExternalId, QaSeedRunner.QaTrainingPlanExternalId),
            cancellationToken: ct))
            .Should().Be(0, "minimal seed skips the training plan");
        (await mongo.TrainingPlans.CountDocumentsAsync(
            Builders<TrainingPlan>.Filter.Eq(p => p.ExternalId, QaSeedRunner.QaPastTrainingPlanExternalId),
            cancellationToken: ct))
            .Should().Be(0, "minimal seed skips the past training plan (#326)");
        (await mongo.TrainingPlans.CountDocumentsAsync(
            Builders<TrainingPlan>.Filter.Eq(p => p.ExternalId, QaSeedRunner.QaMultiSectionPlanExternalId),
            cancellationToken: ct))
            .Should().Be(0, "minimal seed skips the multi-section training plan (#474)");
        (await mongo.SessionExecutions.CountDocumentsAsync(
            Builders<SessionExecution>.Filter.Empty,
            cancellationToken: ct))
            .Should().Be(0, "minimal seed skips all session executions");
        (await mongo.Foods.CountDocumentsAsync(
            Builders<Food>.Filter.Empty,
            cancellationToken: ct))
            .Should().Be(0, "minimal seed skips all foods");
        (await mongo.Recipes.CountDocumentsAsync(
            Builders<Recipe>.Filter.Empty,
            cancellationToken: ct))
            .Should().Be(0, "minimal seed skips all recipes");
        (await mongo.NutritionPlans.CountDocumentsAsync(
            Builders<NutritionPlan>.Filter.Empty,
            cancellationToken: ct))
            .Should().Be(0, "minimal seed skips the nutrition plan");

        // #1095 — the inbox conversation, its messages, the weekly check-ins, and the third
        // client's link are all part of the Rich-only fixture set.
        (await db.Conversations.CountAsync(c => c.PublicId == QaSeedRunner.QaInboxConversationId, ct))
            .Should().Be(0, "minimal seed skips the QA inbox conversation");
        (await db.QuestionnaireResponses.CountAsync(r => r.Kind == QuestionnaireResponseKind.CheckIn, ct))
            .Should().Be(0, "minimal seed skips all weekly check-ins");
        (await db.Users.CountAsync(u => u.Email == QaSeedRunner.Client3Email, ct))
            .Should().Be(0, "minimal seed skips the third client");

        // No blob uploads in minimal mode.
        _factory.BlobStorage.UploadCalls.Should().BeEmpty("minimal seed skips both image blobs");
    }

    /// <summary>
    /// QA_SEED_KIND with a value other than "minimal" or "rich" must throw at
    /// resolve-time — before any database row gets created — so a typo like
    /// `--kind=ritch` doesn't leave behind a half-seeded fixture.
    /// </summary>
    [Fact]
    public async Task SeedAsync_UnknownKind_ThrowsInvalidOperation()
    {
        var ct = TestContext.Current.CancellationToken;

        await _factory.ResetAsync();
        Environment.SetEnvironmentVariable("QA_SEED_KIND", "ritch");
        try
        {
            var act = async () => await QaSeedRunner.SeedAsync(_factory.Services);
            await act.Should().ThrowAsync<InvalidOperationException>()
                .Where(ex => ex.Message.Contains("QA_SEED_KIND"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("QA_SEED_KIND", null);
        }

        // No users should have been created — ResolveKind runs at the very top of
        // SeedAsync, before MigrateAsync + EnsureUserAsync. The typo's blast
        // radius is zero. (Asserts the contract documented on ResolveKind.)
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var userCount = await db.Users.CountAsync(
            u => u.Email == QaSeedRunner.ClientEmail
                 || u.Email == QaSeedRunner.TrainerEmail
                 || u.Email == QaSeedRunner.NutriEmail,
            ct);
        userCount.Should().Be(0, "ResolveKind must run before any database write");
    }

    /// <summary>
    /// The past training plan (#326 fixture) must have:
    ///  - A SessionExecution for the COMPLETED session with Status=Completed.
    ///  - A SessionExecution for the SKIPPED session with Status=Partial.
    ///  - No SessionExecution for the UNTOUCHED session.
    /// This is the minimal assertion the Playwright spec depends on to distinguish
    /// the three past-session states.
    ///
    /// Post-#841 the standalone WorkoutLog/TrainingCompletion documents were unified
    /// into a single SessionExecution aggregate — QaSeedRunner writes SessionExecution
    /// exclusively now, so these assertions query mongo.SessionExecutions.
    /// </summary>
    [Fact]
    public async Task SeedAsync_PastTrainingPlan_HasThreeDistinctSessionStates()
    {
        var ct = TestContext.Current.CancellationToken;

        await _factory.EnsureDefaultSeedAsync();

        using var scope = _factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        // Plan must exist with StartDate in the past.
        var plan = await mongo.TrainingPlans
            .Find(p => p.ExternalId == QaSeedRunner.QaPastTrainingPlanExternalId)
            .FirstOrDefaultAsync(ct);

        plan.Should().NotBeNull("the past training plan must be seeded");
        plan!.StartDate.Should().NotBeNull("past plan must have a StartDate");
        plan.StartDate!.Value.Should().BeBefore(DateTime.UtcNow.AddDays(-7),
            "StartDate must be at least one week in the past");
        plan.Weeks.Should().HaveCount(2, "plan has weeks 1 and 2");
        plan.Weeks.Should().AllSatisfy(w =>
            w.Status.Should().Be(FormUp.Application.Domain.Enums.WeekStatus.Published));
        // TrainerId must be ApplicationUser.Id (not ProfessionalProfile.PublicId) so that
        // GetTrainingPlansEndpoint can scope to the trainer:
        //   filter = filterBuilder.Eq(p => p.TrainerId, Guid.Parse(User.FindFirstValue(AppClaims.UserId)))
        // UserId from AppClaims.UserId is ApplicationUser.Id = TrainerUserId (22222222-...).
        // Using TrainerProfilePublicId (bbbb...) would make this plan invisible to GET /training/plans.
        plan.TrainerId.Should().Be(QaSeedRunner.TrainerUserId,
            "TrainingPlan.TrainerId must be ApplicationUser.Id — GetTrainingPlansEndpoint scopes by " +
            "Guid.Parse(AppClaims.UserId) which is ApplicationUser.Id, not ProfessionalProfile.PublicId");
        // ClientId is ApplicationUser.Id (#840) so that TrainingCompletion.ClientId
        // (written by WorkoutCompletionService as clientProfile.UserId) matches
        // plan.ClientId used in GetTrainingPlanEndpoint's completions fold-in filter.
        plan.ClientId.Should().Be(QaSeedRunner.ClientUserId,
            "TrainingPlan.ClientId must be ApplicationUser.Id — GetTrainingPlanEndpoint queries " +
            "TrainingCompletion by plan.ClientId and WorkoutCompletionService writes " +
            "TrainingCompletion.ClientId = clientProfile.UserId");

        // COMPLETED session — SessionExecution with Status=Completed.
        var completedLog = await mongo.SessionExecutions
            .Find(l => l.ExternalId == QaSeedRunner.QaPastCompletedExecutionId)
            .FirstOrDefaultAsync(ct);

        completedLog.Should().NotBeNull("completed SessionExecution must be seeded");
        completedLog!.Status.Should().Be(SessionExecutionStatus.Completed, "PAST-COMPLETED log must have Status=Completed");
        completedLog.SessionId.Should().Be(QaSeedRunner.QaPastSessionCompletedId);
        completedLog.PlanId.Should().Be(QaSeedRunner.QaPastTrainingPlanExternalId);
        // SessionExecution.ClientId must be ApplicationUser.Id (not ClientProfile.PublicId) so that
        // CompleteWorkoutEndpoint (live client finish) can filter SessionExecutions by
        // ClientId == Guid.Parse(AppClaims.UserId).
        completedLog.ClientId.Should().Be(QaSeedRunner.ClientUserId,
            "SessionExecution.ClientId must be ApplicationUser.Id — CompleteWorkoutEndpoint filters by " +
            "Guid.Parse(AppClaims.UserId) which is ApplicationUser.Id");
        completedLog.Performance.Should().NotBeNull("completed log has live-workout performance data");
        completedLog.Performance!.Workouts.Should().HaveCount(1, "log mirrors the single workout in the session");
        completedLog.Performance.Workouts[0].Exercises.Should().HaveCount(2);
        completedLog.Performance.Workouts[0].Exercises.Should().AllSatisfy(e =>
            e.Sets.Should().NotBeEmpty("completed log has sets on every exercise"));

        // SKIPPED session — SessionExecution with Status=Partial.
        var skippedLog = await mongo.SessionExecutions
            .Find(l => l.ExternalId == QaSeedRunner.QaPastSkippedExecutionId)
            .FirstOrDefaultAsync(ct);

        skippedLog.Should().NotBeNull("skipped SessionExecution must be seeded");
        skippedLog!.Status.Should().Be(SessionExecutionStatus.Partial, "PAST-SKIPPED log must have Status=Partial");
        skippedLog.SessionId.Should().Be(QaSeedRunner.QaPastSessionSkippedId);
        skippedLog.PlanId.Should().Be(QaSeedRunner.QaPastTrainingPlanExternalId);
        // Same id-space requirement as the completed log above.
        skippedLog.ClientId.Should().Be(QaSeedRunner.ClientUserId,
            "SessionExecution.ClientId must be ApplicationUser.Id for the same reason as the completed log");

        // UNTOUCHED session — must have NO SessionExecution.
        var untouchedLogCount = await mongo.SessionExecutions.CountDocumentsAsync(
            Builders<SessionExecution>.Filter.Eq(l => l.SessionId, QaSeedRunner.QaPastSessionUntouchedId),
            cancellationToken: ct);

        untouchedLogCount.Should().Be(0,
            "PAST-UNTOUCHED session must have no SessionExecution so the web classifies it as untouched");
    }

    /// <summary>
    /// The ForTime fixture plan (<see cref="QaSeedRunner.QaTrainingPlanExternalId"/>) must have
    /// TrainerId = TrainerUserId (ApplicationUser.Id = 22222222-...) so that trainer-scoped
    /// endpoints which filter by Guid.Parse(AppClaims.UserId) can see the plan.
    /// Using TrainerProfilePublicId (bbbbbbbb-...) makes the plan invisible to
    /// GET /training/plans and GET /training/plans/{planId}.
    /// </summary>
    [Fact]
    public async Task SeedAsync_ForTimePlan_TrainerIdIsApplicationUserId()
    {
        var ct = TestContext.Current.CancellationToken;

        await _factory.EnsureDefaultSeedAsync();

        using var scope = _factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        var plan = await mongo.TrainingPlans
            .Find(p => p.ExternalId == QaSeedRunner.QaTrainingPlanExternalId)
            .FirstOrDefaultAsync(ct);

        plan.Should().NotBeNull("the ForTime fixture plan must be seeded");
        plan!.TrainerId.Should().Be(QaSeedRunner.TrainerUserId,
            "TrainingPlan.TrainerId must be ApplicationUser.Id (22222222-...) — " +
            "GetTrainingPlansEndpoint and GetTrainingPlanEndpoint scope by " +
            "Guid.Parse(AppClaims.UserId) which is ApplicationUser.Id, not ProfessionalProfile.PublicId (bbbbbbbb-...)");
        plan.ClientId.Should().Be(QaSeedRunner.ClientUserId,
            "TrainingPlan.ClientId must be ApplicationUser.Id (11111111-...) since #840 — " +
            "GetClientPlansEndpoint filters TrainingPlan.ClientId by ApplicationUser.Id and " +
            "TrainingCompletion.ClientId is also keyed on the same identifier");
    }

    /// <summary>
    /// #898 — the main QA plan must carry a <c>StartDate</c> anchored to the current
    /// week's Monday so <c>PlanWindowResolver.ResolveCurrentPlan</c>'s window
    /// <c>[StartDate, StartDate + weeks*7)</c> covers today. Without it the plan is
    /// "unranged" and never matches — and it cannot fall back on the resolver's legacy
    /// single-plan rule because the Past Plan fixture is a second, ranged same-type plan
    /// for the same client. Day coverage {1,2,3} populated / {4,5,6,7} empty is pinned in
    /// the same assertion — the rest days are load-bearing mobile Today rest-day coverage,
    /// not a gap to fill.
    /// </summary>
    [Fact]
    public async Task SeedAsync_ForTimePlan_StartDateAnchoredToCurrentMondayWithDayCoverage()
    {
        var ct = TestContext.Current.CancellationToken;

        await _factory.EnsureDefaultSeedAsync();

        using var scope = _factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        var plan = await mongo.TrainingPlans
            .Find(p => p.ExternalId == QaSeedRunner.QaTrainingPlanExternalId)
            .FirstOrDefaultAsync(ct);

        plan.Should().NotBeNull("the ForTime fixture plan must be seeded");
        plan!.StartDate.Should().NotBeNull(
            "the main QA plan must have a StartDate — otherwise PlanWindowResolver treats it as " +
            "unranged and it never matches today's window");
        plan.StartDate!.Value.DayOfWeek.Should().Be(DayOfWeek.Monday,
            "StartDate must anchor to a Monday so TrainingDay.DayOfWeek=1 maps to that exact date");

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var windowStart = DateOnly.FromDateTime(plan.StartDate.Value);
        var windowEnd = windowStart.AddDays(plan.Weeks.Count * 7);
        today.Should().BeOnOrAfter(windowStart, "today must fall within the plan's resolved window");
        today.Should().BeBefore(windowEnd, "today must fall within the plan's resolved window");

        plan.Weeks.Should().HaveCount(1, "the ForTime fixture is a single-week plan");
        var days = plan.Weeks[0].Days;
        days.Should().HaveCount(7, "BuildTrainingDays/the inline day-map always materialises all 7 weekdays");
        foreach (var dayOfWeek in new[] { 1, 2, 3 })
        {
            days.Single(d => d.DayOfWeek == dayOfWeek).Sessions.Should().NotBeEmpty(
                $"day {dayOfWeek} carries a seeded session");
        }

        foreach (var dayOfWeek in new[] { 4, 5, 6, 7 })
        {
            days.Single(d => d.DayOfWeek == dayOfWeek).Sessions.Should().BeEmpty(
                $"day {dayOfWeek} is an intentional rest day — the only way to exercise the mobile " +
                "Today rest-day empty state; do not fill it with sessions");
        }
    }

    /// <summary>
    /// The nutrition plan seeded by <see cref="QaSeedRunner"/> must have Status=Active
    /// and two Published weeks, each with seven days of three meals (Breakfast, Lunch, Dinner).
    /// </summary>
    [Fact]
    public async Task SeedAsync_NutritionPlan_HasCorrectShape()
    {
        var ct = TestContext.Current.CancellationToken;

        await _factory.EnsureDefaultSeedAsync();

        using var scope = _factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        var plan = await mongo.NutritionPlans
            .Find(p => p.ExternalId == QaSeedRunner.QaNutritionPlanExternalId)
            .FirstOrDefaultAsync(ct);

        plan.Should().NotBeNull("QaSeedRunner must create the QA nutrition plan");
        plan!.ClientId.Should().Be(QaSeedRunner.ClientUserId,
            "NutritionPlan.ClientId must be keyed on ApplicationUser.Id since #840, not ClientProfile.PublicId");
        plan.NutritionistId.Should().Be(QaSeedRunner.NutriUserId,
            "NutritionPlan.NutritionistId must be keyed on ApplicationUser.Id (NutriUserId), not the professional profile PublicId, so nutritionist endpoint filters match AppClaims.UserId");
        plan.Status.Should().Be(FormUp.Application.Domain.Enums.NutritionPlanStatus.Active);

        plan.Weeks.Should().HaveCount(2);
        plan.Weeks.Should().AllSatisfy(week =>
        {
            week.Status.Should().Be(FormUp.Application.Domain.Enums.WeekStatus.Published);
            week.DatePublished.Should().NotBeNull();
            week.Days.Select(d => d.DayOfWeek).Should().Equal(1, 2, 3, 4, 5, 6, 7);
            week.Days.Should().AllSatisfy(d => d.Meals.Should().HaveCount(3, "Breakfast, Lunch, Dinner"));
        });
        plan.Weeks.SelectMany(w => w.Days).SelectMany(d => d.Meals).Select(m => m.MealId)
            .Should().OnlyHaveUniqueItems("meal logs key on MealId");
    }

    /// <summary>
    /// #1303 — qa.client gets identity fields, onboarding answers using only codes the web
    /// <c>clients.values</c> translations know, and derived nutrition targets.
    /// </summary>
    [Fact]
    public async Task SeedAsync_ClientOverview_HasIdentityAndOnboarding()
    {
        var ct = TestContext.Current.CancellationToken;

        await _factory.EnsureDefaultSeedAsync();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var profile = await db.ClientProfiles
            .Include(p => p.OnboardingData!).ThenInclude(o => o.NutritionTargets)
            .FirstAsync(p => p.UserId == QaSeedRunner.ClientUserId, ct);

        profile.DateOfBirth.Should().NotBeNull();
        profile.HeightCm.Should().Be(182m);
        profile.WeightKg.Should().Be(80.5m);

        var onboarding = profile.OnboardingData;
        onboarding.Should().NotBeNull("qa.client has completed onboarding");
        onboarding!.Sex.Should().Be(BiologicalSex.Male);
        onboarding.PrimaryGoal.Should().Be(PrimaryGoal.LoseFat);
        onboarding.TargetWeightKg.Should().Be(76m);
        onboarding.SleepHours.Should().Be(6);
        onboarding.StressLevel.Should().Be(4);

        // Codes must exist in web clients.values (en.json) or the card renders raw keys.
        onboarding.Injuries.Split(',').Should().BeSubsetOf(new[] { "none", "back", "knees", "shoulders" });
        onboarding.Allergies.Split(',').Should().BeSubsetOf(new[] { "none", "lactose", "gluten", "nuts" });
        onboarding.PastBlockers.Split(',').Should().BeSubsetOf(new[] { "none", "time", "motivation", "knowledge", "slow_results" });
        onboarding.PreferredActivities.Split(',').Should().BeSubsetOf(
            new[] { "strength", "cardio", "hiit", "yoga", "cycling", "martial_arts" });
        onboarding.Injuries.Should().NotBeEmpty();
        onboarding.Allergies.Should().NotBeEmpty();
        onboarding.PastBlockers.Should().NotBeEmpty();

        onboarding.NutritionTargets.Should().NotBeNull();
        onboarding.NutritionTargets!.Tdee.Should().BeGreaterThan(0);
        onboarding.NutritionTargets.AdjustedKcal.Should().BeGreaterThan(0);
        onboarding.NutritionTargets.MealDistribution.Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// #1303 — eight weekly measurements with weight, body fat and waist, oldest to newest
    /// trending down, the newest in the past and matching the profile weight.
    /// </summary>
    [Fact]
    public async Task SeedAsync_BodyMeasurements_EightWeeklyRowsTrendingDown()
    {
        var ct = TestContext.Current.CancellationToken;

        await _factory.EnsureDefaultSeedAsync();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var profile = await db.ClientProfiles.FirstAsync(p => p.UserId == QaSeedRunner.ClientUserId, ct);
        var rows = await db.BodyMeasurements
            .Where(m => m.ClientProfileId == profile.Id)
            .OrderBy(m => m.MeasuredAt)
            .ToListAsync(ct);

        rows.Should().HaveCount(8);
        rows.Select(m => m.PublicId).Should().BeEquivalentTo(QaSeedRunner.QaBodyMeasurementIds);
        rows.Should().AllSatisfy(m =>
        {
            m.WeightKg.Should().NotBeNull();
            m.BodyFatPercentage.Should().NotBeNull();
            m.WaistCm.Should().NotBeNull();
        });
        rows.Select(m => m.WeightKg!.Value).Should().BeInDescendingOrder();
        rows.Select(m => m.MeasuredAt.Date).Distinct().Should().HaveCount(8);
        rows.Select(m => (rows[^1].MeasuredAt - m.MeasuredAt).TotalDays).Should()
            .BeEquivalentTo(new[] { 49d, 42d, 35d, 28d, 21d, 14d, 7d, 0d });
        rows[^1].MeasuredAt.Should().BeBefore(DateTime.UtcNow);
        rows[^1].WeightKg.Should().Be(profile.WeightKg, "the profile weight is the latest measurement");
    }

    /// <summary>
    /// #1303 — 14 meal logs on the six days before today against qa.nutri's plan, each day
    /// within the plan window with 3 planned meals; none today.
    /// </summary>
    [Fact]
    public async Task SeedAsync_MealLogs_FourteenAgainstNutritionistPlan()
    {
        var ct = TestContext.Current.CancellationToken;

        await _factory.EnsureDefaultSeedAsync();

        using var scope = _factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        var plan = await mongo.NutritionPlans
            .Find(p => p.ExternalId == QaSeedRunner.QaNutritionPlanExternalId)
            .FirstAsync(ct);
        var logs = await mongo.MealLogs
            .Find(l => l.ClientId == QaSeedRunner.ClientUserId)
            .ToListAsync(ct);

        logs.Should().HaveCount(14);
        logs.Should().AllSatisfy(l =>
        {
            l.PlanId.Should().Be(QaSeedRunner.QaNutritionPlanExternalId);
            l.EatenAt.Should().NotBeNull();
            l.EatenAt!.Value.Should().BeBefore(DateTime.UtcNow);
        });

        var today = DateTime.UtcNow.Date;
        var perDay = logs.GroupBy(l => l.LogDate.Date).ToDictionary(g => g.Key, g => g.Count());
        perDay.Keys.Should().BeEquivalentTo(Enumerable.Range(1, 6).Select(d => today.AddDays(-d)));

        foreach (var day in perDay.Keys)
        {
            FormUp.Application.Domain.Services.PlannedMealCounter
                .GetPlannedMealCountForDate(plan, day)
                .Should().Be(3, $"{day:yyyy-MM-dd} must have 3 planned meals so the row reads N of 3");
        }

        // Every log points at a real planned meal of that day.
        logs.Select(l => l.MealId).Should().OnlyContain(id =>
            plan.Weeks.SelectMany(w => w.Days).SelectMany(d => d.Meals).Any(m => m.MealId == id));
    }

    /// <summary>
    /// #1303 — one personal record on the squat set the completed main-plan execution logged.
    /// </summary>
    [Fact]
    public async Task SeedAsync_PersonalRecord_PointsAtMainExecutionSet()
    {
        var ct = TestContext.Current.CancellationToken;

        await _factory.EnsureDefaultSeedAsync();

        using var scope = _factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        var records = await mongo.PersonalRecords
            .Find(r => r.ClientId == QaSeedRunner.ClientUserId)
            .ToListAsync(ct);

        var record = records.Should().ContainSingle().Subject;
        record.ExternalId.Should().Be(QaSeedRunner.QaPersonalRecordExternalId);
        record.WorkoutLogId.Should().Be(QaSeedRunner.QaMainPlanCompletedExecutionId);
        record.ExerciseExternalId.Should().Be(QaSeedRunner.StandardExercise1Id);
        record.WeightKg.Should().Be(85m);
        record.Reps.Should().Be(8);
        record.AchievedAt.Should().BeBefore(DateTime.UtcNow).And.BeAfter(DateTime.UtcNow.AddDays(-90));
    }

    /// <summary>
    /// #1303 — check-in history around the NextIsoMonday anchor: waiting, answered (reviewed)
    /// and missed slots; the NewCheckIns chip keeps only the #1095 unreviewed row.
    /// </summary>
    [Fact]
    public async Task SeedAsync_CheckInHistory_HasAnsweredMissedAndWaitingAroundAnchor()
    {
        var ct = TestContext.Current.CancellationToken;

        await _factory.EnsureDefaultSeedAsync();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var rows = await db.QuestionnaireResponses
            .Where(r => r.Kind == QuestionnaireResponseKind.CheckIn
                        && r.ClientId == QaSeedRunner.ClientUserId
                        && r.ProfessionalId == QaSeedRunner.TrainerUserId)
            .OrderBy(r => r.CheckInWeekStart)
            .ToListAsync(ct);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var anchor = today.AddDays(-((int)today.DayOfWeek == 0 ? 6 : (int)today.DayOfWeek - 1) + 7);

        rows.Should().HaveCount(7, "two #1095 rows plus waiting, three answered and one missed");
        rows.Should().AllSatisfy(r => r.CheckInWeekStart!.Value.DayOfWeek.Should().Be(DayOfWeek.Monday));
        rows.Select(r => r.CheckInWeekStart).Should().OnlyHaveUniqueItems();

        var waiting = rows.Single(r => r.PublicId == QaSeedRunner.QaCheckInWaitingId);
        waiting.CheckInWeekStart.Should().Be(anchor);
        waiting.Status.Should().Be(QuestionnaireResponseStatus.Pending);
        waiting.CheckInDueAtUtc.Should().BeAfter(DateTime.UtcNow);

        var answeredIds = new[]
        {
            QaSeedRunner.QaCheckInAnsweredLastWeekId, QaSeedRunner.QaCheckInAnsweredWeek4Id, QaSeedRunner.QaCheckInAnsweredWeek6Id,
        };
        rows.Where(r => answeredIds.Contains(r.PublicId)).Should().HaveCount(3).And.AllSatisfy(r =>
        {
            r.Status.Should().Be(QuestionnaireResponseStatus.Submitted);
            r.ReviewedByCoachAtUtc.Should().NotBeNull("new answered rows must not feed the inbox NewCheckIns chip");
        });

        var missed = rows.Single(r => r.PublicId == QaSeedRunner.QaCheckInMissedWeek5Id);
        missed.Status.Should().Be(QuestionnaireResponseStatus.Pending);
        missed.CheckInDueAtUtc.Should().BeBefore(DateTime.UtcNow);
        missed.CheckInWeekStart.Should().Be(anchor.AddDays(-35));

        rows.Where(r => r.Status == QuestionnaireResponseStatus.Submitted && r.ReviewedByCoachAtUtc == null)
            .Select(r => r.PublicId).Should().Equal(QaSeedRunner.QaWeeklyCheckInRespondedUnreviewedId);
        rows.Count(r => r.Status == QuestionnaireResponseStatus.Submitted).Should().Be(4, "answered of total reads 4 of 6");
        rows.Count(r => r.Status == QuestionnaireResponseStatus.Pending && r.CheckInDueAtUtc < DateTime.UtcNow).Should().Be(2);
    }

    [Fact]
    public async Task SeedAsync_CoachProfiles_EachGetTheDefaultWeeklyCheckInForm()
    {
        await _factory.EnsureDefaultSeedAsync();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var coachIds = new[] { QaSeedRunner.TrainerUserId, QaSeedRunner.NutriUserId };

        var forms = await db.Questionnaires
            .Include(q => q.Questions)
            .Where(q => q.Type == QuestionnaireType.CheckIn && coachIds.Contains(q.ProfessionalId))
            .ToListAsync(TestContext.Current.CancellationToken);

        forms.Select(f => f.ProfessionalId).Should().BeEquivalentTo(coachIds, "one form per coach, never a duplicate");
        forms.Should().AllSatisfy(f => f.Questions.Should().HaveCount(2));
    }

    [Fact]
    public async Task SeedAsync_EveryCoach_IsActiveOnTheStudioPlanOnItsOpenPrice()
    {
        await _factory.EnsureDefaultSeedAsync();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var coachIds = new[]
        {
            QaSeedRunner.TrainerUserId, QaSeedRunner.NutriUserId, QaSeedRunner.Trainer2UserId, QaSeedRunner.CoachUserId,
        };

        var subscriptions = await db.CoachSubscriptions
            .AsNoTracking()
            .Where(cs => coachIds.Contains(cs.ProfessionalProfile.UserId))
            .Select(cs => new
            {
                cs.ProfessionalProfile.UserId,
                cs.Status,
                PlanCode = cs.SubscriptionPlan.Code,
                PriceIsOpen = cs.SubscriptionPlanPrice.ValidTo == null
                    && cs.SubscriptionPlanPrice.SubscriptionPlanId == cs.SubscriptionPlanId,
            })
            .ToListAsync(TestContext.Current.CancellationToken);

        subscriptions.Select(s => s.UserId).Should().BeEquivalentTo(coachIds, "one subscription per QA coach, never a duplicate");
        subscriptions.Should().AllSatisfy(s =>
        {
            s.PlanCode.Should().Be("studio");
            s.Status.Should().Be(SubscriptionStatus.Active);
            s.PriceIsOpen.Should().BeTrue();
        });
    }

    private static DateTime ThisMonday()
    {
        var today = DateTime.UtcNow.Date;
        return today.AddDays(-(((int)today.DayOfWeek == 0 ? 7 : (int)today.DayOfWeek) - 1));
    }

    /// <summary>
    /// The seeded execution of <see cref="QaSeedRunner.QaSessionId"/> dated this Monday. On a Thursday the
    /// main plan's completed execution (3 days ago) is that row and the Monday insert is skipped, so it
    /// is matched by client + session + date rather than by the Monday execution's own id.
    /// </summary>
    private static Task<List<SessionExecution>> FindMondayExecutionsAsync(IMongoContext mongo, CancellationToken ct)
    {
        var monday = ThisMonday();
        return mongo.SessionExecutions
            .Find(e => e.ClientId == QaSeedRunner.ClientUserId && e.SessionId == QaSeedRunner.QaSessionId && e.Date == monday)
            .ToListAsync(ct);
    }

    /// <summary>
    /// #1303 — one completed execution dated this Monday for a session of the active plan week.
    /// </summary>
    [Fact]
    public async Task SeedAsync_MondaySessionExecution_IsCompletedAndInThePlanWeek()
    {
        var ct = TestContext.Current.CancellationToken;

        await _factory.EnsureDefaultSeedAsync();

        using var scope = _factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        var execution = (await FindMondayExecutionsAsync(mongo, ct)).Single();
        var monday = ThisMonday();

        execution.Status.Should().Be(SessionExecutionStatus.Completed);
        execution.ClientId.Should().Be(QaSeedRunner.ClientUserId);
        execution.PlanId.Should().Be(QaSeedRunner.QaTrainingPlanExternalId);
        execution.Date.Should().Be(monday);

        var plan = await mongo.TrainingPlans
            .Find(p => p.ExternalId == QaSeedRunner.QaTrainingPlanExternalId)
            .FirstAsync(ct);
        plan.Weeks.SelectMany(w => w.Days).SelectMany(d => d.Sessions).Select(s => s.SessionId)
            .Should().Contain(execution.SessionId!.Value);

        // Unique (clientId, sessionId, date): no other execution shares this session and day.
        (await mongo.SessionExecutions.CountDocumentsAsync(
                e => e.ClientId == QaSeedRunner.ClientUserId && e.SessionId == execution.SessionId && e.Date == monday,
                cancellationToken: ct))
            .Should().Be(1);
    }

    /// <summary>
    /// The Monday execution's session must be scheduled on Monday (day 1) in the main plan; compliance
    /// only counts an execution dated on the day its session is scheduled.
    /// </summary>
    [Fact]
    public async Task SeedAsync_MondaySessionExecution_SessionIsScheduledOnMondayInMainPlan()
    {
        var ct = TestContext.Current.CancellationToken;

        await _factory.EnsureDefaultSeedAsync();

        using var scope = _factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        var execution = (await FindMondayExecutionsAsync(mongo, ct)).Single();
        var plan = await mongo.TrainingPlans
            .Find(p => p.ExternalId == QaSeedRunner.QaTrainingPlanExternalId)
            .FirstAsync(ct);

        var mondaySessionIds = plan.Weeks
            .SelectMany(w => w.Days)
            .Where(d => d.DayOfWeek == 1)
            .SelectMany(d => d.Sessions)
            .Select(s => s.SessionId);

        mondaySessionIds.Should().Contain(execution.SessionId!.Value);
    }

    /// <summary>
    /// #1303 — nutrition data stays on qa.nutri: qa.trainer gets no nutrition capability, no
    /// extra link and no plan authored for qa.client.
    /// </summary>
    [Fact]
    public async Task SeedAsync_ClientOverview_KeepsTrainerWithoutNutritionAccess()
    {
        var ct = TestContext.Current.CancellationToken;

        await _factory.EnsureDefaultSeedAsync();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        var trainerLink = await db.ClientProfessionalLinks.SingleAsync(
            l => l.ClientProfile.UserId == QaSeedRunner.ClientUserId && l.ProfessionalProfile.UserId == QaSeedRunner.TrainerUserId, ct);
        trainerLink.CanViewNutritionPlans.Should().BeFalse();

        (await mongo.NutritionPlans.CountDocumentsAsync(
                p => p.ClientId == QaSeedRunner.ClientUserId && p.NutritionistId == QaSeedRunner.TrainerUserId,
                cancellationToken: ct))
            .Should().Be(0);
        (await mongo.NutritionPlans.CountDocumentsAsync(p => p.ClientId == QaSeedRunner.ClientUserId, cancellationToken: ct))
            .Should().Be(1);
    }

    /// <summary>
    /// #1303 — the minimal kind seeds none of the Overview fixtures.
    /// </summary>
    [Fact]
    public async Task SeedAsync_MinimalKind_SkipsOverviewFixtures()
    {
        var ct = TestContext.Current.CancellationToken;

        await _factory.ResetAsync();
        Environment.SetEnvironmentVariable("QA_SEED_KIND", "minimal");
        try
        {
            await QaSeedRunner.SeedAsync(_factory.Services);
        }
        finally
        {
            Environment.SetEnvironmentVariable("QA_SEED_KIND", null);
        }

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        var profile = await db.ClientProfiles.FirstAsync(p => p.UserId == QaSeedRunner.ClientUserId, ct);
        profile.DateOfBirth.Should().BeNull();
        (await db.BodyMeasurements.CountAsync(ct)).Should().Be(0);
        (await db.ClientOnboardingData.CountAsync(ct)).Should().Be(0);
        (await db.QuestionnaireResponses.CountAsync(r => r.Kind == QuestionnaireResponseKind.CheckIn, ct)).Should().Be(0);
        (await mongo.MealLogs.CountDocumentsAsync(Builders<MealLog>.Filter.Empty, cancellationToken: ct)).Should().Be(0);
        (await mongo.PersonalRecords.CountDocumentsAsync(Builders<PersonalRecord>.Filter.Empty, cancellationToken: ct)).Should().Be(0);
        (await mongo.SessionExecutions.CountDocumentsAsync(Builders<SessionExecution>.Filter.Empty, cancellationToken: ct)).Should().Be(0);
        (await db.Users.CountAsync(u => u.Email == QaSeedRunner.CoachEmail || u.Email == QaSeedRunner.ClientDuoEmail, ct))
            .Should().Be(0, "the both-roles fixture is Rich-only");
    }

    /// <summary>
    /// #1311 — qa.coach holds both professional roles and no Client role.
    /// </summary>
    [Fact]
    public async Task SeedAsync_Duo_CoachHoldsTrainerAndNutritionistRoles()
    {
        await ResetAndSeedAsync();

        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<FormUp.Application.Domain.Entities.ApplicationUser>>();

        var coach = await userManager.FindByEmailAsync(QaSeedRunner.CoachEmail);
        coach.Should().NotBeNull();
        coach!.Id.Should().Be(QaSeedRunner.CoachUserId);
        (await userManager.GetRolesAsync(coach)).Should().BeEquivalentTo(
            new[] { UserRole.Trainer.ToString(), UserRole.Nutritionist.ToString() });

        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var profile = await db.ProfessionalProfiles.SingleAsync(
            p => p.UserId == QaSeedRunner.CoachUserId, TestContext.Current.CancellationToken);
        profile.PublicId.Should().Be(QaSeedRunner.CoachProfilePublicId);
        profile.TrainerRoleRemovedAt.Should().BeNull();
        profile.NutritionistRoleRemovedAt.Should().BeNull();

        var duo = await userManager.FindByEmailAsync(QaSeedRunner.ClientDuoEmail);
        duo.Should().NotBeNull();
        duo!.Id.Should().Be(QaSeedRunner.ClientDuoUserId);
        (await userManager.GetRolesAsync(duo)).Should().BeEquivalentTo(new[] { UserRole.Client.ToString() });
    }

    /// <summary>
    /// #1311 — one link, shaped like AcceptClientInviteEndpoint writes it for a both-roles coach,
    /// and no link between qa.coach and qa.client (one active coach per profession).
    /// </summary>
    [Fact]
    public async Task SeedAsync_Duo_SingleBothFlagsLink_AndNoLinkToQaClient()
    {
        var ct = TestContext.Current.CancellationToken;

        await ResetAndSeedAsync();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var coachLinks = await db.ClientProfessionalLinks
            .Where(l => l.ProfessionalProfile.UserId == QaSeedRunner.CoachUserId)
            .Select(l => new
            {
                ClientUserId = l.ClientProfile.UserId,
                l.ProfessionalRole,
                IsActive = l.ConnectionState != ConnectionState.Ended,
                l.CanViewNutritionPlans,
                l.CanViewTrainingPlans,
            })
            .ToListAsync(ct);

        var link = coachLinks.Should().ContainSingle().Subject;
        link.ClientUserId.Should().Be(QaSeedRunner.ClientDuoUserId);
        link.ProfessionalRole.Should().Be(UserRole.Nutritionist);
        link.IsActive.Should().BeTrue();
        link.CanViewNutritionPlans.Should().BeTrue();
        link.CanViewTrainingPlans.Should().BeTrue();

        (await db.ClientProfessionalLinks.CountAsync(
                l => l.ProfessionalProfile.UserId == QaSeedRunner.CoachUserId && l.ClientProfile.UserId == QaSeedRunner.ClientUserId, ct))
            .Should().Be(0);
        (await db.ClientProfessionalLinks.CountAsync(l => l.ClientProfile.UserId == QaSeedRunner.ClientDuoUserId, ct))
            .Should().Be(1, "QA Duo has exactly one coach");

        // qa.client keeps exactly its trainer and nutritionist links.
        (await db.ClientProfessionalLinks.CountAsync(l => l.ClientProfile.UserId == QaSeedRunner.ClientUserId, ct))
            .Should().Be(2);
    }

    /// <summary>
    /// #1311 — both plans are authored by qa.coach for QA Duo, keyed on ApplicationUser ids and
    /// windowed so they are current today.
    /// </summary>
    [Fact]
    public async Task SeedAsync_Duo_PlansAuthoredByCoachForDuo()
    {
        var ct = TestContext.Current.CancellationToken;

        await ResetAndSeedAsync();

        using var scope = _factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        var nutrition = await mongo.NutritionPlans
            .Find(p => p.ExternalId == QaSeedRunner.QaDuoNutritionPlanExternalId)
            .FirstAsync(ct);
        nutrition.ClientId.Should().Be(QaSeedRunner.ClientDuoUserId);
        nutrition.NutritionistId.Should().Be(QaSeedRunner.CoachUserId);
        nutrition.Status.Should().Be(NutritionPlanStatus.Active);
        nutrition.StartDate.Should().NotBeNull();
        nutrition.StartDate!.Value.Date.Should().BeOnOrBefore(DateTime.UtcNow.Date);
        nutrition.StartDate.Value.Date.AddDays(nutrition.Weeks.Count * 7).Should().BeAfter(DateTime.UtcNow.Date);

        var training = await mongo.TrainingPlans
            .Find(p => p.ExternalId == QaSeedRunner.QaDuoTrainingPlanExternalId)
            .FirstAsync(ct);
        training.ClientId.Should().Be(QaSeedRunner.ClientDuoUserId);
        training.TrainerId.Should().Be(QaSeedRunner.CoachUserId);
        training.Status.Should().Be(TrainingPlanStatus.Active);
        training.StartDate!.Value.Date.Should().BeOnOrBefore(DateTime.UtcNow.Date);
        training.StartDate.Value.Date.AddDays(training.Weeks.Count * 7).Should().BeAfter(DateTime.UtcNow.Date);
        training.Weeks.Should().OnlyContain(w => w.Status == WeekStatus.Published);

        // qa.client's plans are untouched by the new coach.
        (await mongo.NutritionPlans.CountDocumentsAsync(
                p => p.NutritionistId == QaSeedRunner.CoachUserId && p.ClientId == QaSeedRunner.ClientUserId, cancellationToken: ct))
            .Should().Be(0);
        (await mongo.TrainingPlans.CountDocumentsAsync(
                p => p.TrainerId == QaSeedRunner.CoachUserId && p.ClientId == QaSeedRunner.ClientUserId, cancellationToken: ct))
            .Should().Be(0);
    }

    /// <summary>
    /// #1311 — the Duo training plan schedules three sessions this week; the completed one sits
    /// on today's weekday and its execution and personal record are dated today, so "sessions
    /// this week" and "PRs this month" are non-zero on any reset day, including the 1st.
    /// </summary>
    [Fact]
    public async Task SeedAsync_Duo_CompletedSessionAndPersonalRecordAreDatedToday()
    {
        var ct = TestContext.Current.CancellationToken;

        await ResetAndSeedAsync();

        using var scope = _factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        var plan = await mongo.TrainingPlans
            .Find(p => p.ExternalId == QaSeedRunner.QaDuoTrainingPlanExternalId)
            .FirstAsync(ct);
        var sessionsByDay = plan.Weeks.Single().Days
            .SelectMany(d => d.Sessions.Select(s => (d.DayOfWeek, s.SessionId)))
            .ToList();
        sessionsByDay.Should().HaveCount(3);

        var today = DateTime.UtcNow.Date;
        var todayDayOfWeek = (int)today.DayOfWeek == 0 ? 7 : (int)today.DayOfWeek;
        sessionsByDay.Should().Contain((todayDayOfWeek, QaSeedRunner.QaDuoSessionAId));

        var allExercises = plan.Weeks.SelectMany(w => w.Days).SelectMany(d => d.Sessions).SelectMany(s => s.AllExercises).ToList();
        allExercises.Should().OnlyContain(e => e.ExerciseId != Guid.Empty);
        allExercises.Select(e => e.ExerciseId).Should().OnlyHaveUniqueItems();

        var execution = await mongo.SessionExecutions
            .Find(e => e.ExternalId == QaSeedRunner.QaDuoSessionExecutionId)
            .FirstAsync(ct);
        execution.ClientId.Should().Be(QaSeedRunner.ClientDuoUserId);
        execution.PlanId.Should().Be(QaSeedRunner.QaDuoTrainingPlanExternalId);
        execution.SessionId.Should().Be(QaSeedRunner.QaDuoSessionAId);
        execution.Status.Should().Be(SessionExecutionStatus.Completed);
        execution.Date.Should().Be(today);
        execution.DateUpdated.Should().BeOnOrBefore(DateTime.UtcNow);

        var record = await mongo.PersonalRecords
            .Find(r => r.ExternalId == QaSeedRunner.QaDuoPersonalRecordExternalId)
            .FirstAsync(ct);
        record.ClientId.Should().Be(QaSeedRunner.ClientDuoUserId);
        record.WorkoutLogId.Should().Be(QaSeedRunner.QaDuoSessionExecutionId);
        record.AchievedAt.Date.Should().Be(today);
        record.AchievedAt.Should().BeOnOrBefore(DateTime.UtcNow);
    }

    /// <summary>
    /// #1311 — eight weekly measurements, meal logs on the six days before today and the check-in
    /// history for QA Duo, all attributed to qa.coach where an author applies.
    /// </summary>
    [Fact]
    public async Task SeedAsync_Duo_HasMeasurementsMealLogsAndCheckIns()
    {
        var ct = TestContext.Current.CancellationToken;

        await ResetAndSeedAsync();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        var profile = await db.ClientProfiles
            .Include(p => p.OnboardingData!).ThenInclude(o => o.NutritionTargets)
            .FirstAsync(p => p.UserId == QaSeedRunner.ClientDuoUserId, ct);
        profile.DateOfBirth.Should().NotBeNull();
        profile.OnboardingData.Should().NotBeNull();
        profile.OnboardingData!.NutritionTargets.Should().NotBeNull();

        var measurements = await db.BodyMeasurements
            .Where(m => m.ClientProfileId == profile.Id)
            .OrderBy(m => m.MeasuredAt)
            .ToListAsync(ct);
        measurements.Should().HaveCount(8);
        measurements.Select(m => m.PublicId).Should().BeEquivalentTo(QaSeedRunner.QaDuoBodyMeasurementIds);
        measurements.Select(m => m.WeightKg!.Value).Should().BeInDescendingOrder();
        measurements[^1].MeasuredAt.Should().BeBefore(DateTime.UtcNow);
        measurements[^1].WeightKg.Should().Be(profile.WeightKg);

        var plan = await mongo.NutritionPlans
            .Find(p => p.ExternalId == QaSeedRunner.QaDuoNutritionPlanExternalId)
            .FirstAsync(ct);
        var logs = await mongo.MealLogs.Find(l => l.ClientId == QaSeedRunner.ClientDuoUserId).ToListAsync(ct);
        logs.Should().NotBeEmpty();
        logs.Should().AllSatisfy(l => l.PlanId.Should().Be(QaSeedRunner.QaDuoNutritionPlanExternalId));
        logs.Select(l => l.LogDate.Date).Should().OnlyContain(day => day < DateTime.UtcNow.Date, "today stays empty");
        logs.Select(l => l.MealId).Should().OnlyContain(id =>
            plan.Weeks.SelectMany(w => w.Days).SelectMany(d => d.Meals).Any(m => m.MealId == id));

        var checkIns = await db.QuestionnaireResponses
            .Where(r => r.Kind == QuestionnaireResponseKind.CheckIn && r.ClientId == QaSeedRunner.ClientDuoUserId)
            .ToListAsync(ct);
        checkIns.Should().HaveCount(5);
        checkIns.Should().AllSatisfy(r => r.ProfessionalId.Should().Be(QaSeedRunner.CoachUserId));
        checkIns.Select(r => r.PublicId).Should().BeEquivalentTo(QaSeedRunner.QaDuoCheckInIds);
        checkIns.Where(r => r.Status == QuestionnaireResponseStatus.Submitted).Should().HaveCount(3)
            .And.AllSatisfy(r => r.ReviewedByCoachAtUtc.Should().NotBeNull());
        checkIns.Count(r => r.Status == QuestionnaireResponseStatus.Pending && r.CheckInDueAtUtc < DateTime.UtcNow).Should().Be(1);
        checkIns.Count(r => r.Status == QuestionnaireResponseStatus.Pending && r.CheckInDueAtUtc > DateTime.UtcNow).Should().Be(1);
    }

    /// <summary>
    /// #1094 — the QA nutrition plan must carry a <c>StartDate</c> anchored to the current
    /// week's Monday so <c>PlanWindowResolver</c>'s window <c>[StartDate, StartDate +
    /// weeks*7)</c> covers today, and a <c>GlobalSettings</c> block whose three gram values
    /// sum to <c>DailyKcal</c> exactly — the AC 7 macro card relies on
    /// GET /nutrition/plans/{id} returning non-null globalSettings for the client's active
    /// nutrition plan.
    /// </summary>
    [Fact]
    public async Task SeedAsync_NutritionPlan_StartDateAnchoredToCurrentMondayWithGlobalSettings()
    {
        var ct = TestContext.Current.CancellationToken;

        await _factory.EnsureDefaultSeedAsync();

        using var scope = _factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        var plan = await mongo.NutritionPlans
            .Find(p => p.ExternalId == QaSeedRunner.QaNutritionPlanExternalId)
            .FirstOrDefaultAsync(ct);

        plan.Should().NotBeNull("the QA nutrition plan must be seeded");
        plan!.StartDate.Should().NotBeNull(
            "the QA nutrition plan must have a StartDate — otherwise PlanWindowResolver treats it as " +
            "unranged and it never matches today's window");
        plan.StartDate!.Value.DayOfWeek.Should().Be(DayOfWeek.Monday,
            "StartDate must anchor to a Monday so PlanDay.DayOfWeek=1 maps to that exact date");

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var windowStart = DateOnly.FromDateTime(plan.StartDate.Value);
        var windowEnd = windowStart.AddDays(plan.Weeks.Count * 7);
        today.Should().BeOnOrAfter(windowStart, "today must fall within the plan's resolved window");
        today.Should().BeBefore(windowEnd, "today must fall within the plan's resolved window");

        plan.GlobalSettings.Should().NotBeNull("the AC 7 macro card needs a non-null globalSettings block");
        plan.GlobalSettings!.DailyKcal.Should().Be(2500);
        plan.GlobalSettings.ProteinGrams.Should().Be(160);
        plan.GlobalSettings.CarbsGrams.Should().Be(240);
        plan.GlobalSettings.FatGrams.Should().Be(100);

        var kcalFromGrams = 4 * plan.GlobalSettings.ProteinGrams!.Value
            + 4 * plan.GlobalSettings.CarbsGrams!.Value
            + 9 * plan.GlobalSettings.FatGrams!.Value;
        kcalFromGrams.Should().Be(plan.GlobalSettings.DailyKcal!.Value,
            "the three gram values must sum to DailyKcal exactly so the three rounded macro " +
            "percentages sum to 100 rather than drifting to 99 or 101");
    }

    /// <summary>
    /// The main QA training plan (dddddddd-...) Standard section must have prescribed sets
    /// on both exercises so the planned-vs-actual WorkoutLog has concrete prescription values
    /// to compare against.
    /// </summary>
    [Fact]
    public async Task SeedAsync_MainPlan_StandardSectionHasPrescribedSets()
    {
        var ct = TestContext.Current.CancellationToken;

        await _factory.EnsureDefaultSeedAsync();

        using var scope = _factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        var plan = await mongo.TrainingPlans
            .Find(p => p.ExternalId == QaSeedRunner.QaTrainingPlanExternalId)
            .FirstOrDefaultAsync(ct);

        plan.Should().NotBeNull("main training plan must be seeded");

        var session = plan!.Weeks[0].Days.SelectMany(d => d.Sessions).Single(s => s.SessionId == QaSeedRunner.QaSessionId);
        var standardSection = session.Workouts.Single(s => s.WorkoutId == QaSeedRunner.StandardSectionId);

        standardSection.Exercises.Should().HaveCount(2, "Standard section has two exercises");

        var squat = standardSection.Exercises.Single(e => e.ExerciseExternalId == QaSeedRunner.StandardExercise1Id);
        squat.Sets.Should().HaveCount(2, "QA Squat has 2 prescribed sets");
        squat.Sets.Should().AllSatisfy(s =>
        {
            s.Reps.Should().Be(10, "prescribed 10 reps");
            s.WeightKg.Should().Be(80m, "prescribed 80 kg");
        });

        var deadlift = standardSection.Exercises.Single(e => e.ExerciseExternalId == QaSeedRunner.StandardExercise2Id);
        deadlift.Sets.Should().HaveCount(2, "QA Deadlift has 2 prescribed sets");
        deadlift.Sets.Should().AllSatisfy(s =>
        {
            s.Reps.Should().Be(5, "prescribed 5 reps");
            s.WeightKg.Should().Be(100m, "prescribed 100 kg");
        });
    }

    /// <summary>
    /// Regression guard for the QA-findings bug on #857: every seeded <see cref="SessionExercise"/>
    /// across every training plan must carry a non-empty <see cref="SessionExercise.ExerciseId"/>.
    /// Before the fix, all 13 seeded instances persisted <c>Guid.Empty</c>, which
    /// <c>MarkExerciseCompleteValidator</c> rejects — making per-exercise completion unreachable
    /// on the QA fixture, and every exercise in a session shared one empty instance id (the exact
    /// ambiguity the instance id exists to remove).
    /// </summary>
    [Fact]
    public async Task SeedAsync_AllPlans_EverySessionExerciseHasNonEmptyExerciseId()
    {
        var ct = TestContext.Current.CancellationToken;

        await _factory.EnsureDefaultSeedAsync();

        using var scope = _factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        var planIds = new[]
        {
            QaSeedRunner.QaTrainingPlanExternalId,
            QaSeedRunner.QaPastTrainingPlanExternalId,
            QaSeedRunner.QaMultiSectionPlanExternalId,
            QaSeedRunner.QaDuoTrainingPlanExternalId,
        };

        foreach (var planId in planIds)
        {
            var plan = await mongo.TrainingPlans
                .Find(p => p.ExternalId == planId)
                .FirstOrDefaultAsync(ct);

            plan.Should().NotBeNull($"plan {planId} must be seeded");

            var allExercises = plan!.Weeks
                .SelectMany(w => w.Days)
                .SelectMany(d => d.Sessions)
                .SelectMany(s => s.AllExercises)
                .ToList();

            allExercises.Should().NotBeEmpty($"plan {planId} must seed at least one exercise");
            allExercises.Should().OnlyContain(e => e.ExerciseId != Guid.Empty,
                $"every SessionExercise in plan {planId} must carry a non-empty instance ExerciseId — " +
                "a Guid.Empty instance id is rejected by MarkExerciseCompleteValidator and makes per-exercise " +
                "completion unreachable on this fixture");

            var instanceIds = allExercises.Select(e => e.ExerciseId).ToList();
            instanceIds.Should().OnlyHaveUniqueItems(
                $"every exercise instance in plan {planId} must have a distinct ExerciseId, even when two " +
                "instances share the same catalog ExerciseExternalId");
        }
    }

    /// <summary>
    /// #857 QA fixture extension: the main QA plan must also seed a session with ONLY standalone
    /// exercises (no workouts at all) — one of the two new tree shapes the training-plan
    /// restructure makes possible, which the pre-fix fixture could not represent.
    /// </summary>
    [Fact]
    public async Task SeedAsync_MainPlan_HasStandaloneOnlySession()
    {
        var ct = TestContext.Current.CancellationToken;

        await _factory.EnsureDefaultSeedAsync();

        using var scope = _factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        var plan = await mongo.TrainingPlans
            .Find(p => p.ExternalId == QaSeedRunner.QaTrainingPlanExternalId)
            .FirstOrDefaultAsync(ct);

        plan.Should().NotBeNull();

        var session = plan!.Weeks[0].Days
            .SelectMany(d => d.Sessions)
            .Single(s => s.SessionId == QaSeedRunner.QaStandaloneOnlySessionId);

        session.Workouts.Should().BeEmpty("this session must carry ONLY standalone exercises");
        session.StandaloneExercises.Should().ContainSingle();
        var exercise = session.StandaloneExercises[0];
        exercise.ExerciseId.Should().Be(QaSeedRunner.QaStandaloneOnlyInstanceId);
        exercise.ExerciseExternalId.Should().Be(QaSeedRunner.QaStandaloneOnlyExerciseId);
        session.AllExercises.Should().ContainSingle(
            "the computed flat AllExercises view must still surface the standalone-only exercise");
    }

    /// <summary>
    /// #857 QA fixture extension: the main QA plan must also seed a session where the SAME
    /// catalog exercise appears BOTH standalone on the session AND nested inside one of that
    /// session's workouts, with distinct instance ExerciseId values — the pairing standalone
    /// exercises newly make reachable that the pre-fix fixture could not represent.
    /// </summary>
    [Fact]
    public async Task SeedAsync_MainPlan_HasStandaloneAndNestedDuplicateExerciseSession()
    {
        var ct = TestContext.Current.CancellationToken;

        await _factory.EnsureDefaultSeedAsync();

        using var scope = _factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        var plan = await mongo.TrainingPlans
            .Find(p => p.ExternalId == QaSeedRunner.QaTrainingPlanExternalId)
            .FirstOrDefaultAsync(ct);

        plan.Should().NotBeNull();

        var session = plan!.Weeks[0].Days
            .SelectMany(d => d.Sessions)
            .Single(s => s.SessionId == QaSeedRunner.QaDualPlacementSessionId);

        session.StandaloneExercises.Should().ContainSingle();
        var standaloneExercise = session.StandaloneExercises[0];
        standaloneExercise.ExerciseId.Should().Be(QaSeedRunner.QaDualPlacementStandaloneInstanceId);
        standaloneExercise.ExerciseExternalId.Should().Be(QaSeedRunner.QaDualPlacementExerciseId);

        var workout = session.Workouts.Single(w => w.WorkoutId == QaSeedRunner.QaDualPlacementWorkoutId);
        var nestedExercise = workout.Exercises.Should().ContainSingle().Subject;
        nestedExercise.ExerciseId.Should().Be(QaSeedRunner.QaDualPlacementNestedInstanceId);
        nestedExercise.ExerciseExternalId.Should().Be(QaSeedRunner.QaDualPlacementExerciseId);

        standaloneExercise.ExerciseId.Should().NotBe(nestedExercise.ExerciseId,
            "the two occurrences of the same catalog exercise must have distinct instance ids");
        session.AllExercises.Should().HaveCount(2,
            "the computed flat AllExercises view must union the standalone exercise with the nested one");
    }

    /// <summary>
    /// #879 — both placements of the dual-placement fixture must carry prescribed sets so the
    /// planned-vs-actual view has concrete prescription values, and so per-instance completion
    /// (see <see cref="SeedAsync_DualPlacementCompletion_MarksOnlyStandaloneInstanceComplete"/>)
    /// is observable rather than falling back to the pre-#877 set-based-only completion check.
    /// </summary>
    [Fact]
    public async Task SeedAsync_DualPlacementSession_BothInstancesHavePrescribedSets()
    {
        var ct = TestContext.Current.CancellationToken;

        await _factory.EnsureDefaultSeedAsync();

        using var scope = _factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        var plan = await mongo.TrainingPlans
            .Find(p => p.ExternalId == QaSeedRunner.QaTrainingPlanExternalId)
            .FirstOrDefaultAsync(ct);

        plan.Should().NotBeNull();

        var session = plan!.Weeks[0].Days
            .SelectMany(d => d.Sessions)
            .Single(s => s.SessionId == QaSeedRunner.QaDualPlacementSessionId);

        var standaloneExercise = session.StandaloneExercises.Single(
            e => e.ExerciseId == QaSeedRunner.QaDualPlacementStandaloneInstanceId);
        standaloneExercise.Sets.Should().NotBeEmpty("the standalone placement must carry prescribed sets (#879)");

        var workout = session.Workouts.Single(w => w.WorkoutId == QaSeedRunner.QaDualPlacementWorkoutId);
        var nestedExercise = workout.Exercises.Single(e => e.ExerciseId == QaSeedRunner.QaDualPlacementNestedInstanceId);
        nestedExercise.Sets.Should().NotBeEmpty("the nested placement must carry prescribed sets (#879)");
    }

    /// <summary>
    /// #879 — a SessionExecution for the dual-placement fixture marks ONLY the standalone
    /// instance complete, leaving the nested instance (sharing the same catalog exercise)
    /// incomplete. This is what makes GetFullTrainingPlanEndpoint's per-instance completion
    /// lookup (CompletedExerciseInstanceIds) observably distinguish the two placements.
    /// </summary>
    [Fact]
    public async Task SeedAsync_DualPlacementCompletion_MarksOnlyStandaloneInstanceComplete()
    {
        var ct = TestContext.Current.CancellationToken;

        await _factory.EnsureDefaultSeedAsync();

        using var scope = _factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        var execution = await mongo.SessionExecutions
            .Find(e => e.ExternalId == QaSeedRunner.QaDualPlacementSessionExecutionId)
            .FirstOrDefaultAsync(ct);

        execution.Should().NotBeNull("the #879 dual-placement completion fixture must be seeded");
        execution!.ClientId.Should().Be(QaSeedRunner.ClientUserId);
        execution.PlanId.Should().Be(QaSeedRunner.QaTrainingPlanExternalId);
        execution.SessionId.Should().Be(QaSeedRunner.QaDualPlacementSessionId);
        execution.CompletedExerciseInstanceIds.Should().ContainSingle()
            .Which.Should().Be(QaSeedRunner.QaDualPlacementStandaloneInstanceId,
                "only the standalone instance must be marked complete");
        execution.CompletedExerciseInstanceIds.Should().NotContain(QaSeedRunner.QaDualPlacementNestedInstanceId,
            "the nested instance must remain incomplete so the two placements are distinguishable");
    }

    /// <summary>
    /// #946 — the QA client-nutritionist link must carry at least two PhotoDiaryRequest rows:
    /// one scoped to the seeded QA nutrition plan, one deliberately plan-less. Both must be
    /// Status=Accepted (reachable from the client app's active-request list, which filters
    /// {Accepted, InProgress}) with a non-null Mode/AcceptedAt as the CHECK constraints require.
    /// </summary>
    [Fact]
    public async Task SeedAsync_PhotoDiaryRequestFixture_HasWithPlanAndPlanlessAcceptedRows()
    {
        var ct = TestContext.Current.CancellationToken;

        await _factory.EnsureDefaultSeedAsync();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var clientProfile = await db.ClientProfiles.FirstAsync(cp => cp.UserId == QaSeedRunner.ClientUserId, ct);
        var nutriProfile = await db.ProfessionalProfiles.FirstAsync(pp => pp.UserId == QaSeedRunner.NutriUserId, ct);
        var nutriLink = await db.ClientProfessionalLinks.FirstAsync(
            l => l.ClientProfileId == clientProfile.Id && l.ProfessionalProfileId == nutriProfile.Id, ct);

        var withPlanRequest = await db.PhotoDiaryRequests
            .FirstOrDefaultAsync(r => r.Id == QaSeedRunner.QaPhotoDiaryRequestWithPlanId, ct);
        withPlanRequest.Should().NotBeNull("the with-plan PhotoDiaryRequest fixture must be seeded");
        withPlanRequest!.LinkId.Should().Be(nutriLink.Id);
        withPlanRequest.PendingInviteId.Should().BeNull("LinkId/PendingInviteId is an XOR CHECK constraint");
        withPlanRequest.PlanId.Should().Be(QaSeedRunner.QaNutritionPlanExternalId);
        withPlanRequest.Status.Should().Be(PhotoDiaryStatus.Accepted);
        withPlanRequest.Mode.Should().NotBeNull("Mode must be non-null when Status=Accepted (CHECK constraint)");
        withPlanRequest.AcceptedAt.Should().NotBeNull("AcceptedAt must be non-null when Status=Accepted (CHECK constraint)");

        var planlessRequest = await db.PhotoDiaryRequests
            .FirstOrDefaultAsync(r => r.Id == QaSeedRunner.QaPhotoDiaryRequestPlanlessId, ct);
        planlessRequest.Should().NotBeNull("the plan-less PhotoDiaryRequest fixture must be seeded");
        planlessRequest!.LinkId.Should().Be(nutriLink.Id);
        planlessRequest.PendingInviteId.Should().BeNull("LinkId/PendingInviteId is an XOR CHECK constraint");
        planlessRequest.PlanId.Should().BeNull(
            "a plan-less request is a legitimate backend state per PhotoDiaryRequest.PlanId's own doc comment");
        planlessRequest.Status.Should().Be(PhotoDiaryStatus.Accepted);
        planlessRequest.Mode.Should().NotBeNull("Mode must be non-null when Status=Accepted (CHECK constraint)");
        planlessRequest.AcceptedAt.Should().NotBeNull("AcceptedAt must be non-null when Status=Accepted (CHECK constraint)");
    }

    /// <summary>
    /// The completed SessionExecution seeded for the main QA plan must exercise all four
    /// planned-vs-actual set cases in a single session:
    ///
    ///   QA Squat  Set 1 — MODIFIED      actual != planned  (IsModified=true)
    ///   QA Squat  Set 2 — AS-PRESCRIBED actual == planned  (IsModified=false)
    ///   QA Deadlift Set 1 — SKIPPED     planned present, actual null
    ///   QA Deadlift Set 2 — EXTRA       no planned snapshot, actual present
    ///
    /// ClientId must be ApplicationUser.Id so the log is visible to CompleteWorkoutEndpoint
    /// and the coach-view planned-vs-actual query.
    /// </summary>
    [Fact]
    public async Task SeedAsync_MainPlanWorkoutLog_ExercisesFourPlannedVsActualCases()
    {
        var ct = TestContext.Current.CancellationToken;

        await _factory.EnsureDefaultSeedAsync();

        using var scope = _factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        var log = await mongo.SessionExecutions
            .Find(l => l.ExternalId == QaSeedRunner.QaMainPlanCompletedExecutionId)
            .FirstOrDefaultAsync(ct);

        log.Should().NotBeNull("main-plan completed SessionExecution must be seeded");
        log!.Status.Should().Be(SessionExecutionStatus.Completed, "log is marked complete");
        log.PlanId.Should().Be(QaSeedRunner.QaTrainingPlanExternalId);
        log.SessionId.Should().Be(QaSeedRunner.QaSessionId);
        log.ClientId.Should().Be(QaSeedRunner.ClientUserId,
            "SessionExecution.ClientId must be ApplicationUser.Id (11111111-...) — same contract as past-plan logs");
        log.Performance.Should().NotBeNull("completed log has live-workout performance data");
        log.Performance!.CompletedAt.Should().NotBeNull("CompletedAt is required for the partial unique index key derivation");

        log.Performance.Workouts.Should().HaveCount(1, "one workout mirrors the Standard section");
        var section = log.Performance.Workouts[0];
        section.Exercises.Should().HaveCount(2);

        // ── Exercise 1: QA Squat ───────────────────────────────────────────────
        var squat = section.Exercises.Single(e => e.ExerciseExternalId == QaSeedRunner.StandardExercise1Id);
        squat.Sets.Should().HaveCount(2);

        var squatSet1 = squat.Sets.Single(s => s.SetNumber == 1);
        squatSet1.Reps.Should().Be(8, "actual reps differ from planned");
        squatSet1.WeightKg.Should().Be(85m, "actual weight differs from planned");
        squatSet1.PlannedReps.Should().Be(10, "snapshot from plan prescription");
        squatSet1.PlannedWeightKg.Should().Be(80m);
        squatSet1.IsModified.Should().BeTrue("actual != planned → MODIFIED");

        var squatSet2 = squat.Sets.Single(s => s.SetNumber == 2);
        squatSet2.Reps.Should().Be(10);
        squatSet2.WeightKg.Should().Be(80m);
        squatSet2.PlannedReps.Should().Be(10);
        squatSet2.PlannedWeightKg.Should().Be(80m);
        squatSet2.IsModified.Should().BeFalse("actual == planned → AS-PRESCRIBED");

        // ── Exercise 2: QA Deadlift ────────────────────────────────────────────
        var deadlift = section.Exercises.Single(e => e.ExerciseExternalId == QaSeedRunner.StandardExercise2Id);
        deadlift.Sets.Should().HaveCount(2);

        var deadliftSet1 = deadlift.Sets.Single(s => s.SetNumber == 1);
        deadliftSet1.PlannedReps.Should().Be(5, "prescription captured");
        deadliftSet1.PlannedWeightKg.Should().Be(100m);
        deadliftSet1.Reps.Should().BeNull("client did not perform the set → SKIPPED");
        deadliftSet1.WeightKg.Should().BeNull();
        deadliftSet1.CompletedAt.Should().BeNull("skipped set has no completion timestamp");

        var deadliftSet2 = deadlift.Sets.Single(s => s.SetNumber == 2);
        deadliftSet2.Reps.Should().Be(6, "client logged an extra set");
        deadliftSet2.WeightKg.Should().Be(90m);
        deadliftSet2.PlannedReps.Should().BeNull("no planned snapshot → EXTRA set");
        deadliftSet2.PlannedWeightKg.Should().BeNull();
    }

    /// <summary>
    /// The multi-section fixture (#474) must seed:
    ///  - A TrainingPlan for the second QA client/trainer pair with one session
    ///    whose two sections BOTH reference the same shared exercise (SharedExerciseId).
    ///  - A completed SessionExecution with SectionId set on both sections so the
    ///    section-keying read path works. The Standard section contains edited
    ///    values (Set 1 and Set 3: IsModified=true). The AMRAP section contains
    ///    a plain logged set with no planned snapshot (IsModified=false).
    /// </summary>
    [Fact]
    public async Task SeedAsync_MultiSectionFixture_HasSharedExerciseInBothSectionsWithCorrectValues()
    {
        var ct = TestContext.Current.CancellationToken;

        await _factory.EnsureDefaultSeedAsync();

        using var scope = _factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        // Plan must exist and be owned by the second pair.
        var plan = await mongo.TrainingPlans
            .Find(p => p.ExternalId == QaSeedRunner.QaMultiSectionPlanExternalId)
            .FirstOrDefaultAsync(ct);

        plan.Should().NotBeNull("multi-section training plan must be seeded");
        plan!.TrainerId.Should().Be(QaSeedRunner.Trainer2UserId,
            "TrainingPlan.TrainerId must be Trainer2UserId (ApplicationUser.Id)");
        plan.ClientId.Should().Be(QaSeedRunner.Client2UserId,
            "TrainingPlan.ClientId must be Client2UserId (ApplicationUser.Id, #840)");

        var session = plan.Weeks[0].Days.SelectMany(d => d.Sessions).Single(s => s.SessionId == QaSeedRunner.QaMultiSectionSessionId);
        session.Workouts.Should().HaveCount(2, "session has Standard + AMRAP sections");

        var standardSection = session.Workouts.Single(s => s.WorkoutId == QaSeedRunner.MultiSectionStandardWorkoutId);
        standardSection.Format.Should().BeNull("Standard section has null format");
        standardSection.Exercises.Should().HaveCount(1);
        standardSection.Exercises[0].ExerciseExternalId.Should().Be(QaSeedRunner.SharedExerciseId);
        standardSection.Exercises[0].Sets.Should().HaveCount(3, "Standard section has 3 prescribed sets");

        var amrapSection = session.Workouts.Single(s => s.WorkoutId == QaSeedRunner.MultiSectionAmrapWorkoutId);
        amrapSection.Format.Should().Be(FormUp.Application.Domain.Enums.WorkoutFormat.AMRAP);
        amrapSection.Exercises.Should().HaveCount(1);
        amrapSection.Exercises[0].ExerciseExternalId.Should().Be(QaSeedRunner.SharedExerciseId,
            "AMRAP section references the SAME exercise as the Standard section");
        amrapSection.Exercises[0].Sets.Should().BeEmpty("AMRAP section carries no prescribed sets");

        // SessionExecution must exist with SectionId populated on both sections.
        var log = await mongo.SessionExecutions
            .Find(l => l.ExternalId == QaSeedRunner.QaMultiSectionExecutionId)
            .FirstOrDefaultAsync(ct);

        log.Should().NotBeNull("multi-section SessionExecution must be seeded");
        log!.Status.Should().Be(SessionExecutionStatus.Completed, "log is marked complete");
        log.PlanId.Should().Be(QaSeedRunner.QaMultiSectionPlanExternalId);
        log.SessionId.Should().Be(QaSeedRunner.QaMultiSectionSessionId);
        log.ClientId.Should().Be(QaSeedRunner.Client2UserId,
            "SessionExecution.ClientId must be ApplicationUser.Id (Client2UserId)");
        log.Performance.Should().NotBeNull("completed log has live-workout performance data");
        log.Performance!.CompletedAt.Should().NotBeNull("CompletedAt is required for the partial unique index key derivation");
        log.Performance.Workouts.Should().HaveCount(2, "log captures both Standard and AMRAP sections");

        // Standard section in the log — WorkoutId must match the plan section.
        var logStandard = log.Performance.Workouts.Single(s => s.WorkoutId == QaSeedRunner.MultiSectionStandardWorkoutId);
        logStandard.Exercises.Should().HaveCount(1);
        var logStandardExercise = logStandard.Exercises[0];
        logStandardExercise.ExerciseExternalId.Should().Be(QaSeedRunner.SharedExerciseId);
        logStandardExercise.Sets.Should().HaveCount(3);

        // Set 1: MODIFIED (actual Reps=12 != planned 15, actual WeightKg=28 != planned 24).
        var set1 = logStandardExercise.Sets.Single(s => s.SetNumber == 1);
        set1.Reps.Should().Be(12);
        set1.WeightKg.Should().Be(28m);
        set1.PlannedReps.Should().Be(15);
        set1.PlannedWeightKg.Should().Be(24m);
        set1.IsModified.Should().BeTrue("Set 1 actual != planned → MODIFIED (shows 'upraveno' on coach-detail)");

        // Set 2: AS-PRESCRIBED (actual matches planned).
        var set2 = logStandardExercise.Sets.Single(s => s.SetNumber == 2);
        set2.Reps.Should().Be(15);
        set2.WeightKg.Should().Be(24m);
        set2.PlannedReps.Should().Be(15);
        set2.PlannedWeightKg.Should().Be(24m);
        set2.IsModified.Should().BeFalse("Set 2 actual == planned → AS-PRESCRIBED (no 'upraveno' badge)");

        // Set 3: MODIFIED.
        var set3 = logStandardExercise.Sets.Single(s => s.SetNumber == 3);
        set3.Reps.Should().Be(10);
        set3.WeightKg.Should().Be(28m);
        set3.IsModified.Should().BeTrue("Set 3 actual != planned → MODIFIED");

        // AMRAP section in the log — WorkoutId must match the plan AMRAP section.
        var logAmrap = log.Performance.Workouts.Single(s => s.WorkoutId == QaSeedRunner.MultiSectionAmrapWorkoutId);
        logAmrap.Format.Should().Be(FormUp.Application.Domain.Enums.WorkoutFormat.AMRAP);
        logAmrap.Exercises.Should().HaveCount(1);
        var logAmrapExercise = logAmrap.Exercises[0];
        logAmrapExercise.ExerciseExternalId.Should().Be(QaSeedRunner.SharedExerciseId,
            "AMRAP section references the SAME exercise but is keyed by a different WorkoutId");
        logAmrapExercise.Sets.Should().HaveCount(1);

        var amrapSet = logAmrapExercise.Sets[0];
        amrapSet.Reps.Should().Be(15);
        amrapSet.WeightKg.Should().Be(24m);
        amrapSet.PlannedReps.Should().BeNull("AMRAP set has no planned snapshot");
        amrapSet.PlannedWeightKg.Should().BeNull("AMRAP set has no planned snapshot");
        amrapSet.IsModified.Should().BeFalse("AMRAP set without planned snapshot → IsModified=false (no 'upraveno')");
    }

    /// <summary>
    /// The #715 questionnaire fixture must seed a template with 2 sections and
    /// 6 answerable questions covering every formatAnswerValue branch
    /// (short_text, number, single_choice, scale, multi_select, file_upload),
    /// a SUBMITTED response with a matching answer per question, and that
    /// response's PublicId must be written onto the main training plan's
    /// QuestionnaireResponseId field. The nutrition plan is asserted
    /// separately (#720 links it to the nutritionist-owned response instead —
    /// see <see cref="SeedAsync_NutritionistQuestionnaireFixture_HasTemplateSubmittedResponseAndPlanLink"/>).
    /// </summary>
    [Fact]
    public async Task SeedAsync_QuestionnaireFixture_HasTemplateSubmittedResponseAndTrainingPlanLink()
    {
        var ct = TestContext.Current.CancellationToken;

        await _factory.EnsureDefaultSeedAsync();

        using var scope = _factory.Services.CreateScope();
        var sp    = scope.ServiceProvider;
        var db    = sp.GetRequiredService<ApplicationDbContext>();
        var mongo = sp.GetRequiredService<IMongoContext>();

        // Questionnaire template — 2 sections + 6 answerable questions.
        var questionnaire = await db.Questionnaires
            .Include(q => q.Questions)
            .FirstOrDefaultAsync(q => q.PublicId == QaSeedRunner.QaQuestionnaireExternalId, ct);

        questionnaire.Should().NotBeNull("the #715 questionnaire template must be seeded");
        questionnaire!.ProfessionalId.Should().Be(QaSeedRunner.TrainerUserId,
            "the questionnaire template is owned by the QA trainer");
        questionnaire.Questions.Should().HaveCount(8, "2 section headers + 6 answerable questions");
        questionnaire.Questions.Count(q => q.Type == "section").Should().Be(2,
            "the template must have at least 2 sections (#715 AC)");

        var answerableTypes = questionnaire.Questions
            .Where(q => q.Type != "section")
            .Select(q => q.Type)
            .ToList();
        answerableTypes.Should().BeEquivalentTo(
            ["short_text", "number", "single_choice", "scale", "multi_select", "file_upload"],
            "every formatAnswerValue branch must be exercised");

        // Submitted response.
        var response = await db.QuestionnaireResponses
            .Include(r => r.Answers)
            .FirstOrDefaultAsync(r => r.PublicId == QaSeedRunner.QaQuestionnaireResponseExternalId, ct);

        response.Should().NotBeNull("the #715 submitted response must be seeded");
        response!.ClientId.Should().Be(QaSeedRunner.ClientUserId);
        response.ProfessionalId.Should().Be(QaSeedRunner.TrainerUserId);
        response.Status.Should().Be(QuestionnaireResponseStatus.Submitted);
        response.SubmittedAt.Should().NotBeNull("a Submitted response must have a SubmittedAt timestamp");
        response.Answers.Should().HaveCount(6, "one answer per answerable question");

        // Plan link — the training plan must point at this response's PublicId.
        var trainingPlan = await mongo.TrainingPlans
            .Find(p => p.ExternalId == QaSeedRunner.QaTrainingPlanExternalId)
            .FirstOrDefaultAsync(ct);
        trainingPlan.Should().NotBeNull();
        trainingPlan!.QuestionnaireResponseId.Should().Be(QaSeedRunner.QaQuestionnaireResponseExternalId,
            "the main training plan must be linked to the trainer's submitted response so #697's Dotaznik tab renders it");

        // The nutrition plan must NOT be linked to this trainer-owned response
        // — #720 links it to the nutritionist-owned response instead.
        var nutritionPlan = await mongo.NutritionPlans
            .Find(p => p.ExternalId == QaSeedRunner.QaNutritionPlanExternalId)
            .FirstOrDefaultAsync(ct);
        nutritionPlan.Should().NotBeNull();
        nutritionPlan!.QuestionnaireResponseId.Should().NotBe(QaSeedRunner.QaQuestionnaireResponseExternalId,
            "the nutrition plan must link a nutritionist-owned response (#720), not this trainer-owned one");
    }

    /// <summary>
    /// The #720 nutritionist-owned questionnaire fixture must seed a template
    /// with 2 sections and 6 answerable questions covering every
    /// formatAnswerValue branch, a SUBMITTED response owned by the QA
    /// nutritionist, an active nutritionist↔client link so
    /// GetClientResponsesEndpoint can return it, and the response's PublicId
    /// must be written onto the seeded nutrition plan's QuestionnaireResponseId
    /// field — replacing #715's trainer-owned link there.
    /// </summary>
    [Fact]
    public async Task SeedAsync_NutritionistQuestionnaireFixture_HasTemplateSubmittedResponseAndPlanLink()
    {
        var ct = TestContext.Current.CancellationToken;

        await _factory.EnsureDefaultSeedAsync();

        using var scope = _factory.Services.CreateScope();
        var sp    = scope.ServiceProvider;
        var db    = sp.GetRequiredService<ApplicationDbContext>();
        var mongo = sp.GetRequiredService<IMongoContext>();

        // Questionnaire template — owned by the QA nutritionist, 2 sections + 6 answerable questions.
        var questionnaire = await db.Questionnaires
            .Include(q => q.Questions)
            .FirstOrDefaultAsync(q => q.PublicId == QaSeedRunner.QaNutriQuestionnaireExternalId, ct);

        questionnaire.Should().NotBeNull("the #720 nutritionist questionnaire template must be seeded");
        questionnaire!.ProfessionalId.Should().Be(QaSeedRunner.NutriUserId,
            "the questionnaire template is owned by the QA nutritionist");
        questionnaire.Questions.Should().HaveCount(8, "2 section headers + 6 answerable questions");
        questionnaire.Questions.Count(q => q.Type == "section").Should().Be(2,
            "the template must have at least 2 sections (#720 AC)");

        var answerableTypes = questionnaire.Questions
            .Where(q => q.Type != "section")
            .Select(q => q.Type)
            .ToList();
        answerableTypes.Should().BeEquivalentTo(
            ["short_text", "number", "single_choice", "scale", "multi_select", "file_upload"],
            "every formatAnswerValue branch must be exercised, same spread as #715");

        // Submitted response.
        var response = await db.QuestionnaireResponses
            .Include(r => r.Answers)
            .FirstOrDefaultAsync(r => r.PublicId == QaSeedRunner.QaNutriQuestionnaireResponseExternalId, ct);

        response.Should().NotBeNull("the #720 submitted response must be seeded");
        response!.ClientId.Should().Be(QaSeedRunner.ClientUserId);
        response.ProfessionalId.Should().Be(QaSeedRunner.NutriUserId,
            "the response must be owned by the QA nutritionist so GetClientResponsesEndpoint returns it to her");
        response.Status.Should().Be(QuestionnaireResponseStatus.Submitted);
        response.SubmittedAt.Should().NotBeNull("a Submitted response must have a SubmittedAt timestamp");
        response.Answers.Should().HaveCount(6, "one answer per answerable question");

        // Nutritionist↔client link must exist and be active — required by
        // GetClientResponsesEndpoint's active-link check.
        var clientProfile = await db.ClientProfiles.FirstAsync(cp => cp.UserId == QaSeedRunner.ClientUserId, ct);
        var nutriProfile = await db.ProfessionalProfiles.FirstAsync(pp => pp.UserId == QaSeedRunner.NutriUserId, ct);
        var nutriLink = await db.ClientProfessionalLinks.FirstOrDefaultAsync(
            l => l.ClientProfileId == clientProfile.Id && l.ProfessionalProfileId == nutriProfile.Id, ct);
        nutriLink.Should().NotBeNull("a nutritionist↔client link must be seeded so the response is queryable by the nutritionist");
        nutriLink!.ConnectionState.Should().Be(ConnectionState.Connected);
        response.LinkId.Should().Be(nutriLink.Id, "the response's LinkId must reference the nutritionist↔client link");

        // Plan link — the nutrition plan must point at this response's PublicId,
        // NOT the #715 trainer-owned response.
        var nutritionPlan = await mongo.NutritionPlans
            .Find(p => p.ExternalId == QaSeedRunner.QaNutritionPlanExternalId)
            .FirstOrDefaultAsync(ct);
        nutritionPlan.Should().NotBeNull();
        nutritionPlan!.QuestionnaireResponseId.Should().Be(QaSeedRunner.QaNutriQuestionnaireResponseExternalId,
            "the nutrition plan must be linked to the nutritionist-owned response so #698's Dotaznik tab renders it for the nutritionist");
    }

    /// <summary>
    /// Root-cause probe for the #854 e2e regression (PR #854 / epic #835): reproduces,
    /// over real HTTP with a real JWT (not a direct Mongo/EF assertion), the exact two
    /// calls the trainer-portal "Dotazník" tab makes on the training-plan detail page:
    ///   1. GET /training/plans/{planId} — must return a non-null questionnaireResponseId.
    ///   2. GET /trainer/clients/{clientPublicId}/questionnaire-responses — must contain
    ///      a Submitted response whose responsePublicId matches (1).
    /// The QaSeedRunnerTests above only assert the Mongo document / EF row directly;
    /// this closes the gap by exercising the actual endpoints the web e2e spec hits.
    /// </summary>
    [Fact]
    public async Task HttpFlow_TrainerPlanDetail_QuestionnaireResponseIdRoundTripsAndIsFetchable()
    {
        var ct = TestContext.Current.CancellationToken;

        await _factory.EnsureDefaultSeedAsync();

        var client = _factory.CreateClient();
        var (accessToken, _) = await TestHelpers.LoginAsync(client, QaSeedRunner.TrainerEmail, "TestSeed1!");
        TestHelpers.SetBearerToken(client, accessToken);

        // The API serializes enums as strings (JsonStringEnumConverter globally),
        // so use matching options when deserializing the test response — see the
        // same pattern in GetFullTrainingPlanIntegrationTests.
        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        // ── 1. GET /training/plans/{planId} — same call TrainingPlanPage.tsx makes ──
        var planResponse = await client.GetAsync(
            $"/training/plans/{QaSeedRunner.QaTrainingPlanExternalId}", ct);
        planResponse.EnsureSuccessStatusCode();

        var plan = await planResponse.Content.ReadFromJsonAsync<GetTrainingPlanResponse>(jsonOptions, ct);
        plan.Should().NotBeNull();
        plan!.QuestionnaireResponseId.Should().Be(QaSeedRunner.QaQuestionnaireResponseExternalId,
            "GET /training/plans/{planId} must return the linked questionnaire response id " +
            "so the web 'Dotazník' tab can resolve it — this is what feeds " +
            "plan.questionnaireResponseId in TrainingPlanPage.tsx");

        // ── 2. GET /trainer/clients/{clientPublicId}/questionnaire-responses — same call
        // PlanQuestionnairePanel / QuestionnaireAnswersSection make (getClientQuestionnaireResponses) ──
        var responsesHttp = await client.GetAsync(
            $"/trainer/clients/{QaSeedRunner.ClientProfilePublicId}/questionnaire-responses", ct);
        responsesHttp.EnsureSuccessStatusCode();

        var responses = await responsesHttp.Content.ReadFromJsonAsync<GetClientResponsesResponse>(jsonOptions, ct);
        responses.Should().NotBeNull();
        var linked = responses!.Responses.SingleOrDefault(
            r => r.ResponsePublicId == plan.QuestionnaireResponseId.Value);

        linked.Should().NotBeNull(
            "the questionnaire-responses list must contain an entry whose ResponsePublicId " +
            "matches plan.QuestionnaireResponseId — this is the join TrainingPlanPage.tsx's " +
            "linkedQuestionnaireResponse useMemo performs client-side");
        linked!.Status.Should().Be("Submitted");
        linked.QuestionnaireTitle.Should().Be("QA Onboarding Questionnaire");
    }

    /// <summary>
    /// The #1101 former-client thread (ended link, unread client message) must be listed under
    /// the inbox's All chip and under none of the five roster-driven chips — an ended link is off
    /// the live roster — and must stay sendable (an ended link never locks a professional's send).
    /// </summary>
    [Fact]
    public async Task HttpFlow_EndedLinkConversation_ListsUnderAllOnly_AndIsNotSendLocked()
    {
        var ct = TestContext.Current.CancellationToken;

        await _factory.EnsureDefaultSeedAsync();

        var client = _factory.CreateClient();
        var (accessToken, _) = await TestHelpers.LoginAsync(client, QaSeedRunner.TrainerEmail, "TestSeed1!");
        TestHelpers.SetBearerToken(client, accessToken);

        async Task<List<InboxRow>> ListAsync(string filter)
        {
            var response = await client.GetAsync($"/conversations?filter={filter}", ct);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<List<InboxRow>>(
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct))!;
        }

        var allRows = await ListAsync("All");
        var endedRow = allRows.Should().ContainSingle(r => r.Id == QaSeedRunner.QaEndedLinkConversationId,
            "the ended-link thread is unioned into All as an off-roster conversation").Subject;
        endedRow.IsSendLocked.Should().BeFalse("an ended link stays sendable");
        endedRow.UnreadCount.Should().Be(1, "the fixture carries one unread client message");
        endedRow.Participant.Name.Should().Be("QA Former")
            .And.NotContain("QA Client", "three Playwright specs pick the first row containing 'QA Client'");

        foreach (var chip in new[] { "UnreadMessages", "NoMessages", "NewCheckIns", "MissingCheckIns", "EndingSoon" })
        {
            (await ListAsync(chip)).Should().NotContain(r => r.Id == QaSeedRunner.QaEndedLinkConversationId,
                $"an ended link is off the live roster, so the {chip} chip must not list it");
        }
    }

    private sealed class InboxRow
    {
        public Guid? Id { get; set; }
        public InboxParticipant Participant { get; set; } = null!;
        public int UnreadCount { get; set; }
        public bool IsSendLocked { get; set; }
    }

    private sealed class InboxParticipant
    {
        public string Name { get; set; } = string.Empty;
    }

    /// <summary>
    /// Nutritionist-side counterpart to <see cref="HttpFlow_TrainerPlanDetail_QuestionnaireResponseIdRoundTripsAndIsFetchable"/>,
    /// reproducing the #698 nutrition-plan-questionnaire.spec.ts flow: GET /nutrition/plans/{planId}
    /// (GetPlanEndpoint — NOT touched by the #835 epic diff) must return a non-null
    /// questionnaireResponseId, and GET /trainer/clients/{clientPublicId}/questionnaire-responses
    /// (also not touched) must return a matching Submitted entry owned by the QA nutritionist.
    /// Included to rule out an asymmetric nutritionist-role bug — neither endpoint appears in
    /// the epic's file diff, so this is expected to pass; a failure here would mean the
    /// regression is NOT confined to the touched TrainingPlans/ClientTraining read paths.
    /// </summary>
    [Fact]
    public async Task HttpFlow_NutritionistPlanDetail_QuestionnaireResponseIdRoundTripsAndIsFetchable()
    {
        var ct = TestContext.Current.CancellationToken;

        await _factory.EnsureDefaultSeedAsync();

        var client = _factory.CreateClient();
        var (accessToken, _) = await TestHelpers.LoginAsync(client, QaSeedRunner.NutriEmail, "TestSeed1!");
        TestHelpers.SetBearerToken(client, accessToken);

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        // ── 1. GET /nutrition/plans/{planId} — same call NutritionPlanPage.tsx makes ──
        var planResponse = await client.GetAsync(
            $"/nutrition/plans/{QaSeedRunner.QaNutritionPlanExternalId}", ct);
        planResponse.EnsureSuccessStatusCode();

        var plan = await planResponse.Content.ReadFromJsonAsync<FormUp.Application.Features.NutritionPlans.GetPlan.GetPlanResponse>(
            jsonOptions, ct);
        plan.Should().NotBeNull();
        plan!.QuestionnaireResponseId.Should().Be(QaSeedRunner.QaNutriQuestionnaireResponseExternalId,
            "GET /nutrition/plans/{planId} must return the nutritionist-linked questionnaire response id");

        // ── 2. GET /trainer/clients/{clientPublicId}/questionnaire-responses as the nutritionist ──
        var responsesHttp = await client.GetAsync(
            $"/trainer/clients/{QaSeedRunner.ClientProfilePublicId}/questionnaire-responses", ct);
        responsesHttp.EnsureSuccessStatusCode();

        var responses = await responsesHttp.Content.ReadFromJsonAsync<GetClientResponsesResponse>(jsonOptions, ct);
        responses.Should().NotBeNull();
        var linked = responses!.Responses.SingleOrDefault(
            r => r.ResponsePublicId == plan.QuestionnaireResponseId.Value);

        linked.Should().NotBeNull(
            "the nutritionist's questionnaire-responses list must contain an entry whose " +
            "ResponsePublicId matches plan.QuestionnaireResponseId");
        linked!.Status.Should().Be("Submitted");
        linked.QuestionnaireTitle.Should().Be("QA Nutrition Intake Questionnaire");
    }

    /// <summary>
    /// Regression test for the #840/#845 client-identifier semantics regression that broke
    /// PR #854 (#697/#698 e2e specs). #840 standardised TrainingPlan.ClientId on
    /// ApplicationUser.Id for internal storage/filters — correct — but
    /// <see cref="GetTrainingPlanResponse.FromDocument"/> was (pre-fix) still emitting that
    /// internal UserId verbatim as the response's outward-facing <c>clientId</c> field, which
    /// TrainingPlanPage.tsx feeds directly into
    /// <c>GET /trainer/clients/{{clientId}}/questionnaire-responses</c> — a route keyed on
    /// <c>ClientProfile.PublicId</c>, not <c>ApplicationUser.Id</c>. UserId != PublicId, so the
    /// browser's real call 404s and the Dotazník tab renders empty.
    /// <para>
    /// Unlike <see cref="HttpFlow_TrainerPlanDetail_QuestionnaireResponseIdRoundTripsAndIsFetchable"/>
    /// (which calls the responses endpoint with <see cref="QaSeedRunner.ClientProfilePublicId"/>
    /// directly and therefore cannot detect this class of bug), this test CHAINS the two calls
    /// exactly the way the browser does: it takes <c>plan.ClientId</c> straight off the first
    /// HTTP response and feeds it into the second HTTP call, with no test-side knowledge of the
    /// "real" PublicId. If the response boundary regresses back to emitting the internal
    /// UserId, this test 404s on step 2.
    /// </para>
    /// </summary>
    [Fact]
    public async Task HttpFlow_TrainerPlanDetail_ChainedClientIdRoundTripsToQuestionnaireResponses()
    {
        var ct = TestContext.Current.CancellationToken;

        await _factory.EnsureDefaultSeedAsync();

        var client = _factory.CreateClient();
        var (accessToken, _) = await TestHelpers.LoginAsync(client, QaSeedRunner.TrainerEmail, "TestSeed1!");
        TestHelpers.SetBearerToken(client, accessToken);

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        // ── 1. GET /training/plans/{planId} — capture the response's clientId verbatim,
        // exactly as TrainingPlanPage.tsx does (plan.clientId, no re-derivation). ──
        var planResponse = await client.GetAsync(
            $"/training/plans/{QaSeedRunner.QaTrainingPlanExternalId}", ct);
        planResponse.EnsureSuccessStatusCode();

        var plan = await planResponse.Content.ReadFromJsonAsync<GetTrainingPlanResponse>(jsonOptions, ct);
        plan.Should().NotBeNull();

        // The response's clientId must be the ClientProfile.PublicId contract, NOT the
        // internal Mongo storage key (ApplicationUser.Id) — this is the exact assertion that
        // would have caught #854 before it reached the compose-harness e2e gate.
        plan!.ClientId.Should().Be(QaSeedRunner.ClientProfilePublicId,
            "GetTrainingPlanResponse.ClientId must be the client-facing ClientProfile.PublicId " +
            "(pre-#840 contract) — NOT plan.ClientId's internal ApplicationUser.Id storage key");
        plan.ClientId.Should().NotBe(QaSeedRunner.ClientUserId,
            "the response must not leak the internal ApplicationUser.Id storage key as clientId");

        // ── 2. CHAIN: feed the plan response's clientId straight into the second call, with
        // no test-side substitution of the "known-correct" PublicId. This is what makes the
        // test equivalent to the browser's real request sequence. ──
        var responsesHttp = await client.GetAsync(
            $"/trainer/clients/{plan.ClientId}/questionnaire-responses", ct);

        responsesHttp.StatusCode.Should().Be(System.Net.HttpStatusCode.OK,
            "chaining plan.ClientId into /trainer/clients/{{clientId}}/... must resolve — a 404 " +
            "here reproduces the #854 regression exactly as the browser experienced it");

        var responses = await responsesHttp.Content.ReadFromJsonAsync<GetClientResponsesResponse>(jsonOptions, ct);
        responses.Should().NotBeNull();
        var linked = responses!.Responses.SingleOrDefault(
            r => r.ResponsePublicId == plan.QuestionnaireResponseId!.Value);

        linked.Should().NotBeNull(
            "the chained call must return the same linked Submitted response the direct-PublicId " +
            "call returns — proving the chained clientId resolves to the same client");
        linked!.Status.Should().Be("Submitted");
        linked.QuestionnaireTitle.Should().Be("QA Onboarding Questionnaire");
    }

    /// <summary>
    /// Nutrition-side counterpart to
    /// <see cref="HttpFlow_TrainerPlanDetail_ChainedClientIdRoundTripsToQuestionnaireResponses"/>,
    /// reproducing the #698 nutrition-plan-questionnaire.spec.ts chained-identifier flow via
    /// <see cref="GetPlanResponse"/> (nutrition plan detail).
    /// </summary>
    [Fact]
    public async Task HttpFlow_NutritionistPlanDetail_ChainedClientIdRoundTripsToQuestionnaireResponses()
    {
        var ct = TestContext.Current.CancellationToken;

        await _factory.EnsureDefaultSeedAsync();

        var client = _factory.CreateClient();
        var (accessToken, _) = await TestHelpers.LoginAsync(client, QaSeedRunner.NutriEmail, "TestSeed1!");
        TestHelpers.SetBearerToken(client, accessToken);

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        // ── 1. GET /nutrition/plans/{planId} — capture clientId verbatim, as
        // NutritionPlanPage.tsx does. ──
        var planResponse = await client.GetAsync(
            $"/nutrition/plans/{QaSeedRunner.QaNutritionPlanExternalId}", ct);
        planResponse.EnsureSuccessStatusCode();

        var plan = await planResponse.Content.ReadFromJsonAsync<FormUp.Application.Features.NutritionPlans.GetPlan.GetPlanResponse>(
            jsonOptions, ct);
        plan.Should().NotBeNull();

        plan!.ClientId.Should().Be(QaSeedRunner.ClientProfilePublicId,
            "GetPlanResponse.ClientId must be the client-facing ClientProfile.PublicId " +
            "(pre-#840 contract) — NOT plan.ClientId's internal ApplicationUser.Id storage key");
        plan.ClientId.Should().NotBe(QaSeedRunner.ClientUserId,
            "the response must not leak the internal ApplicationUser.Id storage key as clientId");

        // ── 2. CHAIN: feed the plan response's clientId straight into the second call. ──
        var responsesHttp = await client.GetAsync(
            $"/trainer/clients/{plan.ClientId}/questionnaire-responses", ct);

        responsesHttp.StatusCode.Should().Be(System.Net.HttpStatusCode.OK,
            "chaining plan.ClientId into /trainer/clients/{{clientId}}/... must resolve — a 404 " +
            "here reproduces the #854 regression exactly as the browser experienced it");

        var responses = await responsesHttp.Content.ReadFromJsonAsync<GetClientResponsesResponse>(jsonOptions, ct);
        responses.Should().NotBeNull();
        var linked = responses!.Responses.SingleOrDefault(
            r => r.ResponsePublicId == plan.QuestionnaireResponseId!.Value);

        linked.Should().NotBeNull(
            "the chained call must return the same linked Submitted response the direct-PublicId " +
            "call returns — proving the chained clientId resolves to the same client");
        linked!.Status.Should().Be("Submitted");
        linked.QuestionnaireTitle.Should().Be("QA Nutrition Intake Questionnaire");
    }
}
