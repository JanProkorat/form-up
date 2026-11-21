using System.Net;
using FluentAssertions;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
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

namespace FormUp.Tests.Endpoints.Testing;

// ---------------------------------------------------------------------------
// Custom factories — each gate combination needs its own WebApplicationFactory
// because gating depends on per-factory configuration (Testing:Enabled +
// environment). The endpoint is always in the route table; the gate is evaluated
// at request time inside HandleAsync, so each factory supplies different config
// to drive the different gate outcomes.
// ---------------------------------------------------------------------------

/// <summary>
/// Base factory that spins up real Postgres + Mongo via Testcontainers and
/// replaces the non-DB services (email, blob, push) with no-op fakes.
/// Derived classes configure the environment name and Testing:Enabled value.
/// </summary>
public abstract class ResetEndpointFactoryBase(SharedTestContainers sharedContainers)
    : WebApplicationFactory<Program>, IAsyncLifetime
{
    private string _postgresConnectionString = string.Empty;
    private string _mongoDatabaseName = string.Empty;

    /// <summary>
    /// ASPNETCORE_ENVIRONMENT to use for this factory instance.
    /// </summary>
    protected abstract string EnvironmentName { get; }

    /// <summary>
    /// Whether Testing:Enabled should be true for this factory instance.
    /// </summary>
    protected abstract bool TestingEnabled { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("POSTGRES_PASSWORD", "test");
        builder.UseSetting("MONGO_PASSWORD", "test");
        builder.UseSetting("MINIO_ACCESS_KEY", "test");
        builder.UseSetting("MINIO_SECRET_KEY", "test");
        builder.UseSetting("JWT_SECRET", new string('x', 64));
        builder.UseSetting("RateLimiting:Disabled", "true");
        builder.UseSetting("Testing:Enabled", TestingEnabled ? "true" : "false");

        // QA_SEED_PASSWORD must be set as a real env var because QaSeedRunner reads
        // it via Environment.GetEnvironmentVariable, not from IConfiguration.
        Environment.SetEnvironmentVariable("QA_SEED_PASSWORD", "TestSeed1!");

        // Placeholder connection strings (overridden below)
        builder.UseSetting("ConnectionStrings:PostgreSQl",
            "Host=localhost;Database=placeholder;Username=postgres");
        builder.UseSetting("ConnectionStrings:MongoDB",
            "mongodb://localhost:27017");

        builder.ConfigureServices(services =>
        {
            // Replace DbContext with Testcontainer-backed Postgres
            var pgDescriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<ApplicationDbContext>));
            if (pgDescriptor is not null)
                services.Remove(pgDescriptor);

            services.AddDbContext<ApplicationDbContext>(options =>
                options.UseNpgsql(_postgresConnectionString)
                    .ConfigureWarnings(w => w.Ignore(
                        Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));

            // Replace MongoDB
            var mongoDbDescriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(IMongoDatabase));
            if (mongoDbDescriptor is not null)
                services.Remove(mongoDbDescriptor);

            var mongoContextDescriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(IMongoContext));
            if (mongoContextDescriptor is not null)
                services.Remove(mongoContextDescriptor);

            services.AddSingleton<IMongoDatabase>(_ =>
            {
                var client = new MongoClient(sharedContainers.MongoConnectionString);
                return client.GetDatabase(_mongoDatabaseName);
            });
            services.AddSingleton<IMongoContext, MongoContext>();

            // No-op fakes for external services
            var emailDescriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(FormUp.Application.Domain.Interfaces.IEmailService));
            if (emailDescriptor is not null)
                services.Remove(emailDescriptor);
            services.AddSingleton<FakeEmailService>();
            services.AddSingleton<FormUp.Application.Domain.Interfaces.IEmailService>(
                sp => sp.GetRequiredService<FakeEmailService>());

            var notifierDescriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(FormUp.Application.Domain.Interfaces.IRealtimeNotifier));
            if (notifierDescriptor is not null)
                services.Remove(notifierDescriptor);
            services.AddSingleton<FakeRealtimeNotifier>();
            services.AddSingleton<FormUp.Application.Domain.Interfaces.IRealtimeNotifier>(
                sp => sp.GetRequiredService<FakeRealtimeNotifier>());

            var blobDescriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(FormUp.Application.Domain.Interfaces.IBlobStorageService));
            if (blobDescriptor is not null)
                services.Remove(blobDescriptor);
            services.AddSingleton<FormUp.Application.Domain.Interfaces.IBlobStorageService, FakeBlobStorageService>();

            var pushDescriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(FormUp.Application.Domain.Interfaces.IPushNotificationService));
            if (pushDescriptor is not null)
                services.Remove(pushDescriptor);
            services.AddSingleton<FakePushNotificationService>();
            services.AddSingleton<FormUp.Application.Domain.Interfaces.IPushNotificationService>(
                sp => sp.GetRequiredService<FakePushNotificationService>());

            // #726: prevent the background schedulers/worker from starting in this
            // test host — see TestHostedServiceExtensions for the root cause.
            services.RemoveBackgroundHostedServices();
        });

        builder.UseEnvironment(EnvironmentName);
    }

    public async ValueTask InitializeAsync()
    {
        // GetType().Name distinguishes the three concrete gate-combination factories
        // (ResetEndpointEnabledFactory / DisabledFactory / ProductionFactory) so each gets
        // its own isolated database inside the shared containers without every subclass
        // having to declare its own name prefix.
        _postgresConnectionString = await sharedContainers.CreatePostgresDatabaseAsync(GetType().Name);
        _mongoDatabaseName = SharedTestContainers.CreateMongoDatabaseName(GetType().Name);

        // Apply migrations + seed roles (not QA users — the reset endpoint handles that)
        await ApplicationDbContextSeed.SeedAsync(Services);
    }

    // Intentionally skip base.DisposeAsync() — see FitnessApiFactory's matching remark. No
    // Testcontainer to dispose here anymore (#1104 Phase B); SharedTestContainers owns that.
    public new ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Gate: Testing:Enabled=true + Development environment — the happy path.
/// The reset endpoint MUST be registered and callable.
/// </summary>
public class ResetEndpointEnabledFactory(SharedTestContainers sharedContainers)
    : ResetEndpointFactoryBase(sharedContainers)
{
    protected override string EnvironmentName => "Development";
    protected override bool TestingEnabled => true;
}

/// <summary>
/// Gate: Testing:Enabled=false (any environment) — endpoint must NOT be registered.
/// </summary>
public class ResetEndpointDisabledFactory(SharedTestContainers sharedContainers)
    : ResetEndpointFactoryBase(sharedContainers)
{
    protected override string EnvironmentName => "Development";
    protected override bool TestingEnabled => false;
}

/// <summary>
/// Gate: Testing:Enabled=true + non-Development environment.
/// With the single-gate model (Testing:Enabled is the sole gate), the endpoint
/// must succeed (return 204) even when the environment is not "Development".
/// Using "Staging" to simulate a non-Development CI test harness environment.
/// </summary>
public class ResetEndpointProductionFactory(SharedTestContainers sharedContainers)
    : ResetEndpointFactoryBase(sharedContainers)
{
    protected override string EnvironmentName => "Staging";
    protected override bool TestingEnabled => true;
}

// ---------------------------------------------------------------------------
// Test class — one [Fact] per gate combination + idempotency + round-trip seed
// ---------------------------------------------------------------------------

/// <summary>
/// Defines a test collection for the reset-endpoint tests. Tests are NOT in
/// the shared "Integration" collection because each test needs its own factory
/// with distinct startup configuration. Using a named collection ensures the
/// reset tests run serially with each other, which avoids Testcontainer port
/// exhaustion from multiple MongoDB instances starting simultaneously. Boots
/// all three gate-combination factories ONCE for the whole collection rather
/// than per test (#1104) — safe because every fact's own <c>POST /test/reset</c>
/// call performs a full drop-schema/drop-collections wipe as its first action,
/// so no fact ever depends on state left behind by a previous one.
/// </summary>
// Sets QA_SEED_PASSWORD and runs QaSeedRunner, which reads process-wide env vars; run alone.
[CollectionDefinition("ResetTests", DisableParallelization = true)]
public class ResetTestsCollection :
    ICollectionFixture<ResetEndpointEnabledFactory>,
    ICollectionFixture<ResetEndpointDisabledFactory>,
    ICollectionFixture<ResetEndpointProductionFactory>;

/// <summary>
/// Integration tests for <c>POST /test/reset</c>. Tests are NOT in the shared
/// "Integration" collection because each test needs its own factory with a
/// distinct startup configuration (env + Testing:Enabled). Placed in the
/// "ResetTests" collection so they run serially with each other.
/// </summary>
[Collection("ResetTests")]
public class ResetTestStateEndpointTests(
    ResetEndpointEnabledFactory enabledFactory,
    ResetEndpointDisabledFactory disabledFactory,
    ResetEndpointProductionFactory productionFactory)
{
    // The enabled factory is reused across multiple tests in this class.
    private readonly ResetEndpointEnabledFactory _enabledFactory = enabledFactory;

    // ── Happy path ──────────────────────────────────────────────────────────

    /// <summary>
    /// Testing:Enabled=true + Development → POST /test/reset returns 204, QaSeedRunner users are
    /// present with their stable GUIDs, and the main QA training plan is seeded with its
    /// ForTime / AMRAP / Standard / Tabata sections.
    /// </summary>
    [Fact]
    public async Task Reset_EnabledInDevelopment_Returns204AndSeedsQaUsersAndTrainingPlan()
    {
        var client = _enabledFactory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var response = await client.PostAsync("/test/reset", null, ct);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = _enabledFactory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var qaClient = await userManager.FindByEmailAsync(QaSeedRunner.ClientEmail);
        qaClient.Should().NotBeNull(because: "QaSeedRunner must create the QA client user");
        qaClient!.Id.Should().Be(QaSeedRunner.ClientUserId,
            because: "QaSeedRunner assigns a stable GUID to the QA client user");

        var qaTrainer = await userManager.FindByEmailAsync(QaSeedRunner.TrainerEmail);
        qaTrainer.Should().NotBeNull(because: "QaSeedRunner must create the QA trainer user");
        qaTrainer!.Id.Should().Be(QaSeedRunner.TrainerUserId);

        var qaNutri = await userManager.FindByEmailAsync(QaSeedRunner.NutriEmail);
        qaNutri.Should().NotBeNull(because: "QaSeedRunner must create the QA nutritionist user");
        qaNutri!.Id.Should().Be(QaSeedRunner.NutriUserId);

        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        var plan = await mongo.TrainingPlans
            .Find(p => p.ExternalId == QaSeedRunner.QaTrainingPlanExternalId)
            .FirstOrDefaultAsync(ct);

        plan.Should().NotBeNull(because: "QaSeedRunner must create the QA training plan");

        // ClientId must be ClientUserId (ApplicationUser.Id) — post-#840/#845,
        // GetClientPlansEndpoint resolves clientProfile.UserId and filters Mongo
        // documents on that value, not on ClientProfile.PublicId.
        plan!.ClientId.Should().Be(QaSeedRunner.ClientUserId,
            because: "TrainingPlan.ClientId must equal ApplicationUser.Id for the plan to be visible via GET /client/plans");

        // Plan must be Active (not Draft) with at least one Published week.
        plan.Status.Should().Be(TrainingPlanStatus.Active);

        plan.Weeks.Should().HaveCount(1, because: "one week is seeded");

        var week = plan.Weeks[0];

        // GetClientPlansEndpoint:142 applies ElemMatch(w => w.Status == WeekStatus.Published).
        // A Draft week silently excludes the plan from the client response.
        week.Status.Should().Be(WeekStatus.Published,
            because: "week must be Published for GET /client/plans to return the plan");
        week.DatePublished.Should().NotBeNull(because: "published week must have a DatePublished timestamp");

        // Select by SessionId rather than taking the first of the list: the fixture also seeds a
        // standalone-only session and a standalone-plus-nested duplicate session (#857 phase 3a),
        // and this test is about the four workout formats on the main QA session only. Asserting a
        // session COUNT here would make every future fixture addition break an unrelated test.
        var sessions = week.Days.SelectMany(d => d.Sessions).ToList();
        var session = sessions.Should().ContainSingle(s => s.SessionId == QaSeedRunner.QaSessionId).Subject;

        session.Workouts.Should().HaveCount(4, because: "one ForTime section + one AMRAP section + one Standard section + one Tabata section");

        // Section 1 — ForTime + 0 exercises (the #258 bug shape).
        var section1 = session.Workouts[0];
        section1.Format.Should().Be(WorkoutFormat.ForTime,
            because: "Section 1 must be ForTime format");
        section1.FormatConfig.Should().NotBeNull();
        section1.FormatConfig!.TimeCapSeconds.Should().Be(1800,
            because: "ForTime section must have a 30-minute (1800s) time cap");
        section1.Exercises.Should().BeEmpty(
            because: "ForTime section intentionally has 0 exercises to exercise the #258 empty-exercise bug shape");

        // Section 2 — AMRAP + exercises (non-regression: format-with-exercises path).
        var section2 = session.Workouts[1];
        section2.Format.Should().Be(WorkoutFormat.AMRAP,
            because: "Section 2 must be AMRAP format");
        section2.Exercises.Should().HaveCountGreaterThanOrEqualTo(1,
            because: "AMRAP section must have at least one exercise for non-regression");

        // Section 3 — Standard (null format) + exercises (non-regression: no-format path).
        var section3 = session.Workouts[2];
        section3.Format.Should().BeNull(
            because: "Section 3 must be Standard (null format)");
        section3.Exercises.Should().HaveCountGreaterThanOrEqualTo(1,
            because: "Standard section must have at least one exercise for non-regression");

        // Section 4 — Tabata 20s/10s × 8 + 1 exercise (#327 iOS QA fixture).
        var section4 = session.Workouts[3];
        section4.Format.Should().Be(WorkoutFormat.Tabata,
            because: "Section 4 must be Tabata format");
        section4.Order.Should().Be(3,
            because: "Tabata section must be Order=3");
        section4.Name.Should().Be("Tabata test",
            because: "Tabata section must have the seeded name");
        section4.FormatConfig.Should().NotBeNull();
        section4.FormatConfig!.WorkSeconds.Should().Be(20,
            because: "Tabata FormatConfig must have WorkSeconds=20");
        section4.FormatConfig.RestSeconds.Should().Be(10,
            because: "Tabata FormatConfig must have RestSeconds=10");
        section4.FormatConfig.TotalRounds.Should().Be(8,
            because: "Tabata FormatConfig must have TotalRounds=8");
        section4.Exercises.Should().HaveCount(1,
            because: "Tabata section must have exactly one seeded exercise");
    }

    // ── Idempotency + Mongo index recreation (#1124) ────────────────────────

    /// <summary>
    /// Two consecutive POSTs to /test/reset both return 204 and converge on the same state:
    /// the QA users keep their stable GUIDs, the training plan is not duplicated, and the reset
    /// path (not the catalog seeder) recreates the foodTags indexes it dropped — the unique
    /// per-owner normalized-name index exists after the first reset, and the index-name set is
    /// identical after the second.
    /// </summary>
    [Fact]
    public async Task Reset_CalledTwice_IsIdempotentAndRecreatesFoodTagIndexes()
    {
        var client = _enabledFactory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var first = await client.PostAsync("/test/reset", null, ct);
        first.StatusCode.Should().Be(HttpStatusCode.NoContent, because: "first reset must succeed");

        using var scope = _enabledFactory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        var indexesAfterFirst = await (await mongo.FoodTags.Indexes.ListAsync(ct)).ToListAsync(ct);

        var ownerNameIndex = indexesAfterFirst.SingleOrDefault(
            i => i["name"].AsString == "idx_foodtag_ownerUserId_normalizedName");
        ownerNameIndex.Should().NotBeNull(
            because: "MongoIndexInitializer.StartAsync must recreate the foodTags indexes after the reset drops the collection");
        ownerNameIndex!["unique"].AsBoolean.Should().BeTrue(
            because: "the per-owner normalized-name constraint must stay unique after the reset");

        var namesAfterFirst = indexesAfterFirst.Select(i => i["name"].AsString).OrderBy(n => n).ToList();

        var second = await client.PostAsync("/test/reset", null, ct);
        second.StatusCode.Should().Be(HttpStatusCode.NoContent, because: "second reset must be idempotent");

        var namesAfterSecond = (await (await mongo.FoodTags.Indexes.ListAsync(ct)).ToListAsync(ct))
            .Select(i => i["name"].AsString)
            .OrderBy(n => n)
            .ToList();
        namesAfterSecond.Should().Equal(namesAfterFirst,
            because: "a second reset must recreate the exact same index set, not accumulate or drop indexes");

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var qaClient = await userManager.FindByEmailAsync(QaSeedRunner.ClientEmail);
        qaClient.Should().NotBeNull();
        qaClient!.Id.Should().Be(QaSeedRunner.ClientUserId,
            because: "stable GUID must be preserved after re-seed");

        var planCount = await mongo.TrainingPlans.CountDocumentsAsync(
            Builders<FormUp.Application.Domain.Documents.TrainingPlan>.Filter
                .Eq(p => p.ExternalId, QaSeedRunner.QaTrainingPlanExternalId),
            cancellationToken: ct);
        planCount.Should().Be(1, because: "EnsureTrainingPlanAsync must be idempotent — no duplicate plan documents");
    }

    // ── Gate: Testing:Enabled=false ─────────────────────────────────────────

    /// <summary>
    /// Testing:Enabled=false → request-time gate rejects the call with 404.
    /// The endpoint IS registered in the route table (FastEndpoints 8.x builds
    /// the static route table once per process, so registration-time filters
    /// would poison other factory instances in the same test run). The gate is
    /// enforced per-request inside HandleAsync.
    /// </summary>
    [Fact]
    public async Task Reset_TestingDisabled_GateRejects_Returns404()
    {
        var client = disabledFactory.CreateClient();
        var response = await client.PostAsync("/test/reset", null, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            because: "request-time gate must reject the call when Testing:Enabled=false");
    }

    // ── Gate: Non-Development environment with Testing:Enabled=true ────────────

    /// <summary>
    /// Testing:Enabled=true + non-Development environment → endpoint succeeds (204).
    /// The environment name is no longer checked — Testing:Enabled is the sole gate.
    /// This verifies CI test harnesses that use non-Development environment names
    /// (e.g. "Testing", "Staging") can call /test/reset as long as the flag is set.
    /// </summary>
    [Fact]
    public async Task Reset_TestingEnabled_NonDevelopment_Succeeds()
    {
        var client = productionFactory.CreateClient();
        var response = await client.PostAsync("/test/reset", null, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent,
            because: "Testing:Enabled=true is the sole gate; environment name must not block the call");
    }
}
