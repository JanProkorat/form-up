using FluentAssertions;
using FormUp.Application.Domain.Common;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Application.Infrastructure.Services;
using FormUp.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;

namespace FormUp.Tests.Endpoints.SessionExecutions;

// ── Test factory ─────────────────────────────────────────────────────────────

/// <summary>
/// Dedicated factory for WorkoutLog uniqueness tests. Uses the shared Postgres/Mongo
/// Testcontainers (#1104 Phase B — see <see cref="SharedTestContainers"/>), each in its own
/// database, to avoid polluting the shared Integration collection's data with scenarios that
/// modify existing data.
/// </summary>
public class WorkoutLogUniquenessFactory(SharedTestContainers sharedContainers)
    : WebApplicationFactory<Program>, IAsyncLifetime
{
    private string _postgresConnectionString = string.Empty;
    private string _mongoDatabaseName = string.Empty;

    /// <summary>
    /// The IMongoContext resolved from the running host's DI container.
    /// Available after <see cref="InitializeAsync"/> completes.
    /// </summary>
    public IMongoContext? MongoContext { get; private set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("POSTGRES_PASSWORD", "test");
        builder.UseSetting("MONGO_PASSWORD", "test");
        builder.UseSetting("MINIO_ACCESS_KEY", "test");
        builder.UseSetting("MINIO_SECRET_KEY", "test");
        builder.UseSetting("JWT_SECRET", new string('x', 64));
        builder.UseSetting("RateLimiting:Disabled", "true");

        builder.UseSetting("ConnectionStrings:PostgreSQl",
            "Host=localhost;Database=placeholder;Username=postgres");
        builder.UseSetting("ConnectionStrings:MongoDB",
            "mongodb://localhost:27017");

        builder.ConfigureServices(services =>
        {
            // Replace DbContext with Testcontainer-backed Postgres.
            var pgDesc = services.SingleOrDefault(
                d => d.ServiceType == typeof(Microsoft.EntityFrameworkCore.DbContextOptions<Application.Infrastructure.Data.ApplicationDbContext>));
            if (pgDesc is not null) services.Remove(pgDesc);

            services.AddDbContext<Application.Infrastructure.Data.ApplicationDbContext>(options =>
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
                d => d.ServiceType == typeof(Application.Domain.Interfaces.IEmailService));
            if (emailDesc is not null) services.Remove(emailDesc);
            services.AddSingleton<FakeEmailService>();
            services.AddSingleton<Application.Domain.Interfaces.IEmailService>(
                sp => sp.GetRequiredService<FakeEmailService>());

            var notifierDesc = services.SingleOrDefault(
                d => d.ServiceType == typeof(Application.Domain.Interfaces.IRealtimeNotifier));
            if (notifierDesc is not null) services.Remove(notifierDesc);
            services.AddSingleton<FakeRealtimeNotifier>();
            services.AddSingleton<Application.Domain.Interfaces.IRealtimeNotifier>(
                sp => sp.GetRequiredService<FakeRealtimeNotifier>());

            var blobDesc = services.SingleOrDefault(
                d => d.ServiceType == typeof(Application.Domain.Interfaces.IBlobStorageService));
            if (blobDesc is not null) services.Remove(blobDesc);
            services.AddSingleton<Application.Domain.Interfaces.IBlobStorageService, FakeBlobStorageService>();

            var pushDesc = services.SingleOrDefault(
                d => d.ServiceType == typeof(Application.Domain.Interfaces.IPushNotificationService));
            if (pushDesc is not null) services.Remove(pushDesc);
            services.AddSingleton<FakePushNotificationService>();
            services.AddSingleton<Application.Domain.Interfaces.IPushNotificationService>(
                sp => sp.GetRequiredService<FakePushNotificationService>());

            // #726: prevent the background schedulers/worker from starting in this
            // test host — see TestHostedServiceExtensions for the root cause.
            services.RemoveBackgroundHostedServices();
        });

        builder.UseEnvironment("Development");
    }

    public async ValueTask InitializeAsync()
    {
        _postgresConnectionString = await sharedContainers.CreatePostgresDatabaseAsync("workoutlog_uniqueness");
        _mongoDatabaseName = SharedTestContainers.CreateMongoDatabaseName("workoutlog_uniqueness");

        FastEndpointsBootGate.Start(this);

        await Application.Infrastructure.Data.ApplicationDbContextSeed.SeedAsync(Services);

        using var scope = Services.CreateScope();
        MongoContext = scope.ServiceProvider.GetRequiredService<IMongoContext>();
    }

    // Intentionally skip base.DisposeAsync() — see FitnessApiFactory's matching remark. No
    // Testcontainer to dispose here anymore (#1104 Phase B); SharedTestContainers owns that.
    public new ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

// ── Collection definition ─────────────────────────────────────────────────────

/// <summary>
/// Boots <see cref="WorkoutLogUniquenessFactory"/> ONCE for the whole collection
/// rather than per test (#1104). Safe without an explicit reset because every fact
/// generates its own unique client/session GUIDs (see the class remarks below) —
/// the same isolation-by-unique-data pattern the shared "Integration" collection
/// already relies on for its ~106 files.
/// </summary>
[CollectionDefinition("WorkoutLogUniqueness")]
public class WorkoutLogUniquenessCollection : ICollectionFixture<WorkoutLogUniquenessFactory>;

// ── Tests ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Integration tests for the date-scoped partial unique index on WorkoutLog that
/// closes the TOCTOU race where two concurrent completions of the same session
/// on the same day would create duplicate completed logs.
///
/// Runs in its own collection + factory (separate Testcontainers) to isolate
/// these index scenarios from the shared Integration collection.
/// </summary>
[Collection("WorkoutLogUniqueness")]
public class WorkoutLogCompletionUniquenessTests(WorkoutLogUniquenessFactory factory, SharedTestContainers sharedContainers)
{
    private readonly WorkoutLogUniquenessFactory _factory = factory;

    private IMongoContext Mongo
    {
        get
        {
            using var scope = _factory.Services.CreateScope();
            return scope.ServiceProvider.GetRequiredService<IMongoContext>();
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static DateTime Midnight(DateTime instant) =>
        SessionExecution.ToCompletionDateUtc(instant);

    /// <summary>
    /// Builds a test <see cref="SessionExecution"/> for the unified-index tests (#841).
    /// <paramref name="sessionId"/> is nullable so the ad-hoc (no-session) shape can be
    /// built — see <see cref="PartialIndex_NullSessionId_DoesNotTripIndex"/>.
    /// </summary>
    private static SessionExecution BuildExecution(
        Guid clientId,
        Guid? sessionId,
        bool isCompleted,
        DateTime date,
        DateTime? completedAt = null)
    {
        var now = DateTime.UtcNow;
        return new SessionExecution
        {
            ExternalId = Guid.NewGuid(),
            ClientId = clientId,
            SessionId = sessionId,
            Date = date,
            Status = isCompleted ? SessionExecutionStatus.Completed : SessionExecutionStatus.Partial,
            Performance = new SessionExecutionPerformance
            {
                StartedAt = now.AddMinutes(-30),
                CompletedAt = completedAt,
                Workouts = []
            },
            DateCreated = now.AddMinutes(-30),
            Version = 1
        };
    }

    // ── (3) Index exists after startup init ───────────────────────────────────

    /// <summary>
    /// The partial unique index must be present in the SessionExecutions collection
    /// after MongoIndexInitializer.StartAsync() runs on host startup.
    /// </summary>
    /// <remarks>
    /// This is the only assertion anywhere that any <c>idx_sessionexecution_*</c> index is
    /// actually created at boot. The two CompleteAsync tests below exercise the uniqueness
    /// <em>constraint</em> behaviourally, which is a different property — they would still
    /// pass if the index were created under a different name, or by a different code path.
    /// </remarks>
    [Fact]
    public async Task PartialUniqueIndex_ExistsAfterInit()
    {
        var ct = TestContext.Current.CancellationToken;

        // Program.cs invokes MongoIndexInitializer explicitly (awaited) before
        // app.Run() — not via AddHostedService (see MongoIndexInitializer's class
        // remarks for why). WebApplicationFactory<Program> runs that same
        // top-level statement when this factory boots, so by the time we get a
        // scope here the index is already guaranteed to exist.
        // We just need to verify the index is present.
        using var scope = _factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        var indexCursor = await mongo.SessionExecutions.Indexes.ListAsync(cancellationToken: ct);
        var indexes = await indexCursor.ToListAsync(ct);

        var expectedName = "idx_sessionexecution_clientId_sessionId_date_unique";
        indexes.Should().Contain(
            idx => idx["name"].AsString == expectedName,
            $"the partial unique index '{expectedName}' must be created at startup");
    }

    // ── (1) Same-day concurrent double-complete → 409 ────────────────────────
    //
    // #841: IWorkoutCompletionService.CompleteAsync now takes a SessionExecution, and the
    // uniqueness guard moved to the unified (clientId, sessionId, date) partial index on
    // SessionExecutions — see idx_sessionexecution_clientId_sessionId_date_unique in
    // MongoIndexInitializer.CreateSessionExecutionIndexes. These two tests exercise that
    // unified index directly (superseding the retired WorkoutLog-level assertions).

    /// <summary>
    /// When two concurrent requests both complete the same (ClientId, SessionId) on the
    /// same calendar day, exactly one succeeded execution must exist and the loser must get
    /// <see cref="WorkoutAlreadyCompletedException"/> (surfaced as HTTP 409).
    ///
    /// We simulate the race by inserting a completed execution directly (bypassing the service)
    /// and then calling CompleteAsync on a second execution with the same key.
    /// </summary>
    [Fact]
    public async Task CompleteAsync_SameDaySameSession_SecondCallThrowsWorkoutAlreadyCompletedException()
    {
        var ct = TestContext.Current.CancellationToken;
        var clientId  = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var today     = DateTime.UtcNow;
        var midnightToday = SessionExecution.ToCompletionDateUtc(today);

        using var scope = _factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        // Insert the "winner" — already completed today.
        var winner = BuildExecution(
            clientId: clientId, sessionId: sessionId,
            isCompleted: true, completedAt: today, date: midnightToday);
        await mongo.SessionExecutions.InsertOneAsync(winner, cancellationToken: ct);

        // The "loser" is in-progress and currently keyed to a DIFFERENT day (e.g. a session
        // started yesterday and still open) so its own insert doesn't collide with the winner.
        // Completing it "today" below moves its Date to today's key, which is where the TOCTOU
        // race against the winner actually happens — the unique index rejects the REPLACE, not
        // the initial insert (Date is always present, so two same-day docs can never coexist
        // even in draft form; the real race is a still-open draft from a prior day converging
        // onto today's key at completion time).
        var loser = BuildExecution(
            clientId: clientId, sessionId: sessionId, isCompleted: false,
            date: SessionExecution.ToCompletionDateUtc(today.AddDays(-1)));
        await mongo.SessionExecutions.InsertOneAsync(loser, cancellationToken: ct);

        var svc = scope.ServiceProvider.GetRequiredService<Application.Domain.Interfaces.IWorkoutCompletionService>();

        // CompleteAsync on the loser must throw WorkoutAlreadyCompletedException because
        // the partial unique index (clientId, sessionId, date) rejects the second completed
        // execution for the same triplet on the same day.
        var act = async () => await svc.CompleteAsync(loser, today, TimeZoneInfo.Utc, ct);
        await act.Should().ThrowAsync<WorkoutAlreadyCompletedException>();

        // Exactly one completed execution must exist for this session on this day.
        var completedCount = await mongo.SessionExecutions.CountDocumentsAsync(
            Builders<SessionExecution>.Filter.Eq(e => e.Status, SessionExecutionStatus.Completed)
            & Builders<SessionExecution>.Filter.Eq(e => e.ClientId, clientId)
            & Builders<SessionExecution>.Filter.Eq(e => e.SessionId, sessionId),
            cancellationToken: ct);
        completedCount.Should().Be(1, "only one completed execution per (clientId, sessionId, day) is allowed");
    }

    // ── (2) Different-day re-completion is ALLOWED ────────────────────────────

    /// <summary>
    /// A different-day re-completion of the same (ClientId, SessionId) MUST succeed.
    /// The unique index is scoped to Date — two distinct midnight-UTC values
    /// for the same (clientId, sessionId) are two distinct index keys.
    /// </summary>
    [Fact]
    public async Task CompleteAsync_DifferentDaySameSession_BothLogsAllowed()
    {
        var ct = TestContext.Current.CancellationToken;
        var clientId  = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var day1      = DateTime.UtcNow.AddDays(-7);
        var day2      = DateTime.UtcNow; // different day

        using var scope = _factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        // Insert day-1 completed execution directly.
        var firstExecution = BuildExecution(
            clientId: clientId, sessionId: sessionId,
            isCompleted: true, completedAt: day1, date: SessionExecution.ToCompletionDateUtc(day1));
        await mongo.SessionExecutions.InsertOneAsync(firstExecution, cancellationToken: ct);

        // Second execution: in-progress, same clientId/sessionId, completed on day2.
        var secondExecution = BuildExecution(
            clientId: clientId, sessionId: sessionId, isCompleted: false,
            date: SessionExecution.ToCompletionDateUtc(day2));
        await mongo.SessionExecutions.InsertOneAsync(secondExecution, cancellationToken: ct);

        var svc = scope.ServiceProvider.GetRequiredService<Application.Domain.Interfaces.IWorkoutCompletionService>();

        // Must NOT throw — different day means a different index key.
        var act = async () => await svc.CompleteAsync(secondExecution, day2, TimeZoneInfo.Utc, ct);
        await act.Should().NotThrowAsync<WorkoutAlreadyCompletedException>(
            "different-day re-completions of the same session are valid");

        // Both completed executions must coexist.
        var completedCount = await mongo.SessionExecutions.CountDocumentsAsync(
            Builders<SessionExecution>.Filter.Eq(e => e.Status, SessionExecutionStatus.Completed)
            & Builders<SessionExecution>.Filter.Eq(e => e.ClientId, clientId)
            & Builders<SessionExecution>.Filter.Eq(e => e.SessionId, sessionId),
            cancellationToken: ct);
        completedCount.Should().Be(2, "one completed execution per distinct day is valid");
    }

    // ── (6) Logs with null PlanId/SessionId don't trip the index ─────────────

    /// <summary>
    /// Ad-hoc executions (no SessionId — a free workout not tied to a planned session) must
    /// stay outside the partial unique index, so a client can log more than one of them on
    /// the same calendar day.
    /// </summary>
    /// <remarks>
    /// This pins a load-bearing single-attribute invariant. The partial filter is
    /// <c>Exists(sessionId) &amp; Exists(date)</c>, and ad-hoc executions escape it only because
    /// <see cref="SessionExecution.SessionId"/> carries <c>[BsonIgnoreIfNull]</c> — a null
    /// SessionId is omitted from the stored document entirely, so <c>Exists(sessionId)</c> is
    /// false. Drop that one attribute and the field would serialise as an explicit null,
    /// <c>Exists</c> would become true, and a client's second free workout of the day would be
    /// rejected as a duplicate. Nothing else in the suite asserts this.
    /// </remarks>
    [Fact]
    public async Task PartialIndex_NullSessionId_DoesNotTripIndex()
    {
        var ct = TestContext.Current.CancellationToken;
        var today = DateTime.UtcNow;

        var mongoClient = new MongoClient(sharedContainers.MongoConnectionString);
        var db = mongoClient.GetDatabase(SharedTestContainers.CreateMongoDatabaseName("null_key"));
        var executionsColl = db.GetCollection<SessionExecution>("sessionExecutions");

        var clientId = Guid.NewGuid();
        var date = Midnight(today);

        // Two pre-existing ad-hoc executions on the same (clientId, date).
        await executionsColl.InsertManyAsync(
            [
                BuildExecution(clientId, sessionId: null, isCompleted: true, date: date, completedAt: today),
                BuildExecution(clientId, sessionId: null, isCompleted: true, date: date, completedAt: today)
            ],
            cancellationToken: ct);

        // Index init must succeed over that data — the Exists guard excludes them.
        var initializer = new MongoIndexInitializer(
            new IndexInitMongoContext(db), NullLogger<MongoIndexInitializer>.Instance);

        var act = async () => await initializer.StartAsync(ct);
        await act.Should().NotThrowAsync(
            "ad-hoc executions with no SessionId must not trigger the partial unique index");

        // Stronger than index creation alone: the constraint must stay off them once the
        // index EXISTS. A third ad-hoc execution on the same (clientId, date) must insert.
        var insertThird = async () => await executionsColl.InsertOneAsync(
            BuildExecution(clientId, sessionId: null, isCompleted: true, date: date, completedAt: today),
            cancellationToken: ct);

        await insertThird.Should().NotThrowAsync(
            "the Exists(sessionId) guard must keep ad-hoc executions outside the unique index");

        (await executionsColl.CountDocumentsAsync(
            Builders<SessionExecution>.Filter.Eq(e => e.ClientId, clientId),
            cancellationToken: ct))
            .Should().Be(3, "all three ad-hoc executions coexist on the same calendar day");
    }
}

// ── Minimal IMongoContext for index-creation tests ────────────────────────────

/// <summary>
/// Minimal <see cref="IMongoContext"/> implementation for index-creation tests. Every
/// collection points at the same throwaway database, so running the full index initializer
/// against it is harmless.
/// </summary>
internal sealed class IndexInitMongoContext : IMongoContext
{
    private readonly IMongoDatabase _db;

    public IndexInitMongoContext(IMongoDatabase db) => _db = db;

    // The following collections are required by IMongoContext but unused in these tests.
    // They point at the same DB so index creation on those collections is harmless.
    public IMongoCollection<Food> Foods               => _db.GetCollection<Food>("foods");
    public IMongoCollection<NutritionPlan> NutritionPlans => _db.GetCollection<NutritionPlan>("nutritionPlans");
    public IMongoCollection<MealLog> MealLogs         => _db.GetCollection<MealLog>("mealLogs");
    public IMongoCollection<Exercise> Exercises       => _db.GetCollection<Exercise>("exercises");
    public IMongoCollection<Recipe> Recipes           => _db.GetCollection<Recipe>("recipes");
    public IMongoCollection<TrainingPlan> TrainingPlans => _db.GetCollection<TrainingPlan>("trainingPlans");
    public IMongoCollection<SessionExecution> SessionExecutions => _db.GetCollection<SessionExecution>("sessionExecutions");
    public IMongoCollection<PersonalRecord> PersonalRecords => _db.GetCollection<PersonalRecord>("personalRecords");
    public IMongoCollection<DayLog> DayLogs               => _db.GetCollection<DayLog>("dayLogs");
    public IMongoCollection<WorkoutTemplate> WorkoutTemplates => _db.GetCollection<WorkoutTemplate>("workoutTemplates");
    public IMongoCollection<SessionLock> SessionLocks         => _db.GetCollection<SessionLock>("sessionLocks");
    public IMongoCollection<SessionLog> SessionLogs           => _db.GetCollection<SessionLog>("sessionLogs");
    public IMongoCollection<TrainerNote> TrainerNotes         => _db.GetCollection<TrainerNote>("trainerNotes");
    public IMongoCollection<MealTemplate> MealTemplates => _db.GetCollection<MealTemplate>("mealTemplates");
    public IMongoCollection<NutritionPlanTemplate> NutritionPlanTemplates => _db.GetCollection<NutritionPlanTemplate>("nutritionPlanTemplates");
    public IMongoCollection<SessionTemplate> SessionTemplates => _db.GetCollection<SessionTemplate>("sessionTemplates");
    public IMongoCollection<TrainingPlanTemplate> TrainingPlanTemplates => _db.GetCollection<TrainingPlanTemplate>("trainingPlanTemplates");
    public IMongoCollection<FoodTag> FoodTags => _db.GetCollection<FoodTag>("foodTags");
    public IMongoCollection<FoodTagAssignment> FoodTagAssignments => _db.GetCollection<FoodTagAssignment>("foodTagAssignments");
    public IMongoCollection<RecipeTagAssignment> RecipeTagAssignments => _db.GetCollection<RecipeTagAssignment>("recipeTagAssignments");
    public IMongoCollection<ShoppingListTick> ShoppingListTicks => _db.GetCollection<ShoppingListTick>("shoppingListTicks");
    public IMongoCollection<ProcessedClientRequest> ProcessedClientRequests => _db.GetCollection<ProcessedClientRequest>("processedClientRequests");
    public IMongoCollection<OfflineSyncConflict> OfflineSyncConflicts => _db.GetCollection<OfflineSyncConflict>("offlineSyncConflicts");
}
