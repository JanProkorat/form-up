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
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;

namespace FormUp.Tests.Endpoints.SessionExecutions;

/// <summary>
/// Integration tests for GET /training/clients/{clientId}/progress/{exerciseId} — issue #529.
///
/// Root cause: GetExerciseProgressEndpoint filtered WorkoutLog.ClientId == req.ClientId
/// (a PublicId), but WorkoutLog.ClientId stores ApplicationUser.Id (UserId). The result
/// was always an empty data-points list.
///
/// These tests use real PostgreSQL + MongoDB (Testcontainers) to validate
/// that data points are returned only when the workout log is keyed on UserId —
/// not when it is keyed on PublicId (the wrong key). A mock-based test cannot
/// catch this because mocks ignore the filter value.
/// </summary>
[Collection(TestCollection.Name)]
public class GetExerciseProgressIntegrationTests(FitnessApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static string UniqueEmail(string tag) => $"{Guid.NewGuid():N}@exprogress-{tag}.com";

    // ── helpers ───────────────────────────────────────────────────────────────

    private async Task<(HttpClient Http, long ProfessionalProfileId)> SetupTrainerAsync()
    {
        var http = factory.CreateClient();
        var email = UniqueEmail("trainer");

        await TestHelpers.RegisterAsync(http, email, "TestPass1!", "Test", "Trainer", "Trainer");
        var (token, _) = await TestHelpers.LoginAsync(http, email, "TestPass1!");
        TestHelpers.SetBearerToken(http, token);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var user = await db.Users.FirstAsync(
            u => u.Email == email, TestContext.Current.CancellationToken);
        var profile = await db.ProfessionalProfiles.FirstAsync(
            p => p.UserId == user.Id, TestContext.Current.CancellationToken);

        return (http, profile.Id);
    }

    private async Task<(Guid ClientPublicId, long ClientProfileId, Guid ClientUserId)>
        SetupClientAsync()
    {
        var http = factory.CreateClient();
        var email = UniqueEmail("client");

        await TestHelpers.RegisterAsync(http, email, "TestPass1!", "Test", "Client", "Client");
        await TestHelpers.LoginAsync(http, email, "TestPass1!");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var user = await db.Users.FirstAsync(
            u => u.Email == email, TestContext.Current.CancellationToken);
        var profile = await db.ClientProfiles.FirstAsync(
            cp => cp.UserId == user.Id, TestContext.Current.CancellationToken);

        return (profile.PublicId, profile.Id, user.Id);
    }

    private async Task LinkTrainerToClientAsync(long trainerProfileId, long clientProfileId)
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
            CanViewTrainingPlans = true,
            CanViewNutritionPlans = true,
            DateCreated = DateTime.UtcNow,
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    // ── regression guard tests ────────────────────────────────────────────────

    /// <summary>
    /// Regression guard for #529: when a WorkoutLog is seeded with
    /// ClientId = clientProfile.UserId (the correct key), the endpoint returns data points.
    /// With the old buggy filter (ClientId == req.ClientId == PublicId), this would
    /// return zero data points.
    /// </summary>
    [Fact]
    public async Task ExerciseProgress_WorkoutLogSeededWithUserId_ReturnsDataPoints()
    {
        var (trainerHttp, trainerProfileId) = await SetupTrainerAsync();
        var (clientPublicId, clientProfileId, clientUserId) = await SetupClientAsync();
        await LinkTrainerToClientAsync(trainerProfileId, clientProfileId);

        var exerciseId = Guid.NewGuid();
        var exerciseName = "Deadlift";

        // Seed a completed SessionExecution with ClientId = UserId (the correct key).
        // #841: GetExerciseProgressEndpoint now reads mongo.SessionExecutions exclusively —
        // the retired WorkoutLogs collection is no longer consulted.
        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
            var startedAt = DateTime.UtcNow.AddDays(-3);
            await mongo.SessionExecutions.InsertOneAsync(new SessionExecution
            {
                Id = ObjectId.GenerateNewId(),
                ExternalId = Guid.NewGuid(),
                ClientId = clientUserId,    // ← MUST be UserId, not PublicId
                Date = SessionExecution.ToCompletionDateUtc(startedAt),
                Status = SessionExecutionStatus.Completed,
                Performance = new SessionExecutionPerformance
                {
                    StartedAt = startedAt,
                    CompletedAt = startedAt.AddHours(1),
                    Workouts =
                    [
                        new LoggedWorkout
                        {
                            WorkoutId = Guid.NewGuid(),
                            Name = "Main",
                            Exercises =
                            [
                                new WorkoutExercise
                                {
                                    ExerciseExternalId = exerciseId,
                                    ExerciseName = exerciseName,
                                    Sets =
                                    [
                                        new WorkoutSet
                                        {
                                            SetNumber = 1,
                                            WeightKg = 140m,
                                            Reps = 5,
                                        }
                                    ]
                                }
                            ]
                        }
                    ]
                },
                DateCreated = DateTime.UtcNow,
                Version = 1
            }, cancellationToken: TestContext.Current.CancellationToken);
        }

        var response = await trainerHttp.GetAsync(
            $"/training/clients/{clientPublicId}/progress/{exerciseId}",
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<ExerciseProgressResponse>(
            JsonOptions, cancellationToken: TestContext.Current.CancellationToken);

        body!.DataPoints.Should().HaveCountGreaterThanOrEqualTo(1,
            "the workout log seeded with ClientId = UserId must produce a data point");
        body.ExerciseName.Should().Be(exerciseName);
    }

    /// <summary>
    /// Regression guard for #529: a WorkoutLog seeded with ClientId = PublicId
    /// (the WRONG key, the old bug's side-effect) must NOT produce data points.
    /// This confirms the endpoint now correctly resolves UserId before filtering.
    /// </summary>
    [Fact]
    public async Task ExerciseProgress_WorkoutLogSeededWithPublicId_ReturnsNoDataPoints()
    {
        var (trainerHttp, trainerProfileId) = await SetupTrainerAsync();
        var (clientPublicId, clientProfileId, _) = await SetupClientAsync();
        await LinkTrainerToClientAsync(trainerProfileId, clientProfileId);

        var exerciseId = Guid.NewGuid();

        // Seed a completed SessionExecution with ClientId = PublicId (wrong key — pre-fix bug).
        // #841: GetExerciseProgressEndpoint now reads mongo.SessionExecutions exclusively.
        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
            var startedAt = DateTime.UtcNow.AddDays(-3);
            await mongo.SessionExecutions.InsertOneAsync(new SessionExecution
            {
                Id = ObjectId.GenerateNewId(),
                ExternalId = Guid.NewGuid(),
                ClientId = clientPublicId,   // ← WRONG: PublicId stored where UserId expected
                Date = SessionExecution.ToCompletionDateUtc(startedAt),
                Status = SessionExecutionStatus.Completed,
                Performance = new SessionExecutionPerformance
                {
                    StartedAt = startedAt,
                    CompletedAt = startedAt.AddHours(1),
                    Workouts =
                    [
                        new LoggedWorkout
                        {
                            WorkoutId = Guid.NewGuid(),
                            Name = "Main",
                            Exercises =
                            [
                                new WorkoutExercise
                                {
                                    ExerciseExternalId = exerciseId,
                                    ExerciseName = "Squat",
                                    Sets =
                                    [
                                        new WorkoutSet
                                        {
                                            SetNumber = 1,
                                            WeightKg = 100m,
                                            Reps = 5,
                                        }
                                    ]
                                }
                            ]
                        }
                    ]
                },
                DateCreated = DateTime.UtcNow,
                Version = 1
            }, cancellationToken: TestContext.Current.CancellationToken);
        }

        var response = await trainerHttp.GetAsync(
            $"/training/clients/{clientPublicId}/progress/{exerciseId}",
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<ExerciseProgressResponse>(
            JsonOptions, cancellationToken: TestContext.Current.CancellationToken);

        body!.DataPoints.Should().BeEmpty(
            "a workout log whose ClientId is PublicId (not UserId) must not be returned");
    }

    // ── DTOs for deserialization ──────────────────────────────────────────────

    private record ExerciseProgressResponse(string ExerciseName, List<DataPoint> DataPoints);

    private record DataPoint(
        DateTime Date,
        decimal? BestWeightKg,
        int? BestReps,
        decimal TotalVolume,
        bool HasPR);
}
