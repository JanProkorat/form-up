using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Infrastructure;
using FormUp.Tests.Endpoints.TrainingPlans;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Client.Training;

/// <summary>
/// Integration tests for <c>GET /client/training/plans/{planId}</c>.
/// Uses Testcontainers (real PostgreSQL + MongoDB) so the ownership filter,
/// exercise muscle-group enrichment, and workout-log completion state are all
/// verified against a real stack.
/// </summary>
[Collection(TestCollection.Shard4)]
public class GetFullTrainingPlanIntegrationTests(FitnessApiFactory factory)
{
    private static string UniqueEmail() => $"{Guid.NewGuid():N}@integration-test.com";

    /// <summary>
    /// Happy-path: client owns the plan.
    /// Verifies plan metadata, week dates, session counts, muscle-group enrichment,
    /// and per-set completion state derived from a partial workout log.
    /// </summary>
    [Fact]
    public async Task GetFullPlan_WithValidPlanAndPartialLog_Returns200_WithCorrectCompletionState()
    {
        var httpClient = factory.CreateClient();

        // ── 1. Register + log in the client ──────────────────────────────────────
        var clientEmail = UniqueEmail();
        await TestHelpers.RegisterAsync(httpClient, clientEmail, "TestPass1!", "Test", "Client", "Client");
        var (accessToken, _) = await TestHelpers.LoginAsync(httpClient, clientEmail, "TestPass1!");

        // ── 2. Resolve client's ApplicationUser.Id from Postgres ───────────────────
        // Post-#840/#845, TrainingPlan.ClientId and SessionExecution.ClientId are both
        // keyed on ApplicationUser.Id (NOT ClientProfile.PublicId) — GetFullTrainingPlanEndpoint
        // resolves clientProfile.UserId and filters both collections on that single value.
        Guid clientUserId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await db.Users.FirstAsync(
                u => u.Email == clientEmail,
                TestContext.Current.CancellationToken);
            var profile = await db.ClientProfiles.FirstAsync(
                cp => cp.UserId == user.Id,
                TestContext.Current.CancellationToken);
            clientUserId = profile.UserId;
        }

        // ── 3. Seed Exercise docs ─────────────────────────────────────────────────
        var squatId = Guid.NewGuid();
        var benchId = Guid.NewGuid();
        // Distinct per-instance ids (#857 phase 3b) — deliberately different from the catalog
        // ExternalId above so the response's ExerciseId/ExerciseExternalId assertions below
        // cannot pass by coincidence.
        var squatInstanceId = Guid.NewGuid();
        var benchInstanceId = Guid.NewGuid();

        var squatExercise = new Exercise
        {
            ExternalId = squatId,
            Name = "Squat",
            MuscleGroups = [MuscleGroup.Quadriceps, MuscleGroup.Glutes],
            Equipment = ExerciseEquipment.Barbell,
            Category = ExerciseCategory.Strength,
            Difficulty = ExerciseDifficulty.Intermediate,
            IsActive = true,
            Source = "system",
            DateCreated = DateTime.UtcNow
        };

        var benchExercise = new Exercise
        {
            ExternalId = benchId,
            Name = "Bench Press",
            MuscleGroups = [MuscleGroup.Chest, MuscleGroup.Triceps],
            Equipment = ExerciseEquipment.Barbell,
            Category = ExerciseCategory.Strength,
            Difficulty = ExerciseDifficulty.Intermediate,
            IsActive = true,
            Source = "system",
            DateCreated = DateTime.UtcNow
        };

        // ── 4. Seed TrainingPlan ──────────────────────────────────────────────────
        var planId = Guid.NewGuid();
        var sessionAId = Guid.NewGuid();
        var sessionBId = Guid.NewGuid();
        // Shared with the seeded SessionExecution.Performance.Workouts below (#885) — the
        // live-log write path (UpdateWorkoutEndpoint) always stamps a LoggedWorkout with the
        // REAL TrainingWorkout.WorkoutId it was logged against, so a realistic fixture must use
        // the same value on both sides rather than two independent Guid.NewGuid() calls.
        var sessionAWorkoutId = Guid.NewGuid();

        var plan = new TrainingPlan
        {
            ExternalId = planId,
            ClientId = clientUserId,
            TrainerId = Guid.NewGuid(),
            Name = "Test Hypertrophy Plan",
            Status = TrainingPlanStatus.Active,
            Version = 1,
            DateCreated = DateTime.UtcNow.AddDays(-10),
            StartDate = DateTime.UtcNow.AddDays(-7), // Week 1 started 7 days ago
            Weeks =
            [
                new TrainingWeek
                {
                    WeekNumber = 1,
                    Status = WeekStatus.Published,
                    DatePublished = DateTime.UtcNow.AddDays(-8),
                    Days = TrainingPlanTestHelpers.MaterializeDays(
                        // Session A: Squat, 3 sets — Monday
                        (1, new TrainingSession
                        {
                            SessionId = sessionAId,
                            Name = "Leg Day",
                            Order = 1,
                            Notes = "Focus on depth",
                            Workouts =
                            [
                                new TrainingWorkout
                                {
                                    WorkoutId = sessionAWorkoutId,
                                    Order = 0,
                                    Name = "Hlavní",
                                    Exercises =
                                    [
                                        new SessionExercise
                                        {
                                            ExerciseId = squatInstanceId,
                                            ExerciseExternalId = squatId,
                                            ExerciseName = "Squat",
                                            Order = 1,
                                            RestSeconds = 120,
                                            Sets =
                                            [
                                                new ExerciseSet { SetNumber = 1, Type = SetType.Normal, Reps = 8, WeightKg = 100 },
                                                new ExerciseSet { SetNumber = 2, Type = SetType.Normal, Reps = 8, WeightKg = 100 },
                                                new ExerciseSet { SetNumber = 3, Type = SetType.Normal, Reps = 8, WeightKg = 100 }
                                            ]
                                        }
                                    ]
                                }
                            ]
                        }),
                        // Session B: Bench Press, 3 sets — also Monday (order 2)
                        (1, new TrainingSession
                        {
                            SessionId = sessionBId,
                            Name = "Push Day",
                            Order = 2,
                            Workouts =
                            [
                                new TrainingWorkout
                                {
                                    WorkoutId = Guid.NewGuid(),
                                    Order = 0,
                                    Name = "Hlavní",
                                    Exercises =
                                    [
                                        new SessionExercise
                                        {
                                            ExerciseId = benchInstanceId,
                                            ExerciseExternalId = benchId,
                                            ExerciseName = "Bench Press",
                                            Order = 1,
                                            RestSeconds = 90,
                                            Sets =
                                            [
                                                new ExerciseSet { SetNumber = 1, Type = SetType.Normal, Reps = 10, WeightKg = 80 },
                                                new ExerciseSet { SetNumber = 2, Type = SetType.Normal, Reps = 10, WeightKg = 80 },
                                                new ExerciseSet { SetNumber = 3, Type = SetType.Normal, Reps = 10, WeightKg = 80 }
                                            ]
                                        }
                                    ]
                                }
                            ]
                        }))
                }
            ]
        };

        // ── 5. Seed partial SessionExecution for Session A (2 of 3 sets completed) ──
        // SessionExecution.ClientId stores the ApplicationUser.Id (not ClientProfile.PublicId).
        // The endpoint filters by ApplicationUser.Id derived from the JWT claim. Post-#841 the
        // standalone WorkoutLog document was unified into SessionExecution.Performance.
        var startedAt = DateTime.UtcNow.AddDays(-6);
        var execution = new SessionExecution
        {
            ExternalId = Guid.NewGuid(),
            ClientId = clientUserId,
            PlanId = planId,
            SessionId = sessionAId,
            Date = SessionExecution.ToCompletionDateUtc(startedAt),
            Status = SessionExecutionStatus.Partial,
            DateCreated = startedAt,
            Performance = new SessionExecutionPerformance
            {
                StartedAt = startedAt,
                CompletedAt = null,
                Workouts =
                [
                    new LoggedWorkout
                    {
                        WorkoutId = sessionAWorkoutId,
                        Order = 0,
                        Name = "Hlavní",
                        Exercises =
                        [
                            new WorkoutExercise
                            {
                                ExerciseExternalId = squatId,
                                ExerciseName = "Squat",
                                Sets =
                                [
                                    new WorkoutSet { SetNumber = 1, Reps = 8, WeightKg = 100, CompletedAt = startedAt.AddMinutes(5) },
                                    new WorkoutSet { SetNumber = 2, Reps = 8, WeightKg = 100, CompletedAt = startedAt.AddMinutes(8) },
                                    // Set 3 not completed
                                    new WorkoutSet { SetNumber = 3, Reps = 0, WeightKg = 100, CompletedAt = null }
                                ]
                            }
                        ]
                    }
                ]
            }
        };

        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
            await mongo.Exercises.InsertOneAsync(squatExercise, cancellationToken: TestContext.Current.CancellationToken);
            await mongo.Exercises.InsertOneAsync(benchExercise, cancellationToken: TestContext.Current.CancellationToken);
            await mongo.TrainingPlans.InsertOneAsync(plan, cancellationToken: TestContext.Current.CancellationToken);
            await mongo.SessionExecutions.InsertOneAsync(execution, cancellationToken: TestContext.Current.CancellationToken);
        }

        // ── 6. GET /client/training/plans/{planId} ────────────────────────────────
        TestHelpers.SetBearerToken(httpClient, accessToken);
        var response = await httpClient.GetAsync(
            $"/client/training/plans/{planId}",
            TestContext.Current.CancellationToken);

        // ── 7. Assert top-level ───────────────────────────────────────────────────
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // The API serializes enums as strings (JsonStringEnumConverter globally),
        // so use matching options when deserializing the test response.
        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        var body = await response.Content.ReadFromJsonAsync<FullPlanResponse>(
            jsonOptions,
            cancellationToken: TestContext.Current.CancellationToken);

        body.Should().NotBeNull();
        body!.PlanId.Should().Be(planId);
        body.PlanName.Should().Be("Test Hypertrophy Plan");
        body.Status.Should().Be("Active");
        body.TotalWeeks.Should().Be(1);
        body.PublishedWeekCount.Should().Be(1);
        body.Weeks.Should().HaveCount(1);

        // ── 8. Assert week ────────────────────────────────────────────────────────
        var week = body.Weeks[0];
        week.WeekNumber.Should().Be(1);
        week.Status.Should().Be("Published");
        week.Sessions.Should().HaveCount(2);

        // Week start/end should be derived from StartDate
        week.WeekStartDate.Should().NotBeNull();
        week.WeekEndDate.Should().NotBeNull();

        // ── 9. Assert Session A (Leg Day — partial completion) ────────────────────
        var sessionA = week.Sessions.First(s => s.SessionId == sessionAId);
        sessionA.TotalExerciseCount.Should().Be(1);
        sessionA.CompletedExerciseCount.Should().Be(0,
            "only 2 of 3 sets are logged, so the exercise is not fully complete");

        sessionA.AllExercises.Should().HaveCount(1);
        var squatDto = sessionA.AllExercises[0];
        squatDto.ExerciseId.Should().Be(squatInstanceId,
            "the response must expose the per-instance id the mark-complete/incomplete routes require (#857 phase 3b)");
        squatDto.ExerciseExternalId.Should().Be(squatId);
        squatDto.IsCompleted.Should().BeFalse("only 2 of 3 sets are done");
        squatDto.MuscleGroups.Should().Contain(MuscleGroup.Quadriceps);
        squatDto.MuscleGroups.Should().Contain(MuscleGroup.Glutes);
        squatDto.Sets.Should().HaveCount(3);

        var completedSets = squatDto.Sets.Where(s => s.CompletedAt is not null).ToList();
        completedSets.Should().HaveCount(2, "sets 1 and 2 were logged as completed");

        var pendingSet = squatDto.Sets.First(s => s.SetNumber == 3);
        pendingSet.CompletedAt.Should().BeNull("set 3 was not completed");

        // ── 10. Assert Session B (Push Day — no log, no completion) ──────────────
        var sessionB = week.Sessions.First(s => s.SessionId == sessionBId);
        sessionB.TotalExerciseCount.Should().Be(1);
        sessionB.CompletedExerciseCount.Should().Be(0);

        sessionB.AllExercises.Should().HaveCount(1);
        var benchDto = sessionB.AllExercises[0];
        benchDto.ExerciseId.Should().Be(benchInstanceId,
            "the response must expose the per-instance id the mark-complete/incomplete routes require (#857 phase 3b)");
        benchDto.ExerciseExternalId.Should().Be(benchId);
        benchDto.IsCompleted.Should().BeFalse();
        benchDto.MuscleGroups.Should().Contain(MuscleGroup.Chest);
        benchDto.MuscleGroups.Should().Contain(MuscleGroup.Triceps);
        benchDto.Sets.Should().HaveCount(3);
        benchDto.Sets.Should().AllSatisfy(s => s.CompletedAt.Should().BeNull());
    }

    /// <summary>
    /// Ownership guard: a second client calling with the first client's plan ID receives 404,
    /// not 403 — existence is not leaked.
    /// </summary>
    [Fact]
    public async Task GetFullPlan_ByNonOwner_Returns404()
    {
        var httpClient = factory.CreateClient();

        // ── Register + log in first client (plan owner) ───────────────────────────
        var ownerEmail = UniqueEmail();
        await TestHelpers.RegisterAsync(httpClient, ownerEmail, "TestPass1!", "Owner", "Client", "Client");

        Guid ownerUserId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await db.Users.FirstAsync(
                u => u.Email == ownerEmail,
                TestContext.Current.CancellationToken);
            var profile = await db.ClientProfiles.FirstAsync(
                cp => cp.UserId == user.Id,
                TestContext.Current.CancellationToken);
            ownerUserId = profile.UserId;
        }

        // Seed a plan for the owner
        var ownerPlanId = Guid.NewGuid();
        var ownerPlan = new TrainingPlan
        {
            ExternalId = ownerPlanId,
            ClientId = ownerUserId,
            TrainerId = Guid.NewGuid(),
            Name = "Owner's Secret Plan",
            Status = TrainingPlanStatus.Active,
            Version = 1,
            DateCreated = DateTime.UtcNow,
            Weeks =
            [
                new TrainingWeek
                {
                    WeekNumber = 1,
                    Status = WeekStatus.Published,
                    DatePublished = DateTime.UtcNow.AddDays(-1),
                    Days = TrainingPlanTestHelpers.MaterializeDays()
                }
            ]
        };

        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
            await mongo.TrainingPlans.InsertOneAsync(ownerPlan, cancellationToken: TestContext.Current.CancellationToken);
        }

        // ── Register + log in second client (the attacker) ───────────────────────
        var attackerEmail = UniqueEmail();
        await TestHelpers.RegisterAsync(httpClient, attackerEmail, "TestPass1!", "Attacker", "Client", "Client");
        var (attackerToken, _) = await TestHelpers.LoginAsync(httpClient, attackerEmail, "TestPass1!");

        // ── Call with attacker's token against the owner's plan ──────────────────
        TestHelpers.SetBearerToken(httpClient, attackerToken);
        var response = await httpClient.GetAsync(
            $"/client/training/plans/{ownerPlanId}",
            TestContext.Current.CancellationToken);

        // ── Expect 404 — not 403 — to avoid leaking existence ────────────────────
        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "non-owner must receive 404 so plan existence is not revealed");
    }

    /// <summary>
    /// Section round-trip: a plan with explicit sections must return those sections in the response,
    /// preserving order, name, format, and per-section exercises.
    /// The backward-compat flat Exercises list must equal the concatenation across sections in order.
    /// </summary>
    [Fact]
    public async Task GetFullPlan_WithExplicitSections_ReturnsSectionsAndFlatExercises()
    {
        var httpClient = factory.CreateClient();

        var clientEmail = UniqueEmail();
        await TestHelpers.RegisterAsync(httpClient, clientEmail, "TestPass1!", "Section", "Client", "Client");
        var (accessToken, _) = await TestHelpers.LoginAsync(httpClient, clientEmail, "TestPass1!");

        Guid clientUserId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await db.Users.FirstAsync(
                u => u.Email == clientEmail,
                TestContext.Current.CancellationToken);
            var profile = await db.ClientProfiles.FirstAsync(
                cp => cp.UserId == user.Id,
                TestContext.Current.CancellationToken);
            clientUserId = profile.UserId;
        }

        var squatId = Guid.NewGuid();
        var benchId = Guid.NewGuid();

        var squatExercise = new Exercise
        {
            ExternalId = squatId,
            Name = "Squat",
            MuscleGroups = [MuscleGroup.Quadriceps],
            Equipment = ExerciseEquipment.Barbell,
            Category = ExerciseCategory.Strength,
            Difficulty = ExerciseDifficulty.Intermediate,
            IsActive = true,
            Source = "system",
            DateCreated = DateTime.UtcNow
        };

        var benchExercise = new Exercise
        {
            ExternalId = benchId,
            Name = "Bench Press",
            MuscleGroups = [MuscleGroup.Chest],
            Equipment = ExerciseEquipment.Barbell,
            Category = ExerciseCategory.Strength,
            Difficulty = ExerciseDifficulty.Intermediate,
            IsActive = true,
            Source = "system",
            DateCreated = DateTime.UtcNow
        };

        var planId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var warmUpSectionId = Guid.NewGuid();
        var mainSectionId = Guid.NewGuid();

        var plan = new TrainingPlan
        {
            ExternalId = planId,
            ClientId = clientUserId,
            TrainerId = Guid.NewGuid(),
            Name = "Sections Round-trip Plan",
            Status = TrainingPlanStatus.Active,
            Version = 1,
            DateCreated = DateTime.UtcNow.AddDays(-5),
            Weeks =
            [
                new TrainingWeek
                {
                    WeekNumber = 1,
                    Status = WeekStatus.Published,
                    DatePublished = DateTime.UtcNow.AddDays(-4),
                    Days = TrainingPlanTestHelpers.MaterializeDays(
(2, new TrainingSession
                        {
                            SessionId = sessionId,
                            Name = "Full Body",
                            Order = 1,
                            Workouts =
                            [
                                new TrainingWorkout
                                {
                                    WorkoutId = warmUpSectionId,
                                    Order = 0,
                                    Name = "Warm-up",
                                    Format = null,
                                    Exercises =
                                    [
                                        new SessionExercise
                                        {
                                            ExerciseExternalId = squatId,
                                            ExerciseName = "Squat",
                                            Order = 1,
                                            Sets = [new ExerciseSet { SetNumber = 1, Type = SetType.Warmup, Reps = 10 }]
                                        }
                                    ]
                                },
                                new TrainingWorkout
                                {
                                    WorkoutId = mainSectionId,
                                    Order = 1,
                                    Name = "Hlavní",
                                    Format = Application.Domain.Enums.WorkoutFormat.AMRAP,
                                    Exercises =
                                    [
                                        new SessionExercise
                                        {
                                            ExerciseExternalId = benchId,
                                            ExerciseName = "Bench Press",
                                            Order = 1,
                                            Sets = [new ExerciseSet { SetNumber = 1, Type = SetType.Normal, Reps = 8, WeightKg = 80 }]
                                        }
                                    ]
                                }
                            ]
                        }))
                }
            ]
        };

        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
            await mongo.Exercises.InsertOneAsync(squatExercise, cancellationToken: TestContext.Current.CancellationToken);
            await mongo.Exercises.InsertOneAsync(benchExercise, cancellationToken: TestContext.Current.CancellationToken);
            await mongo.TrainingPlans.InsertOneAsync(plan, cancellationToken: TestContext.Current.CancellationToken);
        }

        TestHelpers.SetBearerToken(httpClient, accessToken);
        var response = await httpClient.GetAsync(
            $"/client/training/plans/{planId}",
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        var body = await response.Content.ReadFromJsonAsync<FullPlanResponse>(
            jsonOptions,
            cancellationToken: TestContext.Current.CancellationToken);

        body.Should().NotBeNull();

        var session = body!.Weeks[0].Sessions[0];

        // ── Workouts round-trip ───────────────────────────────────────────────────
        session.Workouts.Should().HaveCount(2, "two workouts were persisted");

        var warmUp = session.Workouts.First(w => w.WorkoutId == warmUpSectionId);
        warmUp.Order.Should().Be(0);
        warmUp.Name.Should().Be("Warm-up");
        warmUp.Format.Should().BeNull("Warm-up has no format");
        warmUp.Exercises.Should().HaveCount(1);
        warmUp.Exercises[0].ExerciseExternalId.Should().Be(squatId);

        var main = session.Workouts.First(w => w.WorkoutId == mainSectionId);
        main.Order.Should().Be(1);
        main.Name.Should().Be("Hlavní");
        main.Format.Should().Be("AMRAP");
        main.Exercises.Should().HaveCount(1);
        main.Exercises[0].ExerciseExternalId.Should().Be(benchId);

        // ── Read-only flat union equals workouts concatenated in order ─────────────
        session.AllExercises.Should().HaveCount(2, "total exercises across both workouts");
        session.AllExercises[0].ExerciseExternalId.Should().Be(squatId, "Warm-up exercise comes first (Order=0)");
        session.AllExercises[1].ExerciseExternalId.Should().Be(benchId, "Hlavní exercise comes second (Order=1)");
    }

    /// <summary>
    /// Standalone-only session (#857 phase 3a — the headline feature of this refactor): a
    /// session with zero workouts but one exercise programmed directly on the session must not
    /// be invisible. Mirrors the shape of the QA fixture at
    /// <c>QaSeedRunner.QaStandaloneOnlySessionId</c>.
    /// </summary>
    [Fact]
    public async Task GetFullPlan_WithStandaloneOnlySession_ReturnsNonZeroCountsAndExercise()
    {
        var httpClient = factory.CreateClient();

        var clientEmail = UniqueEmail();
        await TestHelpers.RegisterAsync(httpClient, clientEmail, "TestPass1!", "Standalone", "Only", "Client");
        var (accessToken, _) = await TestHelpers.LoginAsync(httpClient, clientEmail, "TestPass1!");

        Guid clientUserId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await db.Users.FirstAsync(
                u => u.Email == clientEmail,
                TestContext.Current.CancellationToken);
            var profile = await db.ClientProfiles.FirstAsync(
                cp => cp.UserId == user.Id,
                TestContext.Current.CancellationToken);
            clientUserId = profile.UserId;
        }

        var plankId = Guid.NewGuid();
        var plankInstanceId = Guid.NewGuid();

        var plankExercise = new Exercise
        {
            ExternalId = plankId,
            Name = "Plank",
            MuscleGroups = [MuscleGroup.Abs],
            Equipment = ExerciseEquipment.Bodyweight,
            Category = ExerciseCategory.Strength,
            Difficulty = ExerciseDifficulty.Beginner,
            IsActive = true,
            Source = "system",
            DateCreated = DateTime.UtcNow
        };

        var planId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        var plan = new TrainingPlan
        {
            ExternalId = planId,
            ClientId = clientUserId,
            TrainerId = Guid.NewGuid(),
            Name = "Standalone-Only Plan",
            Status = TrainingPlanStatus.Active,
            Version = 1,
            DateCreated = DateTime.UtcNow.AddDays(-3),
            Weeks =
            [
                new TrainingWeek
                {
                    WeekNumber = 1,
                    Status = WeekStatus.Published,
                    DatePublished = DateTime.UtcNow.AddDays(-2),
                    Days = TrainingPlanTestHelpers.MaterializeDays(
                        (1, new TrainingSession
                        {
                            SessionId = sessionId,
                            Name = "Standalone-Only Session",
                            Order = 1,
                            Workouts = [],
                            StandaloneExercises =
                            [
                                new SessionExercise
                                {
                                    ExerciseId = plankInstanceId,
                                    ExerciseExternalId = plankId,
                                    ExerciseName = "Plank",
                                    Order = 1,
                                    Sets = [new ExerciseSet { SetNumber = 1, Type = SetType.Normal, DurationSeconds = 60 }]
                                }
                            ]
                        }))
                }
            ]
        };

        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
            await mongo.Exercises.InsertOneAsync(plankExercise, cancellationToken: TestContext.Current.CancellationToken);
            await mongo.TrainingPlans.InsertOneAsync(plan, cancellationToken: TestContext.Current.CancellationToken);
        }

        TestHelpers.SetBearerToken(httpClient, accessToken);
        var response = await httpClient.GetAsync(
            $"/client/training/plans/{planId}",
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };
        var body = await response.Content.ReadFromJsonAsync<FullPlanResponse>(
            jsonOptions,
            cancellationToken: TestContext.Current.CancellationToken);

        body.Should().NotBeNull();
        var session = body!.Weeks[0].Sessions[0];

        session.Workouts.Should().BeEmpty("this session has no workouts at all");
        session.TotalExerciseCount.Should().Be(1,
            "a standalone exercise must be counted even with zero workouts — previously this was 0");
        session.CompletedExerciseCount.Should().Be(0);

        session.StandaloneExercises.Should().HaveCount(1);
        session.StandaloneExercises[0].ExerciseId.Should().Be(plankInstanceId);
        session.StandaloneExercises[0].ExerciseExternalId.Should().Be(plankId);

        session.AllExercises.Should().HaveCount(1,
            "the flat AllExercises view must also include standalone exercises — previously it only walked Workouts");
        session.AllExercises[0].ExerciseId.Should().Be(plankInstanceId);
    }

    /// <summary>
    /// Dual placement (#857 phase 3a/3b): the same catalog exercise appears BOTH standalone on
    /// the session AND nested inside one of that session's workouts, as two distinct
    /// <see cref="SessionExercise.ExerciseId"/> instance values. Mirrors the shape of the QA
    /// fixture at <c>QaSeedRunner.QaDualPlacementSessionId</c>. Both instances must be counted
    /// and returned separately — collapsing on <c>ExerciseExternalId</c> would silently drop one.
    /// </summary>
    [Fact]
    public async Task GetFullPlan_WithDualPlacementSession_ReturnsBothInstancesSeparately()
    {
        var httpClient = factory.CreateClient();

        var clientEmail = UniqueEmail();
        await TestHelpers.RegisterAsync(httpClient, clientEmail, "TestPass1!", "Dual", "Placement", "Client");
        var (accessToken, _) = await TestHelpers.LoginAsync(httpClient, clientEmail, "TestPass1!");

        Guid clientUserId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await db.Users.FirstAsync(
                u => u.Email == clientEmail,
                TestContext.Current.CancellationToken);
            var profile = await db.ClientProfiles.FirstAsync(
                cp => cp.UserId == user.Id,
                TestContext.Current.CancellationToken);
            clientUserId = profile.UserId;
        }

        var wallBallId = Guid.NewGuid();
        var standaloneInstanceId = Guid.NewGuid();
        var nestedInstanceId = Guid.NewGuid();

        var wallBallExercise = new Exercise
        {
            ExternalId = wallBallId,
            Name = "Wall Ball",
            MuscleGroups = [MuscleGroup.Quadriceps, MuscleGroup.Shoulders],
            Equipment = ExerciseEquipment.Kettlebell,
            Category = ExerciseCategory.Strength,
            Difficulty = ExerciseDifficulty.Intermediate,
            IsActive = true,
            Source = "system",
            DateCreated = DateTime.UtcNow
        };

        var planId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var workoutId = Guid.NewGuid();

        var plan = new TrainingPlan
        {
            ExternalId = planId,
            ClientId = clientUserId,
            TrainerId = Guid.NewGuid(),
            Name = "Dual Placement Plan",
            Status = TrainingPlanStatus.Active,
            Version = 1,
            DateCreated = DateTime.UtcNow.AddDays(-3),
            Weeks =
            [
                new TrainingWeek
                {
                    WeekNumber = 1,
                    Status = WeekStatus.Published,
                    DatePublished = DateTime.UtcNow.AddDays(-2),
                    Days = TrainingPlanTestHelpers.MaterializeDays(
                        (1, new TrainingSession
                        {
                            SessionId = sessionId,
                            Name = "Standalone + Nested Session",
                            Order = 1,
                            Workouts =
                            [
                                new TrainingWorkout
                                {
                                    WorkoutId = workoutId,
                                    Order = 2,
                                    Name = "Hlavní",
                                    Exercises =
                                    [
                                        new SessionExercise
                                        {
                                            ExerciseId = nestedInstanceId,
                                            ExerciseExternalId = wallBallId,
                                            ExerciseName = "Wall Ball",
                                            Order = 1,
                                            Sets = [new ExerciseSet { SetNumber = 1, Type = SetType.Normal, Reps = 20 }]
                                        }
                                    ]
                                }
                            ],
                            StandaloneExercises =
                            [
                                new SessionExercise
                                {
                                    ExerciseId = standaloneInstanceId,
                                    ExerciseExternalId = wallBallId,
                                    ExerciseName = "Wall Ball",
                                    Order = 1,
                                    Sets = [new ExerciseSet { SetNumber = 1, Type = SetType.Normal, Reps = 15 }]
                                }
                            ]
                        }))
                }
            ]
        };

        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
            await mongo.Exercises.InsertOneAsync(wallBallExercise, cancellationToken: TestContext.Current.CancellationToken);
            await mongo.TrainingPlans.InsertOneAsync(plan, cancellationToken: TestContext.Current.CancellationToken);
        }

        TestHelpers.SetBearerToken(httpClient, accessToken);
        var response = await httpClient.GetAsync(
            $"/client/training/plans/{planId}",
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };
        var body = await response.Content.ReadFromJsonAsync<FullPlanResponse>(
            jsonOptions,
            cancellationToken: TestContext.Current.CancellationToken);

        body.Should().NotBeNull();
        var session = body!.Weeks[0].Sessions[0];

        session.TotalExerciseCount.Should().Be(2,
            "the same catalog exercise placed both standalone and nested must count as TWO instances");

        session.StandaloneExercises.Should().HaveCount(1);
        session.StandaloneExercises[0].ExerciseId.Should().Be(standaloneInstanceId);

        session.Workouts.Should().HaveCount(1);
        session.Workouts[0].Exercises.Should().HaveCount(1);
        session.Workouts[0].Exercises[0].ExerciseId.Should().Be(nestedInstanceId);

        session.AllExercises.Should().HaveCount(2,
            "the flat view must include both instances — collapsing on ExerciseExternalId would drop one");
        session.AllExercises.Select(e => e.ExerciseId).Should().BeEquivalentTo([standaloneInstanceId, nestedInstanceId]);

        // Shared Order sequence: standalone exercise Order=1 comes before the workout's Order=2,
        // so the standalone instance must appear first in the flat merge.
        session.AllExercises[0].ExerciseId.Should().Be(standaloneInstanceId,
            "standalone Exercise.Order=1 precedes the workout's Order=2 in the shared sequence");
        session.AllExercises[1].ExerciseId.Should().Be(nestedInstanceId);
    }

    /// <summary>
    /// #885 (live-training-assistant path): the same catalog exercise is placed BOTH standalone
    /// and nested in a workout within one session (mirroring the shape of
    /// <see cref="GetFullPlan_WithDualPlacementSession_ReturnsBothInstancesSeparately"/>). The
    /// client fully logs the NESTED placement's set via the live-training assistant —
    /// <see cref="SessionExecution.Performance"/> carries the containing
    /// <see cref="LoggedWorkout.WorkoutId"/> matching the REAL nested
    /// <see cref="TrainingWorkout.WorkoutId"/>, exactly the shape <c>UpdateWorkoutEndpoint</c>
    /// persists. <see cref="SessionExecution.CompletedExerciseInstanceIds"/> is deliberately
    /// empty — no checkbox/instance-keyed signal is involved at all. Before the #885 fix, the
    /// read-side aggregation collapsed both placements onto one (sessionId, ExerciseExternalId,
    /// setNumber) key, so the standalone placement would incorrectly report the nested
    /// placement's completion too.
    /// </summary>
    [Fact]
    public async Task GetFullPlan_DualPlacementPerformance_NestedCompletedViaLiveLog_StandaloneStaysIncomplete()
    {
        var httpClient = factory.CreateClient();

        var clientEmail = UniqueEmail();
        await TestHelpers.RegisterAsync(httpClient, clientEmail, "TestPass1!", "Dual", "LiveLog", "Client");
        var (accessToken, _) = await TestHelpers.LoginAsync(httpClient, clientEmail, "TestPass1!");

        Guid clientUserId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await db.Users.FirstAsync(u => u.Email == clientEmail, TestContext.Current.CancellationToken);
            var profile = await db.ClientProfiles.FirstAsync(cp => cp.UserId == user.Id, TestContext.Current.CancellationToken);
            clientUserId = profile.UserId;
        }

        var wallBallId = Guid.NewGuid();
        var standaloneInstanceId = Guid.NewGuid();
        var nestedInstanceId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var workoutId = Guid.NewGuid();

        var plan = new TrainingPlan
        {
            ExternalId = planId,
            ClientId = clientUserId,
            TrainerId = Guid.NewGuid(),
            Name = "Dual Placement Live Log Plan",
            Status = TrainingPlanStatus.Active,
            Version = 1,
            DateCreated = DateTime.UtcNow.AddDays(-3),
            Weeks =
            [
                new TrainingWeek
                {
                    WeekNumber = 1,
                    Status = WeekStatus.Published,
                    DatePublished = DateTime.UtcNow.AddDays(-2),
                    Days = TrainingPlanTestHelpers.MaterializeDays(
                        (1, new TrainingSession
                        {
                            SessionId = sessionId,
                            Name = "Standalone + Nested Session",
                            Order = 1,
                            Workouts =
                            [
                                new TrainingWorkout
                                {
                                    WorkoutId = workoutId,
                                    Order = 2,
                                    Name = "Hlavní",
                                    Exercises =
                                    [
                                        new SessionExercise
                                        {
                                            ExerciseId = nestedInstanceId,
                                            ExerciseExternalId = wallBallId,
                                            ExerciseName = "Wall Ball",
                                            Order = 1,
                                            Sets = [new ExerciseSet { SetNumber = 1, Type = SetType.Normal, Reps = 20 }]
                                        }
                                    ]
                                }
                            ],
                            StandaloneExercises =
                            [
                                new SessionExercise
                                {
                                    ExerciseId = standaloneInstanceId,
                                    ExerciseExternalId = wallBallId,
                                    ExerciseName = "Wall Ball",
                                    Order = 1,
                                    Sets = [new ExerciseSet { SetNumber = 1, Type = SetType.Normal, Reps = 15 }]
                                }
                            ]
                        }))
                }
            ]
        };

        var startedAt = DateTime.UtcNow.AddMinutes(-10);
        var execution = new SessionExecution
        {
            ExternalId = Guid.NewGuid(),
            ClientId = clientUserId,
            PlanId = planId,
            SessionId = sessionId,
            Date = SessionExecution.ToCompletionDateUtc(startedAt),
            Status = SessionExecutionStatus.Partial,
            CompletedExerciseInstanceIds = [],
            DateCreated = startedAt,
            Performance = new SessionExecutionPerformance
            {
                StartedAt = startedAt,
                Workouts =
                [
                    new LoggedWorkout
                    {
                        WorkoutId = workoutId, // matches the REAL nested TrainingWorkout.WorkoutId
                        Order = 0,
                        Name = "Hlavní",
                        Exercises =
                        [
                            new WorkoutExercise
                            {
                                ExerciseExternalId = wallBallId,
                                ExerciseName = "Wall Ball",
                                Sets = [new WorkoutSet { SetNumber = 1, Reps = 20, CompletedAt = startedAt.AddMinutes(2) }]
                            }
                        ]
                    }
                ]
            }
        };

        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
            await mongo.TrainingPlans.InsertOneAsync(plan, cancellationToken: TestContext.Current.CancellationToken);
            await mongo.SessionExecutions.InsertOneAsync(execution, cancellationToken: TestContext.Current.CancellationToken);
        }

        TestHelpers.SetBearerToken(httpClient, accessToken);
        var response = await httpClient.GetAsync($"/client/training/plans/{planId}", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };
        var rawBody = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var body = JsonSerializer.Deserialize<FullPlanResponse>(rawBody, jsonOptions);

        body.Should().NotBeNull($"raw response was: {rawBody}");
        var session = body!.Weeks[0].Sessions[0];

        var nestedExercise = session.Workouts.Single().Exercises.Single(e => e.ExerciseId == nestedInstanceId);
        nestedExercise.Sets.Single(s => s.SetNumber == 1).CompletedAt.Should().NotBeNull(
            $"the nested placement's set was actually logged via the live-training assistant. raw: {rawBody}");
        nestedExercise.IsCompleted.Should().BeTrue();

        var standaloneExercise = session.StandaloneExercises.Single(e => e.ExerciseId == standaloneInstanceId);
        standaloneExercise.Sets.Single(s => s.SetNumber == 1).CompletedAt.Should().BeNull(
            $"the standalone placement was never logged — collapsing on ExerciseExternalId alone would " +
            $"incorrectly leak the nested placement's completion onto it. raw: {rawBody}");
        standaloneExercise.IsCompleted.Should().BeFalse();
    }

    /// <summary>
    /// #885 symmetric case: same dual-placement session, but the STANDALONE placement's set is
    /// logged via the live-training assistant. The <see cref="LoggedWorkout.WorkoutId"/> here is a
    /// fresh id that does NOT match any nested <see cref="TrainingWorkout.WorkoutId"/> in the
    /// session — exactly the shape <c>UpdateWorkoutEndpoint</c>'s legacy single-workout fallback
    /// path assigns when the client logs a standalone exercise (which has no WorkoutId of its own
    /// to send). The nested placement must stay incomplete.
    /// </summary>
    [Fact]
    public async Task GetFullPlan_DualPlacementPerformance_StandaloneCompletedViaLiveLog_NestedStaysIncomplete()
    {
        var httpClient = factory.CreateClient();

        var clientEmail = UniqueEmail();
        await TestHelpers.RegisterAsync(httpClient, clientEmail, "TestPass1!", "Dual", "LiveLog2", "Client");
        var (accessToken, _) = await TestHelpers.LoginAsync(httpClient, clientEmail, "TestPass1!");

        Guid clientUserId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await db.Users.FirstAsync(u => u.Email == clientEmail, TestContext.Current.CancellationToken);
            var profile = await db.ClientProfiles.FirstAsync(cp => cp.UserId == user.Id, TestContext.Current.CancellationToken);
            clientUserId = profile.UserId;
        }

        var wallBallId = Guid.NewGuid();
        var standaloneInstanceId = Guid.NewGuid();
        var nestedInstanceId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var workoutId = Guid.NewGuid();

        var plan = new TrainingPlan
        {
            ExternalId = planId,
            ClientId = clientUserId,
            TrainerId = Guid.NewGuid(),
            Name = "Dual Placement Live Log Plan (standalone completed)",
            Status = TrainingPlanStatus.Active,
            Version = 1,
            DateCreated = DateTime.UtcNow.AddDays(-3),
            Weeks =
            [
                new TrainingWeek
                {
                    WeekNumber = 1,
                    Status = WeekStatus.Published,
                    DatePublished = DateTime.UtcNow.AddDays(-2),
                    Days = TrainingPlanTestHelpers.MaterializeDays(
                        (1, new TrainingSession
                        {
                            SessionId = sessionId,
                            Name = "Standalone + Nested Session",
                            Order = 1,
                            Workouts =
                            [
                                new TrainingWorkout
                                {
                                    WorkoutId = workoutId,
                                    Order = 2,
                                    Name = "Hlavní",
                                    Exercises =
                                    [
                                        new SessionExercise
                                        {
                                            ExerciseId = nestedInstanceId,
                                            ExerciseExternalId = wallBallId,
                                            ExerciseName = "Wall Ball",
                                            Order = 1,
                                            Sets = [new ExerciseSet { SetNumber = 1, Type = SetType.Normal, Reps = 20 }]
                                        }
                                    ]
                                }
                            ],
                            StandaloneExercises =
                            [
                                new SessionExercise
                                {
                                    ExerciseId = standaloneInstanceId,
                                    ExerciseExternalId = wallBallId,
                                    ExerciseName = "Wall Ball",
                                    Order = 1,
                                    Sets = [new ExerciseSet { SetNumber = 1, Type = SetType.Normal, Reps = 15 }]
                                }
                            ]
                        }))
                }
            ]
        };

        var startedAt = DateTime.UtcNow.AddMinutes(-10);
        var execution = new SessionExecution
        {
            ExternalId = Guid.NewGuid(),
            ClientId = clientUserId,
            PlanId = planId,
            SessionId = sessionId,
            Date = SessionExecution.ToCompletionDateUtc(startedAt),
            Status = SessionExecutionStatus.Partial,
            CompletedExerciseInstanceIds = [],
            DateCreated = startedAt,
            Performance = new SessionExecutionPerformance
            {
                StartedAt = startedAt,
                Workouts =
                [
                    new LoggedWorkout
                    {
                        // Fallback WorkoutId that matches NEITHER of the session's nested
                        // TrainingWorkout ids — the shape UpdateWorkoutEndpoint's legacy
                        // single-workout path assigns when logging a standalone exercise.
                        WorkoutId = Guid.NewGuid(),
                        Order = 0,
                        Name = "Hlavní",
                        Exercises =
                        [
                            new WorkoutExercise
                            {
                                ExerciseExternalId = wallBallId,
                                ExerciseName = "Wall Ball",
                                Sets = [new WorkoutSet { SetNumber = 1, Reps = 15, CompletedAt = startedAt.AddMinutes(2) }]
                            }
                        ]
                    }
                ]
            }
        };

        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
            await mongo.TrainingPlans.InsertOneAsync(plan, cancellationToken: TestContext.Current.CancellationToken);
            await mongo.SessionExecutions.InsertOneAsync(execution, cancellationToken: TestContext.Current.CancellationToken);
        }

        TestHelpers.SetBearerToken(httpClient, accessToken);
        var response = await httpClient.GetAsync($"/client/training/plans/{planId}", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };
        var rawBody = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var body = JsonSerializer.Deserialize<FullPlanResponse>(rawBody, jsonOptions);

        body.Should().NotBeNull($"raw response was: {rawBody}");
        var session = body!.Weeks[0].Sessions[0];

        var standaloneExercise = session.StandaloneExercises.Single(e => e.ExerciseId == standaloneInstanceId);
        standaloneExercise.Sets.Single(s => s.SetNumber == 1).CompletedAt.Should().NotBeNull(
            $"the standalone placement's set was actually logged via the live-training assistant. raw: {rawBody}");
        standaloneExercise.IsCompleted.Should().BeTrue();

        var nestedExercise = session.Workouts.Single().Exercises.Single(e => e.ExerciseId == nestedInstanceId);
        nestedExercise.Sets.Single(s => s.SetNumber == 1).CompletedAt.Should().BeNull(
            $"the nested placement was never logged — collapsing on ExerciseExternalId alone would " +
            $"incorrectly leak the standalone placement's completion onto it. raw: {rawBody}");
        nestedExercise.IsCompleted.Should().BeFalse();
    }

    // ── Legacy flat-exercise schema-on-read is retired (#837) ────────────────────
    //
    // The flat-`exercises`-no-sections scenario previously covered here
    // (WithBackfilledSections() at read time) is retired: a plan at this layer is
    // always sections/workouts-populated. (#857 subsequently deleted the boot-time
    // backfill that used to synthesize the modern shape from legacy flat `exercises`
    // plans — see MongoIndexInitializer and its TrainingTreeRestructureMigrationTests
    // absence-test coverage — legacy documents are simply left untouched now, not
    // migrated on read.)

    // ── WorkoutDto.IsCompleted tests ─────────────────────────────────────────────

    /// <summary>
    /// Empty-exercise section where MarkWholeDayComplete wrote a CompletedSectionIds entry
    /// must return IsCompleted = true.
    /// </summary>
    [Fact]
    public async Task Returns_IsCompleted_True_For_Empty_Section_With_Completion_Row()
    {
        var httpClient = factory.CreateClient();

        var clientEmail = UniqueEmail();
        await TestHelpers.RegisterAsync(httpClient, clientEmail, "TestPass1!", "Empty", "Section", "Client");
        var (accessToken, _) = await TestHelpers.LoginAsync(httpClient, clientEmail, "TestPass1!");

        Guid clientUserId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await db.Users.FirstAsync(
                u => u.Email == clientEmail,
                TestContext.Current.CancellationToken);
            var profile = await db.ClientProfiles.FirstAsync(
                cp => cp.UserId == user.Id,
                TestContext.Current.CancellationToken);
            clientUserId = profile.UserId;
        }

        var planId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var emptySectionId = Guid.NewGuid();

        var plan = new TrainingPlan
        {
            ExternalId = planId,
            ClientId = clientUserId,
            TrainerId = Guid.NewGuid(),
            Name = "Empty Section Plan",
            Status = TrainingPlanStatus.Active,
            Version = 1,
            DateCreated = DateTime.UtcNow.AddDays(-3),
            Weeks =
            [
                new TrainingWeek
                {
                    WeekNumber = 1,
                    Status = WeekStatus.Published,
                    DatePublished = DateTime.UtcNow.AddDays(-2),
                    Days = TrainingPlanTestHelpers.MaterializeDays(
(1, new TrainingSession
                        {
                            SessionId = sessionId,
                            Name = "Running",
                            Order = 1,
                            Workouts =
                            [
                                new TrainingWorkout
                                {
                                    WorkoutId = emptySectionId,
                                    Order = 0,
                                    Name = "Running",
                                    Exercises = []
                                }
                            ]
                        }))
                }
            ]
        };

        // Post-#841 the standalone TrainingCompletion document was unified into
        // SessionExecution — the lightweight Today-card checkbox flags (CompletedWorkoutIds,
        // CompletedExerciseInstanceIds) now live directly on it (Performance stays null).
        var completion = new SessionExecution
        {
            ExternalId = Guid.NewGuid(),
            ClientId = clientUserId,
            PlanId = planId,
            Date = SessionExecution.ToCompletionDateUtc(DateTime.UtcNow),
            SessionId = sessionId,
            Status = SessionExecutionStatus.Partial,
            CompletedExerciseInstanceIds = [],
            CompletedWorkoutIds = [emptySectionId],
            DateCreated = DateTime.UtcNow,
            Version = 1
        };

        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
            await mongo.TrainingPlans.InsertOneAsync(plan, cancellationToken: TestContext.Current.CancellationToken);
            await mongo.SessionExecutions.InsertOneAsync(completion, cancellationToken: TestContext.Current.CancellationToken);
        }

        TestHelpers.SetBearerToken(httpClient, accessToken);
        var response = await httpClient.GetAsync(
            $"/client/training/plans/{planId}",
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };
        var body = await response.Content.ReadFromJsonAsync<FullPlanResponse>(
            jsonOptions,
            cancellationToken: TestContext.Current.CancellationToken);

        body.Should().NotBeNull();
        var workout = body!.Weeks[0].Sessions[0].Workouts[0];
        workout.WorkoutId.Should().Be(emptySectionId);
        workout.Exercises.Should().BeEmpty();
        workout.IsCompleted.Should().BeTrue(
            "empty section was added to CompletedSectionIds by MarkWholeDayComplete");
    }

    /// <summary>
    /// Non-empty section where all exercises are completed via TrainingCompletion
    /// must return IsCompleted = true.
    /// </summary>
    [Fact]
    public async Task Returns_IsCompleted_True_For_NonEmpty_Section_With_All_Exercises_Done()
    {
        var httpClient = factory.CreateClient();

        var clientEmail = UniqueEmail();
        await TestHelpers.RegisterAsync(httpClient, clientEmail, "TestPass1!", "Nonempty", "Done", "Client");
        var (accessToken, _) = await TestHelpers.LoginAsync(httpClient, clientEmail, "TestPass1!");

        Guid clientUserId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await db.Users.FirstAsync(
                u => u.Email == clientEmail,
                TestContext.Current.CancellationToken);
            var profile = await db.ClientProfiles.FirstAsync(
                cp => cp.UserId == user.Id,
                TestContext.Current.CancellationToken);
            clientUserId = profile.UserId;
        }

        var planId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var ex1Id = Guid.NewGuid();
        var ex2Id = Guid.NewGuid();

        var ex1 = new Exercise
        {
            ExternalId = ex1Id,
            Name = "Squat",
            MuscleGroups = [MuscleGroup.Quadriceps],
            Equipment = ExerciseEquipment.Barbell,
            Category = ExerciseCategory.Strength,
            Difficulty = ExerciseDifficulty.Intermediate,
            IsActive = true,
            Source = "system",
            DateCreated = DateTime.UtcNow
        };

        var ex2 = new Exercise
        {
            ExternalId = ex2Id,
            Name = "Deadlift",
            MuscleGroups = [MuscleGroup.Hamstrings],
            Equipment = ExerciseEquipment.Barbell,
            Category = ExerciseCategory.Strength,
            Difficulty = ExerciseDifficulty.Intermediate,
            IsActive = true,
            Source = "system",
            DateCreated = DateTime.UtcNow
        };

        var plan = new TrainingPlan
        {
            ExternalId = planId,
            ClientId = clientUserId,
            TrainerId = Guid.NewGuid(),
            Name = "All Done Plan",
            Status = TrainingPlanStatus.Active,
            Version = 1,
            DateCreated = DateTime.UtcNow.AddDays(-3),
            Weeks =
            [
                new TrainingWeek
                {
                    WeekNumber = 1,
                    Status = WeekStatus.Published,
                    DatePublished = DateTime.UtcNow.AddDays(-2),
                    Days = TrainingPlanTestHelpers.MaterializeDays(
(1, new TrainingSession
                        {
                            SessionId = sessionId,
                            Name = "Leg Day",
                            Order = 1,
                            Workouts =
                            [
                                new TrainingWorkout
                                {
                                    WorkoutId = sectionId,
                                    Order = 0,
                                    Name = "Hlavní",
                                    Exercises =
                                    [
                                        new SessionExercise
                                        {
                                            ExerciseId = ex1Id,
                                            ExerciseExternalId = ex1Id,
                                            ExerciseName = "Squat",
                                            Order = 1,
                                            Sets = [new ExerciseSet { SetNumber = 1, Type = SetType.Normal, Reps = 5, WeightKg = 100 }]
                                        },
                                        new SessionExercise
                                        {
                                            ExerciseId = ex2Id,
                                            ExerciseExternalId = ex2Id,
                                            ExerciseName = "Deadlift",
                                            Order = 2,
                                            Sets = [new ExerciseSet { SetNumber = 1, Type = SetType.Normal, Reps = 5, WeightKg = 120 }]
                                        }
                                    ]
                                }
                            ]
                        }))
                }
            ]
        };

        // Mark both exercises complete via SessionExecution (post-#841 unification of the
        // standalone TrainingCompletion document — checkbox flags live on it directly).
        // Completion is keyed on the per-instance SessionExercise.ExerciseId, so this fixture
        // deliberately sets ExerciseId == ExerciseExternalId on the seeded exercises above.
        var completion = new SessionExecution
        {
            ExternalId = Guid.NewGuid(),
            ClientId = clientUserId,
            PlanId = planId,
            Date = SessionExecution.ToCompletionDateUtc(DateTime.UtcNow),
            SessionId = sessionId,
            Status = SessionExecutionStatus.Completed,
            CompletedExerciseInstanceIds = [ex1Id, ex2Id],
            DateCreated = DateTime.UtcNow,
            Version = 1
        };

        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
            await mongo.Exercises.InsertOneAsync(ex1, cancellationToken: TestContext.Current.CancellationToken);
            await mongo.Exercises.InsertOneAsync(ex2, cancellationToken: TestContext.Current.CancellationToken);
            await mongo.TrainingPlans.InsertOneAsync(plan, cancellationToken: TestContext.Current.CancellationToken);
            await mongo.SessionExecutions.InsertOneAsync(completion, cancellationToken: TestContext.Current.CancellationToken);
        }

        TestHelpers.SetBearerToken(httpClient, accessToken);
        var response = await httpClient.GetAsync(
            $"/client/training/plans/{planId}",
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };
        var body = await response.Content.ReadFromJsonAsync<FullPlanResponse>(
            jsonOptions,
            cancellationToken: TestContext.Current.CancellationToken);

        body.Should().NotBeNull();
        var workout = body!.Weeks[0].Sessions[0].Workouts[0];
        workout.Exercises.Should().HaveCount(2);
        workout.IsCompleted.Should().BeTrue("both exercises are marked complete via TrainingCompletion");
    }

    /// <summary>
    /// Non-empty section where only one of two exercises is done must return IsCompleted = false.
    /// </summary>
    [Fact]
    public async Task Returns_IsCompleted_False_For_Partially_Completed_NonEmpty_Section()
    {
        var httpClient = factory.CreateClient();

        var clientEmail = UniqueEmail();
        await TestHelpers.RegisterAsync(httpClient, clientEmail, "TestPass1!", "Partial", "Section", "Client");
        var (accessToken, _) = await TestHelpers.LoginAsync(httpClient, clientEmail, "TestPass1!");

        Guid clientUserId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await db.Users.FirstAsync(
                u => u.Email == clientEmail,
                TestContext.Current.CancellationToken);
            var profile = await db.ClientProfiles.FirstAsync(
                cp => cp.UserId == user.Id,
                TestContext.Current.CancellationToken);
            clientUserId = profile.UserId;
        }

        var planId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var sectionId = Guid.NewGuid();
        var ex1Id = Guid.NewGuid();
        var ex2Id = Guid.NewGuid();

        var ex1 = new Exercise
        {
            ExternalId = ex1Id,
            Name = "Pull-up",
            MuscleGroups = [MuscleGroup.Back],
            Equipment = ExerciseEquipment.Bodyweight,
            Category = ExerciseCategory.Strength,
            Difficulty = ExerciseDifficulty.Intermediate,
            IsActive = true,
            Source = "system",
            DateCreated = DateTime.UtcNow
        };

        var ex2 = new Exercise
        {
            ExternalId = ex2Id,
            Name = "Row",
            MuscleGroups = [MuscleGroup.Back],
            Equipment = ExerciseEquipment.Barbell,
            Category = ExerciseCategory.Strength,
            Difficulty = ExerciseDifficulty.Intermediate,
            IsActive = true,
            Source = "system",
            DateCreated = DateTime.UtcNow
        };

        var plan = new TrainingPlan
        {
            ExternalId = planId,
            ClientId = clientUserId,
            TrainerId = Guid.NewGuid(),
            Name = "Partial Section Plan",
            Status = TrainingPlanStatus.Active,
            Version = 1,
            DateCreated = DateTime.UtcNow.AddDays(-3),
            Weeks =
            [
                new TrainingWeek
                {
                    WeekNumber = 1,
                    Status = WeekStatus.Published,
                    DatePublished = DateTime.UtcNow.AddDays(-2),
                    Days = TrainingPlanTestHelpers.MaterializeDays(
(2, new TrainingSession
                        {
                            SessionId = sessionId,
                            Name = "Pull Day",
                            Order = 1,
                            Workouts =
                            [
                                new TrainingWorkout
                                {
                                    WorkoutId = sectionId,
                                    Order = 0,
                                    Name = "Hlavní",
                                    Exercises =
                                    [
                                        new SessionExercise
                                        {
                                            ExerciseId = ex1Id,
                                            ExerciseExternalId = ex1Id,
                                            ExerciseName = "Pull-up",
                                            Order = 1,
                                            Sets = [new ExerciseSet { SetNumber = 1, Type = SetType.Normal, Reps = 8 }]
                                        },
                                        new SessionExercise
                                        {
                                            ExerciseId = ex2Id,
                                            ExerciseExternalId = ex2Id,
                                            ExerciseName = "Row",
                                            Order = 2,
                                            Sets = [new ExerciseSet { SetNumber = 1, Type = SetType.Normal, Reps = 10 }]
                                        }
                                    ]
                                }
                            ]
                        }))
                }
            ]
        };

        // Only ex1 is marked complete (not ex2) — SessionExecution (post-#841 unification).
        var completion = new SessionExecution
        {
            ExternalId = Guid.NewGuid(),
            ClientId = clientUserId,
            PlanId = planId,
            Date = SessionExecution.ToCompletionDateUtc(DateTime.UtcNow),
            SessionId = sessionId,
            Status = SessionExecutionStatus.Partial,
            CompletedExerciseInstanceIds = [ex1Id],
            DateCreated = DateTime.UtcNow,
            Version = 1
        };

        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
            await mongo.Exercises.InsertOneAsync(ex1, cancellationToken: TestContext.Current.CancellationToken);
            await mongo.Exercises.InsertOneAsync(ex2, cancellationToken: TestContext.Current.CancellationToken);
            await mongo.TrainingPlans.InsertOneAsync(plan, cancellationToken: TestContext.Current.CancellationToken);
            await mongo.SessionExecutions.InsertOneAsync(completion, cancellationToken: TestContext.Current.CancellationToken);
        }

        TestHelpers.SetBearerToken(httpClient, accessToken);
        var response = await httpClient.GetAsync(
            $"/client/training/plans/{planId}",
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };
        var body = await response.Content.ReadFromJsonAsync<FullPlanResponse>(
            jsonOptions,
            cancellationToken: TestContext.Current.CancellationToken);

        body.Should().NotBeNull();
        var workout = body!.Weeks[0].Sessions[0].Workouts[0];
        workout.Exercises.Should().HaveCount(2);
        workout.IsCompleted.Should().BeFalse("only one of two exercises is done — partial completion");
    }

    /// <summary>
    /// When no TrainingCompletion row exists at all, IsCompleted must be false
    /// for both empty and non-empty sections.
    /// </summary>
    [Fact]
    public async Task Returns_IsCompleted_False_When_No_TrainingCompletion_Exists()
    {
        var httpClient = factory.CreateClient();

        var clientEmail = UniqueEmail();
        await TestHelpers.RegisterAsync(httpClient, clientEmail, "TestPass1!", "No", "Completion", "Client");
        var (accessToken, _) = await TestHelpers.LoginAsync(httpClient, clientEmail, "TestPass1!");

        Guid clientUserId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await db.Users.FirstAsync(
                u => u.Email == clientEmail,
                TestContext.Current.CancellationToken);
            var profile = await db.ClientProfiles.FirstAsync(
                cp => cp.UserId == user.Id,
                TestContext.Current.CancellationToken);
            clientUserId = profile.UserId;
        }

        var planId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var emptySectionId = Guid.NewGuid();
        var nonEmptySectionId = Guid.NewGuid();
        var exId = Guid.NewGuid();

        var exercise = new Exercise
        {
            ExternalId = exId,
            Name = "Lunge",
            MuscleGroups = [MuscleGroup.Quadriceps],
            Equipment = ExerciseEquipment.Bodyweight,
            Category = ExerciseCategory.Strength,
            Difficulty = ExerciseDifficulty.Beginner,
            IsActive = true,
            Source = "system",
            DateCreated = DateTime.UtcNow
        };

        var plan = new TrainingPlan
        {
            ExternalId = planId,
            ClientId = clientUserId,
            TrainerId = Guid.NewGuid(),
            Name = "No Completion Plan",
            Status = TrainingPlanStatus.Active,
            Version = 1,
            DateCreated = DateTime.UtcNow.AddDays(-3),
            Weeks =
            [
                new TrainingWeek
                {
                    WeekNumber = 1,
                    Status = WeekStatus.Published,
                    DatePublished = DateTime.UtcNow.AddDays(-2),
                    Days = TrainingPlanTestHelpers.MaterializeDays(
(3, new TrainingSession
                        {
                            SessionId = sessionId,
                            Name = "Mixed Day",
                            Order = 1,
                            Workouts =
                            [
                                new TrainingWorkout
                                {
                                    WorkoutId = emptySectionId,
                                    Order = 0,
                                    Name = "Running",
                                    Exercises = []
                                },
                                new TrainingWorkout
                                {
                                    WorkoutId = nonEmptySectionId,
                                    Order = 1,
                                    Name = "Strength",
                                    Exercises =
                                    [
                                        new SessionExercise
                                        {
                                            ExerciseExternalId = exId,
                                            ExerciseName = "Lunge",
                                            Order = 1,
                                            Sets = [new ExerciseSet { SetNumber = 1, Type = SetType.Normal, Reps = 12 }]
                                        }
                                    ]
                                }
                            ]
                        }))
                }
            ]
        };

        // No TrainingCompletion inserted at all
        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
            await mongo.Exercises.InsertOneAsync(exercise, cancellationToken: TestContext.Current.CancellationToken);
            await mongo.TrainingPlans.InsertOneAsync(plan, cancellationToken: TestContext.Current.CancellationToken);
        }

        TestHelpers.SetBearerToken(httpClient, accessToken);
        var response = await httpClient.GetAsync(
            $"/client/training/plans/{planId}",
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };
        var body = await response.Content.ReadFromJsonAsync<FullPlanResponse>(
            jsonOptions,
            cancellationToken: TestContext.Current.CancellationToken);

        body.Should().NotBeNull();
        var workouts = body!.Weeks[0].Sessions[0].Workouts;
        workouts.Should().HaveCount(2);

        var emptyWorkout = workouts.First(w => w.WorkoutId == emptySectionId);
        emptyWorkout.IsCompleted.Should().BeFalse("no TrainingCompletion exists — empty section must be false");

        var nonEmptyWorkout = workouts.First(w => w.WorkoutId == nonEmptySectionId);
        nonEmptyWorkout.IsCompleted.Should().BeFalse("no TrainingCompletion exists — non-empty section must be false");
    }

    // ── #877: instance-resolved IsCompleted ──────────────────────────────────────

    /// <summary>
    /// #877 (set-less exercise gap): builds a dual-placement session — one catalog exercise both
    /// standalone and nested in a workout — where BOTH placements have ZERO prescribed sets, the
    /// shape the QA fixture (<c>QaSeedRunner.QaDualPlacementSessionId</c>) had before #879 gave it
    /// prescribed sets of its own. Before #877, <c>isCompleted</c> required
    /// <c>setDtos.Count &gt; 0</c>, so a set-less checkbox-completed exercise could never report
    /// complete — this test would have failed (both instances always false) under that logic.
    /// Only the standalone instance is checkbox-completed here; the nested instance must stay
    /// incomplete.
    /// </summary>
    [Fact]
    public async Task GetFullPlan_DualPlacementSetlessExercise_OnlyStandaloneCheckboxCompleted_OnlyThatInstanceReportsCompleted()
    {
        var httpClient = factory.CreateClient();

        var clientEmail = UniqueEmail();
        await TestHelpers.RegisterAsync(httpClient, clientEmail, "TestPass1!", "Setless", "Dual", "Client");
        var (accessToken, _) = await TestHelpers.LoginAsync(httpClient, clientEmail, "TestPass1!");

        Guid clientUserId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await db.Users.FirstAsync(
                u => u.Email == clientEmail,
                TestContext.Current.CancellationToken);
            var profile = await db.ClientProfiles.FirstAsync(
                cp => cp.UserId == user.Id,
                TestContext.Current.CancellationToken);
            clientUserId = profile.UserId;
        }

        var catalogExerciseId = Guid.NewGuid();
        var standaloneInstanceId = Guid.NewGuid();
        var nestedInstanceId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var workoutId = Guid.NewGuid();

        var plan = new TrainingPlan
        {
            ExternalId = planId,
            ClientId = clientUserId,
            TrainerId = Guid.NewGuid(),
            Name = "Setless Dual Placement Plan",
            Status = TrainingPlanStatus.Active,
            Version = 1,
            DateCreated = DateTime.UtcNow.AddDays(-3),
            Weeks =
            [
                new TrainingWeek
                {
                    WeekNumber = 1,
                    Status = WeekStatus.Published,
                    DatePublished = DateTime.UtcNow.AddDays(-2),
                    Days = TrainingPlanTestHelpers.MaterializeDays(
                        (1, new TrainingSession
                        {
                            SessionId = sessionId,
                            Name = "Setless Standalone + Nested Session",
                            Order = 1,
                            Workouts =
                            [
                                new TrainingWorkout
                                {
                                    WorkoutId = workoutId,
                                    Order = 2,
                                    Name = "Hlavní",
                                    Exercises =
                                    [
                                        new SessionExercise
                                        {
                                            ExerciseId = nestedInstanceId,
                                            ExerciseExternalId = catalogExerciseId,
                                            ExerciseName = "Plank",
                                            Order = 1,
                                            Sets = []
                                        }
                                    ]
                                }
                            ],
                            StandaloneExercises =
                            [
                                new SessionExercise
                                {
                                    ExerciseId = standaloneInstanceId,
                                    ExerciseExternalId = catalogExerciseId,
                                    ExerciseName = "Plank",
                                    Order = 1,
                                    Sets = []
                                }
                            ]
                        }))
                }
            ]
        };

        var execution = new SessionExecution
        {
            ExternalId = Guid.NewGuid(),
            ClientId = clientUserId,
            PlanId = planId,
            SessionId = sessionId,
            Date = SessionExecution.ToCompletionDateUtc(DateTime.UtcNow),
            Status = SessionExecutionStatus.Partial,
            CompletedExerciseInstanceIds = [standaloneInstanceId],
            DateCreated = DateTime.UtcNow,
            Version = 1
        };

        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
            await mongo.TrainingPlans.InsertOneAsync(plan, cancellationToken: TestContext.Current.CancellationToken);
            await mongo.SessionExecutions.InsertOneAsync(execution, cancellationToken: TestContext.Current.CancellationToken);
        }

        TestHelpers.SetBearerToken(httpClient, accessToken);
        var response = await httpClient.GetAsync(
            $"/client/training/plans/{planId}",
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };
        var rawBody = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var body = JsonSerializer.Deserialize<FullPlanResponse>(rawBody, jsonOptions);

        body.Should().NotBeNull($"raw response was: {rawBody}");
        var session = body!.Weeks[0].Sessions[0];

        var standaloneExercise = session.StandaloneExercises.Single(e => e.ExerciseId == standaloneInstanceId);
        standaloneExercise.Sets.Should().BeEmpty("this placement has zero prescribed sets");
        standaloneExercise.IsCompleted.Should().BeTrue(
            $"the standalone instance was checkbox-completed and must report complete even with zero sets. raw: {rawBody}");

        var nestedExercise = session.Workouts.Single().Exercises.Single(e => e.ExerciseId == nestedInstanceId);
        nestedExercise.Sets.Should().BeEmpty("this placement also has zero prescribed sets");
        nestedExercise.IsCompleted.Should().BeFalse(
            "only the standalone instance was checkbox-completed — the nested instance sharing the same catalog id must stay incomplete");
    }

    /// <summary>
    /// #877: same set-less dual-placement shape as above, but BOTH instances are
    /// checkbox-completed. Both must report complete.
    /// </summary>
    [Fact]
    public async Task GetFullPlan_DualPlacementSetlessExercise_BothInstancesCheckboxCompleted_BothReportCompleted()
    {
        var httpClient = factory.CreateClient();

        var clientEmail = UniqueEmail();
        await TestHelpers.RegisterAsync(httpClient, clientEmail, "TestPass1!", "Setless", "Dual", "Client");
        var (accessToken, _) = await TestHelpers.LoginAsync(httpClient, clientEmail, "TestPass1!");

        Guid clientUserId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await db.Users.FirstAsync(
                u => u.Email == clientEmail,
                TestContext.Current.CancellationToken);
            var profile = await db.ClientProfiles.FirstAsync(
                cp => cp.UserId == user.Id,
                TestContext.Current.CancellationToken);
            clientUserId = profile.UserId;
        }

        var catalogExerciseId = Guid.NewGuid();
        var standaloneInstanceId = Guid.NewGuid();
        var nestedInstanceId = Guid.NewGuid();
        var planId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var workoutId = Guid.NewGuid();

        var plan = new TrainingPlan
        {
            ExternalId = planId,
            ClientId = clientUserId,
            TrainerId = Guid.NewGuid(),
            Name = "Setless Dual Placement Plan (both complete)",
            Status = TrainingPlanStatus.Active,
            Version = 1,
            DateCreated = DateTime.UtcNow.AddDays(-3),
            Weeks =
            [
                new TrainingWeek
                {
                    WeekNumber = 1,
                    Status = WeekStatus.Published,
                    DatePublished = DateTime.UtcNow.AddDays(-2),
                    Days = TrainingPlanTestHelpers.MaterializeDays(
                        (1, new TrainingSession
                        {
                            SessionId = sessionId,
                            Name = "Setless Standalone + Nested Session",
                            Order = 1,
                            Workouts =
                            [
                                new TrainingWorkout
                                {
                                    WorkoutId = workoutId,
                                    Order = 2,
                                    Name = "Hlavní",
                                    Exercises =
                                    [
                                        new SessionExercise
                                        {
                                            ExerciseId = nestedInstanceId,
                                            ExerciseExternalId = catalogExerciseId,
                                            ExerciseName = "Plank",
                                            Order = 1,
                                            Sets = []
                                        }
                                    ]
                                }
                            ],
                            StandaloneExercises =
                            [
                                new SessionExercise
                                {
                                    ExerciseId = standaloneInstanceId,
                                    ExerciseExternalId = catalogExerciseId,
                                    ExerciseName = "Plank",
                                    Order = 1,
                                    Sets = []
                                }
                            ]
                        }))
                }
            ]
        };

        var execution = new SessionExecution
        {
            ExternalId = Guid.NewGuid(),
            ClientId = clientUserId,
            PlanId = planId,
            SessionId = sessionId,
            Date = SessionExecution.ToCompletionDateUtc(DateTime.UtcNow),
            Status = SessionExecutionStatus.Completed,
            CompletedExerciseInstanceIds = [standaloneInstanceId, nestedInstanceId],
            DateCreated = DateTime.UtcNow,
            Version = 1
        };

        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
            await mongo.TrainingPlans.InsertOneAsync(plan, cancellationToken: TestContext.Current.CancellationToken);
            await mongo.SessionExecutions.InsertOneAsync(execution, cancellationToken: TestContext.Current.CancellationToken);
        }

        TestHelpers.SetBearerToken(httpClient, accessToken);
        var response = await httpClient.GetAsync(
            $"/client/training/plans/{planId}",
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };
        var rawBody = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var body = JsonSerializer.Deserialize<FullPlanResponse>(rawBody, jsonOptions);

        body.Should().NotBeNull($"raw response was: {rawBody}");
        var session = body!.Weeks[0].Sessions[0];

        session.StandaloneExercises.Single(e => e.ExerciseId == standaloneInstanceId).IsCompleted.Should().BeTrue(
            $"both instances were checkbox-completed. raw: {rawBody}");
        session.Workouts.Single().Exercises.Single(e => e.ExerciseId == nestedInstanceId).IsCompleted.Should().BeTrue(
            $"both instances were checkbox-completed. raw: {rawBody}");
        session.CompletedExerciseCount.Should().Be(2, "both instances counted as separately completed");
    }

    // ── Local response DTOs (per slice rules — not shared across features) ────────

    private record FullPlanResponse(
        Guid PlanId,
        string PlanName,
        string Status,
        DateTime? StartDate,
        int? CurrentWeek,
        int TotalWeeks,
        int PublishedWeekCount,
        Guid? QuestionnaireResponseId,
        DateTime? DateCompleted,
        List<WeekResponse> Weeks);

    private record WeekResponse(
        int WeekNumber,
        string Status,
        DateTime? DatePublished,
        DateTime? WeekStartDate,
        DateTime? WeekEndDate,
        Dictionary<int, string> DayNotes,
        List<SessionResponse> Sessions);

    private record SessionResponse(
        Guid SessionId,
        int DayOfWeek,
        string Name,
        int Order,
        string? Notes,
        int CompletedExerciseCount,
        int TotalExerciseCount,
        int? EstimatedDurationMinutes,
        List<WorkoutResponse> Workouts,
        List<ExerciseResponse> AllExercises,
        List<ExerciseResponse> StandaloneExercises);

    private record WorkoutResponse(
        Guid WorkoutId,
        int Order,
        string Name,
        string? Format,
        WodConfigResponse? FormatConfig,
        string? Notes,
        bool IsCompleted,
        List<ExerciseResponse> Exercises);

    private record WodConfigResponse(
        int? TimeCapSeconds,
        int? IntervalSeconds,
        int? TotalRounds,
        int? WorkSeconds,
        int? RestSeconds);

    private record ExerciseResponse(
        Guid ExerciseId,
        Guid ExerciseExternalId,
        string ExerciseName,
        int Order,
        string? Notes,
        int? RestSeconds,
        List<MuscleGroup> MuscleGroups,
        bool IsCompleted,
        List<SetResponse> Sets);

    private record SetResponse(
        int SetNumber,
        string Type,
        int? Reps,
        decimal? WeightKg,
        int? DurationSeconds,
        int? RestSeconds,
        DateTime? CompletedAt);
}
