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
using MongoDB.Bson;
using MongoDB.Driver;

namespace FormUp.Tests.Endpoints.Trainers;

/// <summary>
/// Integration tests for GET /trainer/clients/{ClientId}/timeline.
/// Focus: the <c>personal_record</c> projection added in issue #14a.
/// Uses real PostgreSQL + MongoDB via Testcontainers to validate the full
/// stack including the trainer-link guard and sort ordering.
/// </summary>
[Collection(TestCollection.Name)]
public class GetClientTimelineEndpointTests(FitnessApiFactory factory)
{
    private static string UniqueEmail(string tag) => $"{Guid.NewGuid():N}@timeline-{tag}.com";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    // ── shared setup helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Registers + logs in a trainer, resolves their ProfessionalProfile, and
    /// returns the authenticated HttpClient and the ProfessionalProfile.Id.
    /// </summary>
    private async Task<(HttpClient Http, long ProfessionalProfileId, Guid TrainerUserId)> SetupTrainerAsync()
    {
        var http = factory.CreateClient();
        var email = UniqueEmail("trainer");

        await TestHelpers.RegisterAsync(http, email, "TestPass1!", "Test", "Trainer", "Trainer");
        await factory.GrantRoleAsync(email, "Nutritionist");
        var (token, _) = await TestHelpers.LoginAsync(http, email, "TestPass1!");
        TestHelpers.SetBearerToken(http, token);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var user = await db.Users.FirstAsync(
            u => u.Email == email,
            TestContext.Current.CancellationToken);

        var profile = await db.ProfessionalProfiles.FirstAsync(
            p => p.UserId == user.Id,
            TestContext.Current.CancellationToken);

        return (http, profile.Id, user.Id);
    }

    /// <summary>
    /// Registers + logs in a client, resolves their ClientProfile, and returns
    /// the ClientProfile.PublicId (used in the route) and the UserId
    /// (used in MongoDB document ClientId fields).
    /// </summary>
    private async Task<(Guid ClientProfilePublicId, long ClientProfileId, Guid ClientUserId)>
        SetupClientAsync()
    {
        var http = factory.CreateClient();
        var email = UniqueEmail("client");

        await TestHelpers.RegisterAsync(http, email, "TestPass1!", "Test", "Client", "Client");
        var (token, _) = await TestHelpers.LoginAsync(http, email, "TestPass1!");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var user = await db.Users.FirstAsync(
            u => u.Email == email,
            TestContext.Current.CancellationToken);

        var profile = await db.ClientProfiles.FirstAsync(
            cp => cp.UserId == user.Id,
            TestContext.Current.CancellationToken);

        return (profile.PublicId, profile.Id, user.Id);
    }

    /// <summary>
    /// Creates an active ClientProfessionalLink between the given trainer and client
    /// profiles, inserted directly into Postgres (bypassing the invite flow).
    /// Both capability flags are granted by default so the pre-existing tests in this
    /// file (ordering, PR payload structure, dedup) exercise the merged-timeline behaviour
    /// without being entangled with the #916 per-domain gating — see
    /// <see cref="FormUp.Tests.Endpoints.Authorization.CrossDomainPlanAccessTests"/>
    /// for the single-flag gating coverage.
    /// </summary>
    private Task LinkTrainerToClientAsync(long trainerProfileId, long clientProfileId) =>
        LinkTrainerToClientAsync(trainerProfileId, clientProfileId, canViewNutritionPlans: true, canViewTrainingPlans: true);

    /// <summary>
    /// Overload allowing the caller to pin the link's per-domain capability flags — used by the
    /// AC#5 gating cases, which must prove a link granting only one domain hides the other
    /// domain's plan-publish event even though a published plan for it exists.
    /// </summary>
    private async Task LinkTrainerToClientAsync(
        long trainerProfileId, long clientProfileId, bool canViewNutritionPlans, bool canViewTrainingPlans)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.ClientProfessionalLinks.Add(new ClientProfessionalLink
        {
            PublicId = Guid.NewGuid(),
            ProfessionalProfileId = trainerProfileId,
            ClientProfileId = clientProfileId,
            ProfessionalRole = UserRole.Trainer,
            ConnectionState = ConnectionState.Connected,
            CanViewTrainingPlans = canViewTrainingPlans,
            CanViewNutritionPlans = canViewNutritionPlans,
            DateCreated = DateTime.UtcNow,
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Inserts a PersonalRecord document directly into MongoDB for the given client.
    /// </summary>
    private static async Task InsertPersonalRecordAsync(
        IMongoContext mongo,
        Guid clientUserId,
        string exerciseName,
        decimal weightKg,
        int reps,
        DateTime achievedAt)
    {
        var pr = new PersonalRecord
        {
            Id = ObjectId.GenerateNewId(),
            ExternalId = Guid.NewGuid(),
            ClientId = clientUserId,
            ExerciseExternalId = Guid.NewGuid(),
            ExerciseName = exerciseName,
            WeightKg = weightKg,
            Reps = reps,
            AchievedAt = achievedAt,
            WorkoutLogId = Guid.NewGuid(),
            SetNumber = 1,
            Version = 1,
            DateCreated = DateTime.UtcNow,
        };

        await mongo.PersonalRecords.InsertOneAsync(
            pr, cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Registers a nutritionist plus a client they are actively linked to (both capability
    /// flags granted), and resolves the client's <see cref="ClientProfile.PublicId"/>
    /// — the identifier the timeline route takes, distinct from the <c>ApplicationUser.Id</c>
    /// that <see cref="TestHelpers.RegisterLinkedClientAsync"/> returns.
    /// </summary>
    private async Task<(HttpClient Http, Guid NutritionistUserId, Guid ClientUserId, Guid ClientPublicId)>
        RegisterNutritionistWithLinkedClientAsync()
    {
        var http = factory.CreateClient();
        var email = UniqueEmail("nutritionist");
        await TestHelpers.RegisterAsync(http, email, "TestPass1!", "Timeline", "Nutritionist", "Nutritionist");
        await factory.GrantRoleAsync(email, "Trainer");
        var (token, _) = await TestHelpers.LoginAsync(http, email, "TestPass1!");
        TestHelpers.SetBearerToken(http, token);

        Guid nutritionistUserId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await db.Users.FirstAsync(u => u.Email == email, TestContext.Current.CancellationToken);
            nutritionistUserId = user.Id;
        }

        var clientUserId = await TestHelpers.RegisterLinkedClientAsync(
            factory, nutritionistUserId, TestContext.Current.CancellationToken);

        Guid clientPublicId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var clientProfile = await db.ClientProfiles.FirstAsync(
                cp => cp.UserId == clientUserId, TestContext.Current.CancellationToken);
            clientPublicId = clientProfile.PublicId;
        }

        return (http, nutritionistUserId, clientUserId, clientPublicId);
    }

    /// <summary>
    /// Trainer-side equivalent of <see cref="RegisterNutritionistWithLinkedClientAsync"/>.
    /// </summary>
    private async Task<(HttpClient Http, Guid TrainerUserId, Guid ClientUserId, Guid ClientPublicId)>
        RegisterTrainerWithLinkedClientAsync()
    {
        var http = factory.CreateClient();
        var email = UniqueEmail("timeline-trainer");
        await TestHelpers.RegisterAsync(http, email, "TestPass1!", "Timeline", "Trainer", "Trainer");
        await factory.GrantRoleAsync(email, "Nutritionist");
        var (token, _) = await TestHelpers.LoginAsync(http, email, "TestPass1!");
        TestHelpers.SetBearerToken(http, token);

        Guid trainerUserId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await db.Users.FirstAsync(u => u.Email == email, TestContext.Current.CancellationToken);
            trainerUserId = user.Id;
        }

        var clientUserId = await TestHelpers.RegisterLinkedClientAsync(
            factory, trainerUserId, TestContext.Current.CancellationToken);

        Guid clientPublicId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var clientProfile = await db.ClientProfiles.FirstAsync(
                cp => cp.UserId == clientUserId, TestContext.Current.CancellationToken);
            clientPublicId = clientProfile.PublicId;
        }

        return (http, trainerUserId, clientUserId, clientPublicId);
    }

    private static NutritionPlan BuildDraftNutritionPlan(
        Guid nutritionistId, Guid clientId, int weekCount = 2) => new()
    {
        ExternalId = Guid.NewGuid(),
        ClientId = clientId,
        NutritionistId = nutritionistId,
        Name = "Timeline HTTP Publish Nutrition Plan",
        Status = NutritionPlanStatus.Draft,
        StartDate = DateTime.UtcNow.Date,
        Version = 1,
        DateCreated = DateTime.UtcNow.AddDays(-1),
        Weeks = Enumerable.Range(1, weekCount)
            .Select(w => new PlanWeek { WeekNumber = w, Status = WeekStatus.Draft })
            .ToList()
    };

    private static TrainingPlan BuildDraftTrainingPlan(
        Guid trainerId, Guid clientId, int weekCount = 2) => new()
    {
        ExternalId = Guid.NewGuid(),
        ClientId = clientId,
        TrainerId = trainerId,
        Name = "Timeline HTTP Publish Training Plan",
        Status = TrainingPlanStatus.Draft,
        StartDate = DateTime.UtcNow.Date,
        Version = 1,
        DateCreated = DateTime.UtcNow.AddDays(-1),
        Weeks = Enumerable.Range(1, weekCount)
            .Select(w => new TrainingWeek { WeekNumber = w, Status = WeekStatus.Draft, Days = [] })
            .ToList()
    };

    private async Task SeedNutritionPlanAsync(NutritionPlan plan)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        await mongo.NutritionPlans.InsertOneAsync(plan, cancellationToken: TestContext.Current.CancellationToken);
    }

    private async Task SeedTrainingPlanAsync(TrainingPlan plan)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        await mongo.TrainingPlans.InsertOneAsync(plan, cancellationToken: TestContext.Current.CancellationToken);
    }

    private async Task<NutritionPlan> FetchNutritionPlanAsync(Guid externalId)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        return await mongo.NutritionPlans
            .Find(p => p.ExternalId == externalId)
            .FirstAsync(TestContext.Current.CancellationToken);
    }

    // ── test cases ────────────────────────────────────────────────────────────

    /// <summary>
    /// Test 1: A PR seeded for the linked client appears in the timeline with
    /// the correct type, icon, and structured payload.
    /// </summary>
    [Fact]
    public async Task Timeline_WithPrSeeded_ReturnsPrItemWithCorrectFields()
    {
        var (trainerHttp, trainerProfileId, _) = await SetupTrainerAsync();
        var (clientPublicId, clientProfileId, clientUserId) = await SetupClientAsync();
        await LinkTrainerToClientAsync(trainerProfileId, clientProfileId);

        var achievedAt = DateTime.UtcNow.AddDays(-2);
        var exerciseName = "Bench Press";
        var workoutLogId = Guid.NewGuid();
        var exerciseExternalId = Guid.NewGuid();
        var prExternalId = Guid.NewGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
            await mongo.PersonalRecords.InsertOneAsync(new PersonalRecord
            {
                Id = ObjectId.GenerateNewId(),
                ExternalId = prExternalId,
                ClientId = clientUserId,
                ExerciseExternalId = exerciseExternalId,
                ExerciseName = exerciseName,
                WeightKg = 100m,
                Reps = 5,
                AchievedAt = achievedAt,
                WorkoutLogId = workoutLogId,
                SetNumber = 1,
                Version = 1,
                DateCreated = DateTime.UtcNow,
            }, cancellationToken: TestContext.Current.CancellationToken);
        }

        var response = await trainerHttp.GetAsync(
            $"/trainer/clients/{clientPublicId}/timeline",
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<TimelineResponse>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken);

        var prItem = body!.Items.FirstOrDefault(i => i.Type == "personal_record");
        prItem.Should().NotBeNull("PR item must appear in the timeline");
        prItem!.Icon.Should().Be("🏆", "trophy icon is the agreed symbol for PRs");
        prItem.OccurredAt.Should().BeCloseTo(achievedAt, TimeSpan.FromSeconds(1));
        prItem.PersonalRecord.Should().NotBeNull("structured payload must be populated");
        prItem.PersonalRecord!.ExternalId.Should().Be(prExternalId);
        prItem.PersonalRecord.ExerciseExternalId.Should().Be(exerciseExternalId);
        prItem.PersonalRecord.ExerciseName.Should().Be(exerciseName);
        prItem.PersonalRecord.WeightKg.Should().Be(100m);
        prItem.PersonalRecord.Reps.Should().Be(5);
        prItem.PersonalRecord.WorkoutLogId.Should().Be(workoutLogId);
    }

    /// <summary>
    /// Test 2: When PRs are interleaved with other activity, sort is newest-first.
    /// Seeds: workout@T3 (newest), PR@T2, meal@T1 (oldest).
    /// Expected order in timeline: workout, pr, meal.
    /// </summary>
    [Fact]
    public async Task Timeline_PrsInterleavedWithOtherActivity_SortNewestFirst()
    {
        var (trainerHttp, trainerProfileId, _) = await SetupTrainerAsync();
        var (clientPublicId, clientProfileId, clientUserId) = await SetupClientAsync();
        await LinkTrainerToClientAsync(trainerProfileId, clientProfileId);

        var base_time = DateTime.UtcNow.AddDays(-10);
        var t1 = base_time;               // oldest — meal
        var t2 = base_time.AddHours(1);   // middle — PR
        var t3 = base_time.AddHours(2);   // newest — workout

        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

            // Seed a meal log at T1 (MealLog has no ExternalId/Version fields).
            // MealLog.ClientId is keyed on ApplicationUser.Id (#840, previously PublicId — see #650).
            await mongo.MealLogs.InsertOneAsync(new MealLog
            {
                Id = ObjectId.GenerateNewId(),
                ClientId = clientUserId,
                PlanId = Guid.NewGuid(),
                MealId = Guid.NewGuid(),
                EatenAt = t1,
                FoodsEaten = [],
            }, cancellationToken: TestContext.Current.CancellationToken);

            // Seed a PR at T2
            await InsertPersonalRecordAsync(mongo, clientUserId, "Squat", 120m, 3, t2);

            // Seed a completed workout at T3. #841: GetClientTimelineEndpoint reads the
            // unified SessionExecutions collection (filtered to Performance-bearing,
            // Status=Completed documents) instead of the retired WorkoutLogs collection.
            await mongo.SessionExecutions.InsertOneAsync(new SessionExecution
            {
                Id = ObjectId.GenerateNewId(),
                ExternalId = Guid.NewGuid(),
                ClientId = clientUserId,
                Date = SessionExecution.ToCompletionDateUtc(t3),
                Status = SessionExecutionStatus.Completed,
                Performance = new SessionExecutionPerformance
                {
                    StartedAt = t3.AddMinutes(-30),
                    CompletedAt = t3,
                    Workouts = [],
                },
                DateCreated = DateTime.UtcNow,
            }, cancellationToken: TestContext.Current.CancellationToken);
        }

        var response = await trainerHttp.GetAsync(
            $"/trainer/clients/{clientPublicId}/timeline?limit=100",
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<TimelineResponse>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken);

        var items = body!.Items
            .Where(i => i.Type is "workout" or "personal_record" or "meal_day")
            .ToList();

        items.Should().HaveCount(3, "all three seeded events must appear");
        items[0].Type.Should().Be("workout", "workout at T3 is newest");
        items[1].Type.Should().Be("personal_record", "PR at T2 is middle");
        items[2].Type.Should().Be("meal_day", "meal at T1 is oldest");
    }

    /// <summary>
    /// Test 3: Cross-client isolation — a PR owned by a different client is NOT
    /// surfaced in the linked client's timeline.
    /// </summary>
    [Fact]
    public async Task Timeline_PrOwnedByOtherClient_IsNotSurfaced()
    {
        var (trainerHttp, trainerProfileId, _) = await SetupTrainerAsync();
        var (clientPublicId, clientProfileId, clientUserId) = await SetupClientAsync();
        await LinkTrainerToClientAsync(trainerProfileId, clientProfileId);

        // A second, unlinked client
        var (_, _, otherClientUserId) = await SetupClientAsync();

        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
            // Insert a PR ONLY for the other client
            await InsertPersonalRecordAsync(
                mongo, otherClientUserId, "Deadlift", 200m, 1, DateTime.UtcNow.AddDays(-1));
        }

        var response = await trainerHttp.GetAsync(
            $"/trainer/clients/{clientPublicId}/timeline",
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<TimelineResponse>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken);

        body!.Items.Should().NotContain(
            i => i.Type == "personal_record",
            "the linked client has no PRs; only the other client does");
    }

    /// <summary>
    /// Test 4: Trainer-link guard — a trainer with no active link to the client
    /// receives 404, not the timeline data.
    /// </summary>
    [Fact]
    public async Task Timeline_TrainerWithNoLink_Returns404()
    {
        var (trainerHttp, _, _) = await SetupTrainerAsync();
        var (clientPublicId, _, clientUserId) = await SetupClientAsync();
        // Intentionally skip LinkTrainerToClientAsync

        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
            await InsertPersonalRecordAsync(
                mongo, clientUserId, "Pull-up", 0m, 10, DateTime.UtcNow.AddDays(-1));
        }

        var response = await trainerHttp.GetAsync(
            $"/trainer/clients/{clientPublicId}/timeline",
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "trainer has no active link, so the endpoint must not expose client data");
    }

    /// <summary>
    /// Test 5: Pagination still works when PRs are part of the unified item list.
    /// Seeds 12 PRs (more than the default limit of 30 would cap, but within
    /// the 90-day lookback). With limit=5 the endpoint must return exactly 5
    /// items, and all returned items with type personal_record must carry a
    /// populated PersonalRecord payload.
    /// </summary>
    [Fact]
    public async Task Timeline_PaginationWithPrs_RespectsLimit()
    {
        var (trainerHttp, trainerProfileId, _) = await SetupTrainerAsync();
        var (clientPublicId, clientProfileId, clientUserId) = await SetupClientAsync();
        await LinkTrainerToClientAsync(trainerProfileId, clientProfileId);

        var baseTime = DateTime.UtcNow.AddDays(-5);

        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

            // 12 PRs spaced 1 hour apart, all within the 90-day lookback
            for (var i = 1; i <= 12; i++)
            {
                await InsertPersonalRecordAsync(
                    mongo, clientUserId, $"Exercise {i}", 60m + i, 5,
                    baseTime.AddHours(i));
            }
        }

        // limit=5 — the 1 linked event + 12 PRs = 13 candidates, capped to 5
        var response = await trainerHttp.GetAsync(
            $"/trainer/clients/{clientPublicId}/timeline?limit=5",
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<TimelineResponse>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken);

        body!.Items.Should().HaveCount(5, "limit=5 caps the returned items");

        // Every personal_record item in the result must carry a non-null payload
        var prItems = body.Items.Where(i => i.Type == "personal_record").ToList();
        prItems.Should().NotBeEmpty("some PRs must appear within the top 5 items");
        prItems.Should().AllSatisfy(i => i.PersonalRecord.Should().NotBeNull(
            "personal_record items must always carry the structured payload"));

        // Items must be in descending OccurredAt order
        body.Items.Select(i => i.OccurredAt)
            .Should().BeInDescendingOrder("timeline is newest-first");
    }

    /// <summary>
    /// Test 6: Regression guard for #840. MealLog, NutritionPlan, and TrainingPlan are now
    /// all keyed on ApplicationUser.Id — the same identifier WorkoutLog and PersonalRecord
    /// already used (previously they were keyed on ClientProfile.PublicId; see #650, now
    /// superseded). GetClientTimelineEndpoint resolves and filters every Mongo collection by
    /// clientProfile.UserId, so documents seeded on UserId must appear and the (now-stale)
    /// PublicId key must NOT match anything.
    /// </summary>
    [Fact]
    public async Task Timeline_MealNutritionAndTrainingPlanSeededOnUserId_AllAppear()
    {
        var (trainerHttp, trainerProfileId, trainerUserId) = await SetupTrainerAsync();
        var (clientPublicId, clientProfileId, clientUserId) = await SetupClientAsync();
        await LinkTrainerToClientAsync(trainerProfileId, clientProfileId);

        var mealAt = DateTime.UtcNow.AddDays(-1);
        var nutritionPublishedAt = DateTime.UtcNow.AddDays(-2);
        var trainingPublishedAt = DateTime.UtcNow.AddDays(-3);
        var workoutAt = DateTime.UtcNow.AddDays(-4);

        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

            // MealLog, NutritionPlan, TrainingPlan — keyed on ApplicationUser.Id (#840).
            await mongo.MealLogs.InsertOneAsync(new MealLog
            {
                Id = ObjectId.GenerateNewId(),
                ClientId = clientUserId,
                PlanId = Guid.NewGuid(),
                MealId = Guid.NewGuid(),
                EatenAt = mealAt,
                FoodsEaten = [],
            }, cancellationToken: TestContext.Current.CancellationToken);

            // #1014: publish never writes the plan-level DatePublished field — only
            // weeks[].datePublished. The #840 keying guard below must still hold under the
            // week-level derivation, so these plans carry a single published week instead of
            // the plan-level field.
            await mongo.NutritionPlans.InsertOneAsync(new NutritionPlan
            {
                Id = ObjectId.GenerateNewId(),
                ExternalId = Guid.NewGuid(),
                ClientId = clientUserId,
                NutritionistId = trainerUserId,
                Name = "Timeline Nutrition Plan",
                Status = NutritionPlanStatus.Active,
                Weeks = [new PlanWeek { WeekNumber = 1, Status = WeekStatus.Published, DatePublished = nutritionPublishedAt }],
                Version = 1,
                DateCreated = DateTime.UtcNow,
            }, cancellationToken: TestContext.Current.CancellationToken);

            await mongo.TrainingPlans.InsertOneAsync(new TrainingPlan
            {
                Id = ObjectId.GenerateNewId(),
                ExternalId = Guid.NewGuid(),
                ClientId = clientUserId,
                TrainerId = trainerUserId,
                Name = "Timeline Training Plan",
                Status = TrainingPlanStatus.Active,
                Weeks = [new TrainingWeek { WeekNumber = 1, Status = WeekStatus.Published, DatePublished = trainingPublishedAt, Days = [] }],
                Version = 1,
                DateCreated = DateTime.UtcNow,
            }, cancellationToken: TestContext.Current.CancellationToken);

            // Workout — keyed on ApplicationUser.Id, unaffected by #840. #841: seeded into the
            // unified SessionExecutions collection (GetClientTimelineEndpoint reads that
            // exclusively, filtered to Performance-bearing, Status=Completed documents).
            await mongo.SessionExecutions.InsertOneAsync(new SessionExecution
            {
                Id = ObjectId.GenerateNewId(),
                ExternalId = Guid.NewGuid(),
                ClientId = clientUserId,
                Date = SessionExecution.ToCompletionDateUtc(workoutAt),
                Status = SessionExecutionStatus.Completed,
                Performance = new SessionExecutionPerformance
                {
                    StartedAt = workoutAt.AddMinutes(-30),
                    CompletedAt = workoutAt,
                    Workouts = [],
                },
                DateCreated = DateTime.UtcNow,
            }, cancellationToken: TestContext.Current.CancellationToken);
        }

        var response = await trainerHttp.GetAsync(
            $"/trainer/clients/{clientPublicId}/timeline?limit=100",
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<TimelineResponse>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken);

        body!.Items.Should().Contain(i => i.Type == "meal_day",
            "MealLog.ClientId is keyed on UserId (#840) — must be found when queried by UserId");
        body.Items.Should().Contain(i => i.Type == "nutrition_plan_published",
            "NutritionPlan.ClientId is keyed on UserId (#840) — must be found when queried by UserId");
        body.Items.Should().Contain(i => i.Type == "training_plan_published",
            "TrainingPlan.ClientId is keyed on UserId (#840) — must be found when queried by UserId");
        body.Items.Should().Contain(i => i.Type == "workout",
            "WorkoutLog was already keyed on UserId and must remain visible after #840");
    }

    /// <summary>
    /// Test 7 (#1014): publishing a nutrition plan week over the real HTTP publish endpoint
    /// must surface exactly one <c>nutrition_plan_published</c> timeline event. This is the
    /// load-bearing regression proof — the bug this issue fixes is that the timeline read a
    /// field (the plan-level <c>DatePublished</c>) that publish never writes, so a fixture that
    /// hand-seeds that same field would repeat the bug's own wrong assumption one level down.
    /// </summary>
    [Fact]
    public async Task Timeline_NutritionWeekPublishedViaHttp_ReturnsNutritionPlanPublishedEvent()
    {
        var (nutritionistHttp, nutritionistUserId, clientUserId, clientPublicId) =
            await RegisterNutritionistWithLinkedClientAsync();

        var plan = BuildDraftNutritionPlan(nutritionistUserId, clientUserId);
        await SeedNutritionPlanAsync(plan);

        var publishResponse = await nutritionistHttp.PostAsync(
            $"/nutrition/plans/{plan.ExternalId}/weeks/1/publish",
            JsonContent.Create(new { PlanId = plan.ExternalId, WeekNumber = 1 }),
            TestContext.Current.CancellationToken);

        publishResponse.StatusCode.Should().Be(
            HttpStatusCode.OK, "the publish itself must succeed before the timeline can be asserted");

        var response = await nutritionistHttp.GetAsync(
            $"/trainer/clients/{clientPublicId}/timeline?limit=100",
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<TimelineResponse>(
            JsonOptions, cancellationToken: TestContext.Current.CancellationToken);

        var item = body!.Items.FirstOrDefault(i => i.Type == "nutrition_plan_published");
        item.Should().NotBeNull(
            "publishing a week over the real HTTP publish endpoint must surface exactly one nutrition_plan_published timeline event");
        item!.Id.Should().Be($"nutrition_plan:{plan.ExternalId}:week:1");
    }

    /// <summary>
    /// Test 8 (#1014): training-domain equivalent of
    /// <see cref="Timeline_NutritionWeekPublishedViaHttp_ReturnsNutritionPlanPublishedEvent"/>.
    /// </summary>
    [Fact]
    public async Task Timeline_TrainingWeekPublishedViaHttp_ReturnsTrainingPlanPublishedEvent()
    {
        var (trainerHttp, trainerUserId, clientUserId, clientPublicId) =
            await RegisterTrainerWithLinkedClientAsync();

        var plan = BuildDraftTrainingPlan(trainerUserId, clientUserId);
        await SeedTrainingPlanAsync(plan);

        var publishResponse = await trainerHttp.PostAsync(
            $"/training/plans/{plan.ExternalId}/weeks/1/publish",
            JsonContent.Create(new { PlanId = plan.ExternalId, WeekNumber = 1 }),
            TestContext.Current.CancellationToken);

        publishResponse.StatusCode.Should().Be(
            HttpStatusCode.OK, "the publish itself must succeed before the timeline can be asserted");

        var response = await trainerHttp.GetAsync(
            $"/trainer/clients/{clientPublicId}/timeline?limit=100",
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<TimelineResponse>(
            JsonOptions, cancellationToken: TestContext.Current.CancellationToken);

        var item = body!.Items.FirstOrDefault(i => i.Type == "training_plan_published");
        item.Should().NotBeNull(
            "publishing a week over the real HTTP publish endpoint must surface exactly one training_plan_published timeline event");
        item!.Id.Should().Be($"training_plan:{plan.ExternalId}:week:1");
    }

    /// <summary>
    /// Test 9: a plan with weeks 3 and 1 published (3 first) surfaces ONE
    /// <c>nutrition_plan_published</c> event per published week, each dated at that week's own
    /// DatePublished, with a per-week id and the week number in the payload.
    /// </summary>
    [Fact]
    public async Task Timeline_MultipleWeeksPublished_EmitsOneEventPerWeek()
    {
        var (nutritionistHttp, nutritionistUserId, clientUserId, clientPublicId) =
            await RegisterNutritionistWithLinkedClientAsync();

        var plan = BuildDraftNutritionPlan(nutritionistUserId, clientUserId, weekCount: 3);
        await SeedNutritionPlanAsync(plan);

        var publishWeek3 = await nutritionistHttp.PostAsync(
            $"/nutrition/plans/{plan.ExternalId}/weeks/3/publish",
            JsonContent.Create(new { PlanId = plan.ExternalId, WeekNumber = 3 }),
            TestContext.Current.CancellationToken);
        publishWeek3.StatusCode.Should().Be(HttpStatusCode.OK);

        var publishWeek1 = await nutritionistHttp.PostAsync(
            $"/nutrition/plans/{plan.ExternalId}/weeks/1/publish",
            JsonContent.Create(new { PlanId = plan.ExternalId, WeekNumber = 1 }),
            TestContext.Current.CancellationToken);
        publishWeek1.StatusCode.Should().Be(HttpStatusCode.OK);

        var persisted = await FetchNutritionPlanAsync(plan.ExternalId);
        var week3PublishedAt = persisted.Weeks.First(w => w.WeekNumber == 3).DatePublished!.Value;
        var week1PublishedAt = persisted.Weeks.First(w => w.WeekNumber == 1).DatePublished!.Value;
        week3PublishedAt.Should().BeBefore(week1PublishedAt, "week 3 was published first in this test, before week 1");

        var response = await nutritionistHttp.GetAsync(
            $"/trainer/clients/{clientPublicId}/timeline?limit=100",
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<TimelineResponse>(
            JsonOptions, cancellationToken: TestContext.Current.CancellationToken);

        var nutritionEvents = body!.Items.Where(i => i.Type == "nutrition_plan_published").ToList();
        nutritionEvents.Should().HaveCount(2, "one event per published week; week 2 is still a draft");

        // Exact equality: the two publishes are back-to-back calls, so their gap can be a few
        // milliseconds. OccurredAt is the same persisted value read by FetchNutritionPlanAsync.
        var week3Event = nutritionEvents.Single(i => i.Id == $"nutrition_plan:{plan.ExternalId}:week:3");
        week3Event.OccurredAt.Should().Be(week3PublishedAt);
        week3Event.PlanPublished.Should().NotBeNull();
        week3Event.PlanPublished!.WeekNumber.Should().Be(3);
        week3Event.PlanPublished.PlanId.Should().Be(plan.ExternalId);
        week3Event.PlanPublished.PlanName.Should().Be(plan.Name);

        var week1Event = nutritionEvents.Single(i => i.Id == $"nutrition_plan:{plan.ExternalId}:week:1");
        week1Event.OccurredAt.Should().Be(week1PublishedAt);
        week1Event.PlanPublished!.WeekNumber.Should().Be(1);

        body.Items.Select(i => i.Id).Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// Test 10 (#1014): a plan with no published weeks at all emits no plan-publish event —
    /// new behaviour under the derived semantics (previously always zero events regardless, for
    /// the wrong reason).
    /// </summary>
    [Fact]
    public async Task Timeline_PlanWithNoPublishedWeeks_EmitsNoPlanPublishedEvent()
    {
        var (trainerHttp, trainerProfileId, _) = await SetupTrainerAsync();
        var (clientPublicId, clientProfileId, clientUserId) = await SetupClientAsync();
        await LinkTrainerToClientAsync(trainerProfileId, clientProfileId);

        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

            await mongo.NutritionPlans.InsertOneAsync(new NutritionPlan
            {
                Id = ObjectId.GenerateNewId(),
                ExternalId = Guid.NewGuid(),
                ClientId = clientUserId,
                NutritionistId = Guid.NewGuid(),
                Name = "Draft-only nutrition plan",
                Status = NutritionPlanStatus.Draft,
                Weeks = [new PlanWeek { WeekNumber = 1, Status = WeekStatus.Draft }],
                Version = 1,
                DateCreated = DateTime.UtcNow,
            }, cancellationToken: TestContext.Current.CancellationToken);

            await mongo.TrainingPlans.InsertOneAsync(new TrainingPlan
            {
                Id = ObjectId.GenerateNewId(),
                ExternalId = Guid.NewGuid(),
                ClientId = clientUserId,
                TrainerId = Guid.NewGuid(),
                Name = "Draft-only training plan",
                Status = TrainingPlanStatus.Draft,
                Weeks = [new TrainingWeek { WeekNumber = 1, Status = WeekStatus.Draft, Days = [] }],
                Version = 1,
                DateCreated = DateTime.UtcNow,
            }, cancellationToken: TestContext.Current.CancellationToken);
        }

        var response = await trainerHttp.GetAsync(
            $"/trainer/clients/{clientPublicId}/timeline?limit=100",
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<TimelineResponse>(
            JsonOptions, cancellationToken: TestContext.Current.CancellationToken);

        body!.Items.Should().NotContain(
            i => i.Type == "nutrition_plan_published" || i.Type == "training_plan_published",
            "neither plan has a published week, so no plan-publish event may be emitted");
    }

    /// <summary>
    /// Test 11 (#1014, AC bullet 5): a link with <c>CanViewNutritionPlans=false</c> must hide
    /// the nutrition_plan_published event even though a published nutrition plan exists.
    /// Week-level Mongo seeding is used here deliberately (not HTTP publish) — a professional
    /// without the nutrition capability could never publish the plan through the API in the
    /// first place, so this case must seed directly.
    /// </summary>
    [Fact]
    public async Task Timeline_CanViewNutritionPlansFalse_HidesNutritionPlanPublishedEvent()
    {
        var (trainerHttp, trainerProfileId, _) = await SetupTrainerAsync();
        var (clientPublicId, clientProfileId, clientUserId) = await SetupClientAsync();
        await LinkTrainerToClientAsync(
            trainerProfileId, clientProfileId, canViewNutritionPlans: false, canViewTrainingPlans: true);

        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
            await mongo.NutritionPlans.InsertOneAsync(new NutritionPlan
            {
                Id = ObjectId.GenerateNewId(),
                ExternalId = Guid.NewGuid(),
                ClientId = clientUserId,
                NutritionistId = Guid.NewGuid(),
                Name = "Gated Nutrition Plan",
                Status = NutritionPlanStatus.Active,
                Weeks = [new PlanWeek { WeekNumber = 1, Status = WeekStatus.Published, DatePublished = DateTime.UtcNow.AddDays(-1) }],
                Version = 1,
                DateCreated = DateTime.UtcNow,
            }, cancellationToken: TestContext.Current.CancellationToken);
        }

        var response = await trainerHttp.GetAsync(
            $"/trainer/clients/{clientPublicId}/timeline?limit=100",
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<TimelineResponse>(
            JsonOptions, cancellationToken: TestContext.Current.CancellationToken);

        body!.Items.Should().NotContain(
            i => i.Type == "nutrition_plan_published",
            "CanViewNutritionPlans=false must hide the nutrition_plan_published event even though a published plan exists");
    }

    /// <summary>
    /// Test 12 (#1014, AC bullet 5): mirror-site case for
    /// <see cref="Timeline_CanViewNutritionPlansFalse_HidesNutritionPlanPublishedEvent"/>.
    /// </summary>
    [Fact]
    public async Task Timeline_CanViewTrainingPlansFalse_HidesTrainingPlanPublishedEvent()
    {
        var (trainerHttp, trainerProfileId, _) = await SetupTrainerAsync();
        var (clientPublicId, clientProfileId, clientUserId) = await SetupClientAsync();
        await LinkTrainerToClientAsync(
            trainerProfileId, clientProfileId, canViewNutritionPlans: true, canViewTrainingPlans: false);

        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
            await mongo.TrainingPlans.InsertOneAsync(new TrainingPlan
            {
                Id = ObjectId.GenerateNewId(),
                ExternalId = Guid.NewGuid(),
                ClientId = clientUserId,
                TrainerId = Guid.NewGuid(),
                Name = "Gated Training Plan",
                Status = TrainingPlanStatus.Active,
                Weeks = [new TrainingWeek { WeekNumber = 1, Status = WeekStatus.Published, DatePublished = DateTime.UtcNow.AddDays(-1), Days = [] }],
                Version = 1,
                DateCreated = DateTime.UtcNow,
            }, cancellationToken: TestContext.Current.CancellationToken);
        }

        var response = await trainerHttp.GetAsync(
            $"/trainer/clients/{clientPublicId}/timeline?limit=100",
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<TimelineResponse>(
            JsonOptions, cancellationToken: TestContext.Current.CancellationToken);

        body!.Items.Should().NotContain(
            i => i.Type == "training_plan_published",
            "CanViewTrainingPlans=false must hide the training_plan_published event even though a published plan exists");
    }

    /// <summary>
    /// Test 13 (#1014): a plan whose earliest published week is older than the endpoint's fixed
    /// 90-day lookback must be excluded from the timeline.
    /// </summary>
    [Fact]
    public async Task Timeline_PublishOlderThan90Days_ExcludesPlanPublishedEvent()
    {
        var (trainerHttp, trainerProfileId, _) = await SetupTrainerAsync();
        var (clientPublicId, clientProfileId, clientUserId) = await SetupClientAsync();
        await LinkTrainerToClientAsync(trainerProfileId, clientProfileId);

        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
            await mongo.NutritionPlans.InsertOneAsync(new NutritionPlan
            {
                Id = ObjectId.GenerateNewId(),
                ExternalId = Guid.NewGuid(),
                ClientId = clientUserId,
                NutritionistId = Guid.NewGuid(),
                Name = "Old Nutrition Plan",
                Status = NutritionPlanStatus.Active,
                Weeks = [new PlanWeek { WeekNumber = 1, Status = WeekStatus.Published, DatePublished = DateTime.UtcNow.AddDays(-91) }],
                Version = 1,
                DateCreated = DateTime.UtcNow.AddDays(-100),
            }, cancellationToken: TestContext.Current.CancellationToken);
        }

        var response = await trainerHttp.GetAsync(
            $"/trainer/clients/{clientPublicId}/timeline?limit=100",
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<TimelineResponse>(
            JsonOptions, cancellationToken: TestContext.Current.CancellationToken);

        body!.Items.Should().NotContain(
            i => i.Type == "nutrition_plan_published",
            "a plan-publish event older than the 90-day lookback must be excluded");
    }

    /// <summary>
    /// Training-domain equivalent of the multi-week case: two published weeks produce two
    /// <c>training_plan_published</c> rows with per-week ids and week numbers.
    /// </summary>
    [Fact]
    public async Task Timeline_TrainingMultipleWeeksPublished_EmitsOneEventPerWeek()
    {
        var (trainerHttp, trainerUserId, clientUserId, clientPublicId) =
            await RegisterTrainerWithLinkedClientAsync();

        var plan = BuildDraftTrainingPlan(trainerUserId, clientUserId, weekCount: 3);
        await SeedTrainingPlanAsync(plan);

        foreach (var weekNumber in new[] { 2, 1 })
        {
            var publish = await trainerHttp.PostAsync(
                $"/training/plans/{plan.ExternalId}/weeks/{weekNumber}/publish",
                JsonContent.Create(new { PlanId = plan.ExternalId, WeekNumber = weekNumber }),
                TestContext.Current.CancellationToken);
            publish.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var body = await GetTimelineAsync(trainerHttp, clientPublicId);

        var events = body.Items.Where(i => i.Type == "training_plan_published").ToList();
        events.Should().HaveCount(2);
        events.Select(i => i.Id).Should().BeEquivalentTo(
            [$"training_plan:{plan.ExternalId}:week:1", $"training_plan:{plan.ExternalId}:week:2"]);
        events.Select(i => i.PlanPublished!.WeekNumber).Should().BeEquivalentTo([1, 2]);
        events.Select(i => i.OccurredAt).Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// A meal day carries distinct-meal MealsLogged, and MealsPlanned from the caller's own plan.
    /// </summary>
    [Fact]
    public async Task Timeline_MealDay_OwnPlan_CarriesLoggedAndPlannedCounts()
    {
        var (trainerHttp, trainerProfileId, trainerUserId) = await SetupTrainerAsync();
        var (clientPublicId, clientProfileId, clientUserId) = await SetupClientAsync();
        await LinkTrainerToClientAsync(trainerProfileId, clientProfileId);

        var day = DateTime.UtcNow.Date.AddDays(-3);
        var plan = await SeedPlanWithThreeMealsOnDayOneAsync(trainerUserId, clientUserId, day);
        await SeedMealLogsAsync(clientUserId, plan, day);

        var body = await GetTimelineAsync(trainerHttp, clientPublicId);

        var item = body.Items.Single(i => i.Id == $"meals:{day:yyyy-MM-dd}");
        item.MealDay.Should().NotBeNull();
        item.MealDay!.MealsLogged.Should().Be(2, "a meal logged twice counts once");
        item.MealDay.MealsPlanned.Should().Be(3);
        item.Title.Should().Be("Zaznamenáno 3 jídel", "the existing title keeps the raw log count");
    }

    /// <summary>
    /// A plan written by another coach never leaks its planned meal count.
    /// </summary>
    [Fact]
    public async Task Timeline_MealDay_ForeignPlan_MealsPlannedIsNull()
    {
        var (trainerHttp, trainerProfileId, _) = await SetupTrainerAsync();
        var (clientPublicId, clientProfileId, clientUserId) = await SetupClientAsync();
        await LinkTrainerToClientAsync(trainerProfileId, clientProfileId);

        var day = DateTime.UtcNow.Date.AddDays(-3);
        var plan = await SeedPlanWithThreeMealsOnDayOneAsync(Guid.NewGuid(), clientUserId, day);
        await SeedMealLogsAsync(clientUserId, plan, day);

        var body = await GetTimelineAsync(trainerHttp, clientPublicId);

        var item = body.Items.Single(i => i.Id == $"meals:{day:yyyy-MM-dd}");
        item.MealDay!.MealsLogged.Should().Be(2);
        item.MealDay.MealsPlanned.Should().BeNull();
    }

    /// <summary>
    /// A workout carries its execution id, exercise count and the session name from the
    /// caller's own training plan.
    /// </summary>
    [Fact]
    public async Task Timeline_Workout_OwnPlan_CarriesSessionNameAndExerciseCount()
    {
        var (trainerHttp, trainerProfileId, trainerUserId) = await SetupTrainerAsync();
        var (clientPublicId, clientProfileId, clientUserId) = await SetupClientAsync();
        await LinkTrainerToClientAsync(trainerProfileId, clientProfileId);

        var (plan, sessionId) = await SeedTrainingPlanWithSessionAsync(trainerUserId, clientUserId, "Push day");
        var executionId = await SeedWorkoutExecutionAsync(clientUserId, plan, sessionId);

        var body = await GetTimelineAsync(trainerHttp, clientPublicId);

        var item = body.Items.Single(i => i.Id == $"workout:{executionId}");
        item.Workout.Should().NotBeNull();
        item.Workout!.SessionExecutionId.Should().Be(executionId);
        item.Workout.SessionName.Should().Be("Push day");
        item.Workout.ExerciseCount.Should().Be(2);
    }

    /// <summary>
    /// A session in another coach's plan never leaks its name.
    /// </summary>
    [Fact]
    public async Task Timeline_Workout_ForeignPlan_SessionNameIsNull()
    {
        var (trainerHttp, trainerProfileId, _) = await SetupTrainerAsync();
        var (clientPublicId, clientProfileId, clientUserId) = await SetupClientAsync();
        await LinkTrainerToClientAsync(trainerProfileId, clientProfileId);

        var (plan, sessionId) = await SeedTrainingPlanWithSessionAsync(Guid.NewGuid(), clientUserId, "Secret day");
        var executionId = await SeedWorkoutExecutionAsync(clientUserId, plan, sessionId);

        var body = await GetTimelineAsync(trainerHttp, clientPublicId);

        var item = body.Items.Single(i => i.Id == $"workout:{executionId}");
        item.Workout!.SessionName.Should().BeNull();
        item.Workout.ExerciseCount.Should().Be(2);
    }

    /// <summary>
    /// A measurement carries weight and body fat.
    /// </summary>
    [Fact]
    public async Task Timeline_Measurement_CarriesWeightAndBodyFat()
    {
        var (trainerHttp, trainerProfileId, _) = await SetupTrainerAsync();
        var (clientPublicId, clientProfileId, _) = await SetupClientAsync();
        await LinkTrainerToClientAsync(trainerProfileId, clientProfileId);

        var measurementId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.BodyMeasurements.Add(new BodyMeasurement
            {
                PublicId = measurementId,
                ClientProfileId = clientProfileId,
                MeasuredAt = DateTime.UtcNow.AddDays(-2),
                WeightKg = 81.5m,
                BodyFatPercentage = 18.2m,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var body = await GetTimelineAsync(trainerHttp, clientPublicId);

        var item = body.Items.Single(i => i.Id == $"measurement:{measurementId}");
        item.Measurement.Should().NotBeNull();
        item.Measurement!.WeightKg.Should().Be(81.5m);
        item.Measurement.BodyFatPercentage.Should().Be(18.2m);
    }

    /// <summary>
    /// A submitted questionnaire carries its title.
    /// </summary>
    [Fact]
    public async Task Timeline_Questionnaire_CarriesTitle()
    {
        var (trainerHttp, trainerProfileId, trainerUserId) = await SetupTrainerAsync();
        var (clientPublicId, clientProfileId, clientUserId) = await SetupClientAsync();
        await LinkTrainerToClientAsync(trainerProfileId, clientProfileId);

        var responseId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var linkId = await db.ClientProfessionalLinks
                .Where(l => l.ClientProfileId == clientProfileId)
                .Select(l => l.Id)
                .FirstAsync(TestContext.Current.CancellationToken);

            var questionnaire = new Questionnaire
            {
                PublicId = Guid.NewGuid(),
                ProfessionalId = trainerUserId,
                Title = "Intake questionnaire",
                IsActive = true,
                DateCreated = DateTime.UtcNow,
            };
            db.Questionnaires.Add(questionnaire);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

            db.QuestionnaireResponses.Add(new QuestionnaireResponse
            {
                PublicId = responseId,
                QuestionnaireId = questionnaire.Id,
                ClientId = clientUserId,
                ProfessionalId = trainerUserId,
                LinkId = linkId,
                Status = QuestionnaireResponseStatus.Submitted,
                SubmittedAt = DateTime.UtcNow.AddDays(-2),
                DateCreated = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var body = await GetTimelineAsync(trainerHttp, clientPublicId);

        var item = body.Items.Single(i => i.Id == $"questionnaire:{responseId}");
        item.Questionnaire.Should().NotBeNull();
        item.Questionnaire!.QuestionnaireTitle.Should().Be("Intake questionnaire");
    }

    /// <summary>
    /// Two coaches linked to the same client each see only the questionnaire they authored.
    /// </summary>
    [Fact]
    public async Task Timeline_Questionnaire_ShowsOnlyCallingCoachsOwnResponses()
    {
        var ct = TestContext.Current.CancellationToken;
        var trainer = await TestActors.Trainer(factory).CreateAsync(ct);
        var nutritionist = await TestActors.Nutritionist(factory).CreateAsync(ct);
        var client = await TestActors.Client(factory).CreateAsync(ct);
        var trainerLinkId = await TestActors.Link(factory, trainer, client).CreateAsync(ct);
        var nutritionistLinkId = await TestActors.Link(factory, nutritionist, client)
            .AsRole(UserRole.Nutritionist)
            .CreateAsync(ct);

        var trainerResponseId = Guid.NewGuid();
        var nutritionistResponseId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var trainerQuestionnaire = new Questionnaire
            {
                PublicId = Guid.NewGuid(),
                ProfessionalId = trainer.UserId,
                Title = "Trainer intake",
                IsActive = true,
                DateCreated = DateTime.UtcNow,
            };
            var nutritionistQuestionnaire = new Questionnaire
            {
                PublicId = Guid.NewGuid(),
                ProfessionalId = nutritionist.UserId,
                Title = "Nutrition intake",
                IsActive = true,
                DateCreated = DateTime.UtcNow,
            };
            db.Questionnaires.AddRange(trainerQuestionnaire, nutritionistQuestionnaire);
            await db.SaveChangesAsync(ct);

            db.QuestionnaireResponses.AddRange(
                new QuestionnaireResponse
                {
                    PublicId = trainerResponseId,
                    QuestionnaireId = trainerQuestionnaire.Id,
                    ClientId = client.UserId,
                    ProfessionalId = trainer.UserId,
                    LinkId = trainerLinkId,
                    Status = QuestionnaireResponseStatus.Submitted,
                    SubmittedAt = DateTime.UtcNow.AddDays(-2),
                    DateCreated = DateTime.UtcNow,
                },
                new QuestionnaireResponse
                {
                    PublicId = nutritionistResponseId,
                    QuestionnaireId = nutritionistQuestionnaire.Id,
                    ClientId = client.UserId,
                    ProfessionalId = nutritionist.UserId,
                    LinkId = nutritionistLinkId,
                    Status = QuestionnaireResponseStatus.Submitted,
                    SubmittedAt = DateTime.UtcNow.AddDays(-1),
                    DateCreated = DateTime.UtcNow,
                });
            await db.SaveChangesAsync(ct);
        }

        var trainerTimeline = await GetTimelineAsync(trainer.Http, client.PublicId);
        var nutritionistTimeline = await GetTimelineAsync(nutritionist.Http, client.PublicId);

        trainerTimeline.Items.Where(i => i.Type == "questionnaire").Select(i => i.Id)
            .Should().BeEquivalentTo([$"questionnaire:{trainerResponseId}"]);
        nutritionistTimeline.Items.Where(i => i.Type == "questionnaire").Select(i => i.Id)
            .Should().BeEquivalentTo([$"questionnaire:{nutritionistResponseId}"]);
    }

    private async Task<TimelineResponse> GetTimelineAsync(HttpClient http, Guid clientPublicId)
    {
        var response = await http.GetAsync(
            $"/trainer/clients/{clientPublicId}/timeline?limit=100",
            TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<TimelineResponse>(
            JsonOptions, cancellationToken: TestContext.Current.CancellationToken);
        return body!;
    }

    /// <summary>
    /// Seeds a published plan starting on <paramref name="startDate"/> whose first day lists three meals.
    /// </summary>
    private async Task<NutritionPlan> SeedPlanWithThreeMealsOnDayOneAsync(
        Guid nutritionistId, Guid clientUserId, DateTime startDate)
    {
        var plan = new NutritionPlan
        {
            Id = ObjectId.GenerateNewId(),
            ExternalId = Guid.NewGuid(),
            ClientId = clientUserId,
            NutritionistId = nutritionistId,
            Name = "Meal count plan",
            Status = NutritionPlanStatus.Active,
            StartDate = startDate,
            Version = 1,
            DateCreated = DateTime.UtcNow,
            Weeks =
            [
                new PlanWeek
                {
                    WeekNumber = 1,
                    Status = WeekStatus.Published,
                    // Plan day 1 is the start date; DayOfWeek in a plan is the offset (1 = first day).
                    Days = [new PlanDay { DayOfWeek = 1, Meals = Enumerable.Range(1, 3)
                        .Select(order => new PlanMeal { MealId = Guid.NewGuid(), Order = order }).ToList() }],
                },
            ],
        };
        await SeedNutritionPlanAsync(plan);
        return plan;
    }

    /// <summary>
    /// Logs two distinct plan meals on <paramref name="day"/>, one of them twice.
    /// </summary>
    private async Task SeedMealLogsAsync(Guid clientUserId, NutritionPlan plan, DateTime day)
    {
        var meals = plan.Weeks[0].Days[0].Meals;
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        foreach (var (mealId, hour) in new[] { (meals[0].MealId, 8), (meals[1].MealId, 12), (meals[1].MealId, 13) })
        {
            await mongo.MealLogs.InsertOneAsync(new MealLog
            {
                Id = ObjectId.GenerateNewId(),
                ClientId = clientUserId,
                PlanId = plan.ExternalId,
                MealId = mealId,
                LogDate = day,
                EatenAt = day.AddHours(hour),
                FoodsEaten = [],
            }, cancellationToken: TestContext.Current.CancellationToken);
        }
    }

    private async Task<(TrainingPlan Plan, Guid SessionId)> SeedTrainingPlanWithSessionAsync(
        Guid trainerId, Guid clientUserId, string sessionName)
    {
        var sessionId = Guid.NewGuid();
        var plan = BuildDraftTrainingPlan(trainerId, clientUserId, weekCount: 1);
        plan.Weeks[0].Days =
        [
            new TrainingDay
            {
                DayOfWeek = 1,
                Sessions = [new TrainingSession { SessionId = sessionId, Name = sessionName }],
            },
        ];
        await SeedTrainingPlanAsync(plan);
        return (plan, sessionId);
    }

    private async Task<Guid> SeedWorkoutExecutionAsync(Guid clientUserId, TrainingPlan plan, Guid sessionId)
    {
        var executionId = Guid.NewGuid();
        var completedAt = DateTime.UtcNow.AddDays(-1);
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        await mongo.SessionExecutions.InsertOneAsync(new SessionExecution
        {
            Id = ObjectId.GenerateNewId(),
            ExternalId = executionId,
            ClientId = clientUserId,
            PlanId = plan.ExternalId,
            SessionId = sessionId,
            Date = SessionExecution.ToCompletionDateUtc(completedAt),
            Status = SessionExecutionStatus.Completed,
            Performance = new SessionExecutionPerformance
            {
                StartedAt = completedAt.AddMinutes(-45),
                CompletedAt = completedAt,
                Workouts =
                [
                    new LoggedWorkout
                    {
                        WorkoutId = Guid.NewGuid(),
                        Name = "Block",
                        Exercises =
                        [
                            new WorkoutExercise { ExerciseExternalId = Guid.NewGuid(), ExerciseName = "Squat" },
                            new WorkoutExercise { ExerciseExternalId = Guid.NewGuid(), ExerciseName = "Press" },
                        ],
                    },
                ],
            },
            DateCreated = DateTime.UtcNow,
        }, cancellationToken: TestContext.Current.CancellationToken);

        return executionId;
    }

    // ── local response DTOs ────────────────────────────────────────────────────

    private record TimelineResponse(List<TimelineItem> Items);

    private record TimelineItem(
        string Id,
        string Type,
        DateTime OccurredAt,
        string Title,
        string? Description,
        string? Icon,
        PrPayload? PersonalRecord,
        MealDayPayload? MealDay = null,
        WorkoutPayload? Workout = null,
        MeasurementPayload? Measurement = null,
        QuestionnairePayload? Questionnaire = null,
        PlanPublishedPayload? PlanPublished = null);

    private record MealDayPayload(int MealsLogged, int? MealsPlanned);

    private record WorkoutPayload(Guid SessionExecutionId, string? SessionName, int ExerciseCount);

    private record MeasurementPayload(decimal? WeightKg, decimal? BodyFatPercentage);

    private record QuestionnairePayload(string QuestionnaireTitle);

    private record PlanPublishedPayload(Guid PlanId, string? PlanName, int WeekNumber);

    private record PrPayload(
        Guid ExternalId,
        Guid ExerciseExternalId,
        string ExerciseName,
        decimal WeightKg,
        int Reps,
        Guid WorkoutLogId);
}
