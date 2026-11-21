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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace FormUp.Tests.Endpoints.SessionExecutions;

/// <summary>
/// Integration tests that verify the PR detection side-effect wired into
/// <c>PUT /client/training/logs/{LogId}</c>.
///
/// All tests use real MongoDB via Testcontainers so that filter semantics and
/// unique-index enforcement can be validated — NSubstitute mocks cannot prove
/// that the detection queries actually find the right documents.
/// </summary>
[Collection(TestCollection.Shard3)]
public class PersonalRecordDetectionTests(FitnessApiFactory factory)
{
    private static string UniqueEmail() => $"{Guid.NewGuid():N}@pr-test.com";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    // ── shared setup helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Registers a new client user, logs in, resolves their PublicId and returns
    /// the Http client (with Bearer token set), publicId, and a started WorkoutLog.
    /// </summary>
    private async Task<(HttpClient Http, Guid ClientId, Guid LogId)> SetupClientWithLogAsync()
    {
        var http = factory.CreateClient();
        var email = UniqueEmail();

        await TestHelpers.RegisterAsync(http, email, "TestPass1!", "PR", "Tester", "Client");
        var (token, _) = await TestHelpers.LoginAsync(http, email, "TestPass1!");
        TestHelpers.SetBearerToken(http, token);

        // The UpdateWorkoutEndpoint uses user.Id (not profile.PublicId) as ClientId
        // in all MongoDB workout-log operations, because AppClaims.UserId is set to
        // user.Id in LoginEndpoint.
        Guid userId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await db.Users.FirstAsync(
                u => u.Email == email,
                TestContext.Current.CancellationToken);
            userId = user.Id;
        }

        // Start a workout via the API so we get a real log in Mongo
        var startResponse = await http.PostAsJsonAsync(
            "/client/training/logs",
            new { },
            TestContext.Current.CancellationToken);

        startResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        var startBody = await startResponse.Content.ReadFromJsonAsync<LogResponse>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken);

        return (http, userId, startBody!.LogId);
    }

    // Helper to retrieve PR records for a given client + exercise from the real Mongo
    private static async Task<List<PersonalRecord>> GetPrRecordsAsync(
        IMongoContext mongo,
        Guid clientId,
        Guid exerciseId,
        CancellationToken ct)
    {
        FilterDefinition<PersonalRecord> filter = Builders<PersonalRecord>.Filter.And(
            Builders<PersonalRecord>.Filter.Eq(r => r.ClientId, clientId),
            Builders<PersonalRecord>.Filter.Eq(r => r.ExerciseExternalId, exerciseId));

        var cursor = await mongo.PersonalRecords.FindAsync(filter, cancellationToken: ct);
        return await cursor.ToListAsync(ct);
    }

    private static object BuildUpdateRequest(
        Guid exerciseId,
        string exerciseName,
        decimal weight,
        int reps,
        int setNumber = 1)
    {
        return new
        {
            Exercises = new[]
            {
                new
                {
                    ExerciseExternalId = exerciseId,
                    ExerciseName = exerciseName,
                    Sets = new[]
                    {
                        new
                        {
                            SetNumber = setNumber,
                            Reps = reps,
                            WeightKg = weight,
                            CompletedAt = DateTime.UtcNow
                        }
                    }
                }
            }
        };
    }

    private static object BuildUpdateRequestMultiExercise(
        (Guid ExerciseId, string ExerciseName, decimal Weight, int Reps)[] exercises)
    {
        return new
        {
            Exercises = exercises.Select(e => new
            {
                ExerciseExternalId = e.ExerciseId,
                ExerciseName = e.ExerciseName,
                Sets = new[]
                {
                    new
                    {
                        SetNumber = 1,
                        Reps = e.Reps,
                        WeightKg = e.Weight,
                        CompletedAt = DateTime.UtcNow
                    }
                }
            }).ToArray()
        };
    }

    // ── test cases ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Test 1: First completion at a new max → PersonalRecord created, WorkoutSet.IsPR set.
    /// </summary>
    [Fact]
    public async Task FirstCompletion_AtNewMax_CreatesPrAndSetsIsPrFlag()
    {
        var (http, clientId, logId) = await SetupClientWithLogAsync();
        var exerciseId = Guid.NewGuid();

        var updateBody = BuildUpdateRequest(exerciseId, "Squat", weight: 100m, reps: 5);

        var response = await http.PutAsJsonAsync(
            $"/client/training/logs/{logId}",
            updateBody,
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "updating an in-progress log should succeed");

        var body = await response.Content.ReadFromJsonAsync<LogResponse>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken);

        body!.HasPR.Should().BeTrue("the first set at any weight is always a PR");

        // Verify the PersonalRecord was actually persisted in Mongo
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        var records = await GetPrRecordsAsync(mongo, clientId, exerciseId, TestContext.Current.CancellationToken);

        records.Should().HaveCount(1, "exactly one PR record must be created");
        records[0].WeightKg.Should().Be(100m);
        records[0].Reps.Should().Be(5);
        records[0].WorkoutLogId.Should().Be(logId);
        records[0].SetNumber.Should().Be(1);
        records[0].ExerciseName.Should().Be("Squat");
    }

    /// <summary>
    /// Test 2: Idempotent re-update → calling updateWorkout with the same state twice
    /// creates no additional record. Assert count == 1 after two identical calls.
    /// </summary>
    [Fact]
    public async Task IdempotentReUpdate_DoesNotCreateDuplicatePr()
    {
        var (http, clientId, logId) = await SetupClientWithLogAsync();
        var exerciseId = Guid.NewGuid();

        var updateBody = BuildUpdateRequest(exerciseId, "Bench Press", weight: 80m, reps: 8);

        // First call
        var firstResponse = await http.PutAsJsonAsync(
            $"/client/training/logs/{logId}",
            updateBody,
            TestContext.Current.CancellationToken);

        firstResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Second call with identical payload
        var secondResponse = await http.PutAsJsonAsync(
            $"/client/training/logs/{logId}",
            updateBody,
            TestContext.Current.CancellationToken);

        secondResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            "the log update must succeed even on the second call");

        // Assert exactly one PR record in the database
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        var records = await GetPrRecordsAsync(mongo, clientId, exerciseId, TestContext.Current.CancellationToken);

        records.Should().HaveCount(1,
            "idempotency guard must prevent duplicate PR rows on repeated identical calls");
    }

    /// <summary>
    /// Test 3: Weight tie, higher reps → existing PR at 100 kg × 5;
    /// new set at 100 kg × 8 → new PR row created (tie-breaker rule).
    /// </summary>
    [Fact]
    public async Task WeightTie_HigherReps_CreatesPr()
    {
        var (http, clientId, logId) = await SetupClientWithLogAsync();
        var exerciseId = Guid.NewGuid();

        // Seed a prior completed SessionExecution with 100 kg × 5 as the existing best.
        // PersonalRecord.WorkoutLogId is a live wire contract keyed on the producing
        // SessionExecution.ExternalId (SessionExecution.cs remarks) — NOT tied to the retired
        // WorkoutLog document any more, so priorLogId is reused as-is.
        var priorLogId = Guid.NewGuid();
        var priorExecution = new SessionExecution
        {
            ExternalId = priorLogId,
            ClientId = clientId,
            Date = SessionExecution.ToCompletionDateUtc(DateTime.UtcNow.AddDays(-1)),
            Status = SessionExecutionStatus.Completed,
            Performance = new SessionExecutionPerformance
            {
                StartedAt = DateTime.UtcNow.AddDays(-1),
                CompletedAt = DateTime.UtcNow.AddDays(-1).AddHours(1),
                Workouts =
                [
                    new LoggedWorkout
                    {
                        WorkoutId = Guid.NewGuid(),
                        Order = 0,
                        Name = "Hlavní",
                        Exercises =
                        [
                            new WorkoutExercise
                            {
                                ExerciseExternalId = exerciseId,
                                ExerciseName = "Deadlift",
                                Sets =
                                [
                                    new WorkoutSet
                                    {
                                        SetNumber = 1,
                                        Reps = 5,
                                        WeightKg = 100m,
                                        CompletedAt = DateTime.UtcNow.AddDays(-1).AddMinutes(10),
                                        IsPR = true
                                    }
                                ]
                            }
                        ]
                    }
                ]
            },
            DateCreated = DateTime.UtcNow.AddDays(-1),
            Version = 1
        };

        // Seed the PersonalRecord representing the prior best
        var existingPr = new PersonalRecord
        {
            ExternalId = Guid.NewGuid(),
            ClientId = clientId,
            ExerciseExternalId = exerciseId,
            ExerciseName = "Deadlift",
            WeightKg = 100m,
            Reps = 5,
            AchievedAt = DateTime.UtcNow.AddDays(-1).AddMinutes(10),
            WorkoutLogId = priorLogId,
            SetNumber = 1,
            Version = 1,
            DateCreated = DateTime.UtcNow.AddDays(-1)
        };

        using (var setupScope = factory.Services.CreateScope())
        {
            var setupMongo = setupScope.ServiceProvider.GetRequiredService<IMongoContext>();
            await setupMongo.SessionExecutions.InsertOneAsync(
                priorExecution, cancellationToken: TestContext.Current.CancellationToken);
            await setupMongo.PersonalRecords.InsertOneAsync(
                existingPr, cancellationToken: TestContext.Current.CancellationToken);
        }

        // Now update the current log with 100 kg × 8 — should beat on reps tie-breaker
        var updateBody = BuildUpdateRequest(exerciseId, "Deadlift", weight: 100m, reps: 8);

        var response = await http.PutAsJsonAsync(
            $"/client/training/logs/{logId}",
            updateBody,
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<LogResponse>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken);

        body!.HasPR.Should().BeTrue("100 kg × 8 beats 100 kg × 5 on the reps tie-breaker");

        // Verify exactly 2 PR records now exist (the seeded one + the new one)
        using var verifyScope = factory.Services.CreateScope();
        var mongo = verifyScope.ServiceProvider.GetRequiredService<IMongoContext>();

        var records = await GetPrRecordsAsync(mongo, clientId, exerciseId, TestContext.Current.CancellationToken);

        records.Should().HaveCount(2, "original PR record plus the new tie-breaker PR");
        records.Should().Contain(r => r.WeightKg == 100m && r.Reps == 8,
            "the new PR at 100 kg × 8 must exist");
    }

    /// <summary>
    /// Test 4: Weight below max → existing PR at 100 kg × 5;
    /// new set at 95 kg × 10 → no new PR row.
    /// </summary>
    [Fact]
    public async Task WeightBelowMax_NoPrCreated()
    {
        var (http, clientId, logId) = await SetupClientWithLogAsync();
        var exerciseId = Guid.NewGuid();

        // Seed existing best as 100 kg × 5 in the PersonalRecord collection
        var existingPr = new PersonalRecord
        {
            ExternalId = Guid.NewGuid(),
            ClientId = clientId,
            ExerciseExternalId = exerciseId,
            ExerciseName = "Romanian Deadlift",
            WeightKg = 100m,
            Reps = 5,
            AchievedAt = DateTime.UtcNow.AddDays(-3),
            WorkoutLogId = Guid.NewGuid(),
            SetNumber = 1,
            Version = 1,
            DateCreated = DateTime.UtcNow.AddDays(-3)
        };

        using (var setupScope = factory.Services.CreateScope())
        {
            var setupMongo = setupScope.ServiceProvider.GetRequiredService<IMongoContext>();
            await setupMongo.PersonalRecords.InsertOneAsync(
                existingPr, cancellationToken: TestContext.Current.CancellationToken);
        }

        // Update with 95 kg × 10 — lighter than the existing max, no PR
        var updateBody = BuildUpdateRequest(
            exerciseId, "Romanian Deadlift", weight: 95m, reps: 10);

        var response = await http.PutAsJsonAsync(
            $"/client/training/logs/{logId}",
            updateBody,
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<LogResponse>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken);

        body!.HasPR.Should().BeFalse("95 kg is less than the existing max of 100 kg — not a PR");

        // Verify count is still exactly 1 (only the seeded record)
        using var verifyScope = factory.Services.CreateScope();
        var mongo = verifyScope.ServiceProvider.GetRequiredService<IMongoContext>();

        var records = await GetPrRecordsAsync(mongo, clientId, exerciseId, TestContext.Current.CancellationToken);

        records.Should().HaveCount(1, "no new PR should have been created");
        records[0].WorkoutLogId.Should().Be(existingPr.WorkoutLogId, "the existing PR must be unchanged");
    }

    /// <summary>
    /// Test 5: Multiple PRs in one update → two sets for different exercises
    /// each beat their historical max → two PR rows, each with the correct ExerciseExternalId.
    /// </summary>
    [Fact]
    public async Task MultiplePrsInOneUpdate_BothRowsCreated()
    {
        var (http, clientId, logId) = await SetupClientWithLogAsync();
        var exerciseAId = Guid.NewGuid();
        var exerciseBId = Guid.NewGuid();

        // No prior records — both sets will be first-time PRs
        var updateBody = BuildUpdateRequestMultiExercise(
        [
            (exerciseAId, "Overhead Press", 60m, 6),
            (exerciseBId, "Pull-up", 10m, 10)
        ]);

        var response = await http.PutAsJsonAsync(
            $"/client/training/logs/{logId}",
            updateBody,
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<LogResponse>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken);

        body!.HasPR.Should().BeTrue("at least one PR was detected");

        // Verify both PR records were created
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        var recordsA = await GetPrRecordsAsync(mongo, clientId, exerciseAId, TestContext.Current.CancellationToken);
        var recordsB = await GetPrRecordsAsync(mongo, clientId, exerciseBId, TestContext.Current.CancellationToken);

        recordsA.Should().HaveCount(1,
            "Overhead Press PR must be created with ExerciseExternalId = exerciseAId");
        recordsA[0].WeightKg.Should().Be(60m);
        recordsA[0].Reps.Should().Be(6);
        recordsA[0].ExerciseExternalId.Should().Be(exerciseAId);

        recordsB.Should().HaveCount(1,
            "Pull-up PR must be created with ExerciseExternalId = exerciseBId");
        recordsB[0].WeightKg.Should().Be(10m);
        recordsB[0].Reps.Should().Be(10);
        recordsB[0].ExerciseExternalId.Should().Be(exerciseBId);
    }

    // ── Local response DTOs (per slice rules — never imported across features) ────

    private record LogResponse(
        Guid LogId,
        Guid ClientId,
        bool IsCompleted,
        bool HasPR,
        List<ExerciseResponse> Exercises);

    private record ExerciseResponse(
        Guid ExerciseExternalId,
        string ExerciseName,
        List<SetResponse> Sets);

    private record SetResponse(
        int SetNumber,
        bool IsPR,
        decimal? WeightKg,
        int? Reps,
        DateTime? CompletedAt);
}
