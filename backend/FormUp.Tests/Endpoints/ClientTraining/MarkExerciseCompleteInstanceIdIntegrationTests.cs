using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Endpoints.TrainingPlans;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace FormUp.Tests.Endpoints.ClientTraining;

/// <summary>
/// Testcontainers integration tests (real MongoDB) for the #857 phase-3b re-keying of
/// exercise completion onto the per-instance <see cref="SessionExercise.ExerciseId"/>.
/// </summary>
/// <remarks>
/// Both assertions here are deliberately made against the **raw BSON** document rather than the
/// typed <see cref="SessionExecution"/>. A typed read silently ignores an element that no longer
/// maps to a property, so a typed assertion cannot distinguish "the field is gone" from "the
/// field is present and unmapped" — which is exactly the failure mode being pinned. The mocked
/// unit tests in <see cref="MarkExerciseCompleteEndpointTests"/> prove the C# branching; only a
/// real Mongo instance can prove what is actually persisted.
/// </remarks>
[Collection(TestCollection.Shard2)]
public class MarkExerciseCompleteInstanceIdIntegrationTests(FitnessApiFactory factory)
{
    private static string UniqueEmail() => $"{Guid.NewGuid():N}@mark-exercise-instance-test.com";

    /// <summary>
    /// Monday of the current week (UTC), so a one-week plan's date window
    /// <c>[StartDate, StartDate + 7d)</c> always contains today — `PlanWindowResolver` in the
    /// endpoint rejects plans whose window does not, and a plan with a null StartDate is never
    /// resolvable at all.
    /// </summary>
    private static DateTime StartOfCurrentWeek()
    {
        var today = DateTime.UtcNow.Date;
        return today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
    }

    /// <summary>Today's ISO day-of-week (1 = Monday … 7 = Sunday).</summary>
    private static int TodayDow()
    {
        var dow = (int)DateTime.UtcNow.DayOfWeek;
        return dow == 0 ? 7 : dow;
    }

    /// <summary>
    /// Registers a client, logs in, authorises <paramref name="httpClient"/>, and returns the
    /// client's <c>ApplicationUser.Id</c> — the canonical Mongo <c>ClientId</c> since #840.
    /// </summary>
    private async Task<Guid> RegisterClientAsync(HttpClient httpClient)
    {
        var email = UniqueEmail();
        await TestHelpers.RegisterAsync(httpClient, email, "TestPass1!", "Instance", "Client", "Client");
        var (accessToken, _) = await TestHelpers.LoginAsync(httpClient, email, "TestPass1!");
        TestHelpers.SetBearerToken(httpClient, accessToken);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await db.Users.FirstAsync(u => u.Email == email, TestContext.Current.CancellationToken);
        var profile = await db.ClientProfiles.FirstAsync(cp => cp.UserId == user.Id, TestContext.Current.CancellationToken);
        return profile.UserId;
    }

    /// <summary>
    /// Inserts an Active, published one-week plan whose single session sits on today, so the
    /// endpoint's plan-window and session lookups both succeed.
    /// </summary>
    private async Task InsertPlanAsync(Guid clientId, Guid planId, TrainingSession session)
    {
        var plan = new TrainingPlan
        {
            ExternalId = planId,
            ClientId = clientId,
            TrainerId = Guid.NewGuid(),
            Name = "Instance-id completion plan",
            Status = TrainingPlanStatus.Active,
            Version = 1,
            DateCreated = DateTime.UtcNow.AddDays(-2),
            StartDate = StartOfCurrentWeek(),
            Weeks =
            [
                new TrainingWeek
                {
                    WeekNumber = 1,
                    Status = WeekStatus.Published,
                    DatePublished = StartOfCurrentWeek(),
                    Days = TrainingPlanTestHelpers.MaterializeDays((TodayDow(), session))
                }
            ]
        };

        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        await mongo.TrainingPlans.InsertOneAsync(plan, cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Reads the persisted execution document as raw BSON, bypassing the typed mapping.
    /// </summary>
    private async Task<BsonDocument> ReadRawExecutionAsync(Guid clientId, Guid sessionId)
    {
        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<IMongoDatabase>();
        var raw = database.GetCollection<BsonDocument>(MongoCollections.SessionExecutions);

        var filter = Builders<BsonDocument>.Filter.Eq("clientId", new BsonBinaryData(clientId, GuidRepresentation.Standard))
                     & Builders<BsonDocument>.Filter.Eq("sessionId", new BsonBinaryData(sessionId, GuidRepresentation.Standard));

        using var cursor = await raw.FindAsync(filter, cancellationToken: TestContext.Current.CancellationToken);
        var documents = await cursor.ToListAsync(TestContext.Current.CancellationToken);

        documents.Should().HaveCount(1, "marking an exercise complete must create exactly one execution document for (clientId, date, sessionId)");
        return documents[0];
    }

    /// <summary>
    /// AC 6: no code path may write the retired <c>completedExerciseIdsBySection</c> shape. The
    /// three #837 boot backfills that used to emit it are deleted, but the endpoint itself is the
    /// live path that would reintroduce it, so the persisted document is what gets asserted.
    /// </summary>
    [Fact]
    public async Task MarkExerciseComplete_PersistsOnlyTheInstanceIdShape_NeverCompletedExerciseIdsBySection()
    {
        var httpClient = factory.CreateClient();
        var clientId = await RegisterClientAsync(httpClient);

        var sessionId = Guid.NewGuid();
        var instanceId = Guid.NewGuid();

        await InsertPlanAsync(clientId, Guid.NewGuid(), new TrainingSession
        {
            SessionId = sessionId,
            Name = "Retired-shape guard",
            Order = 1,
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
                            ExerciseId = instanceId,
                            ExerciseExternalId = Guid.NewGuid(),
                            ExerciseName = "Squat",
                            Order = 1,
                            Sets = [new ExerciseSet { SetNumber = 1, Type = SetType.Normal, Reps = 5 }]
                        }
                    ]
                }
            ]
        });

        var response = await httpClient.PostAsJsonAsync(
            $"/client/training/sessions/{sessionId}/exercises/{instanceId}/complete",
            new { },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var raw = await ReadRawExecutionAsync(clientId, sessionId);

        raw.Contains("completedExerciseIdsBySection").Should().BeFalse(
            "the by-workout dictionary shape is retired — a typed read would ignore it silently, so it can only be caught in raw BSON");
        raw.Contains("completedExerciseIds").Should().BeFalse(
            "the flat pre-857 array is retired alongside the dictionary");

        raw.Contains("completedExerciseInstanceIds").Should().BeTrue(
            "completion is keyed on SessionExercise.ExerciseId after #857 phase 3b");
        raw["completedExerciseInstanceIds"].AsBsonArray
            .Select(id => id.AsGuid)
            .Should().BeEquivalentTo([instanceId]);
    }

    /// <summary>
    /// AC 8: the duplicate case standalone exercises newly make reachable — the same catalog
    /// exercise appearing BOTH standalone on a session and nested inside one of that session's
    /// workouts. Pre-857 this pairing could not exist, and the retired
    /// (WorkoutId, ExerciseExternalId) key could not have distinguished the two placements;
    /// the per-instance id must.
    /// </summary>
    [Fact]
    public async Task MarkExerciseComplete_SameCatalogExerciseStandaloneAndNested_CompletesOnlyTheTargetedInstance()
    {
        var httpClient = factory.CreateClient();
        var clientId = await RegisterClientAsync(httpClient);

        var sessionId = Guid.NewGuid();
        var sharedCatalogId = Guid.NewGuid();
        var standaloneInstanceId = Guid.NewGuid();
        var nestedInstanceId = Guid.NewGuid();

        await InsertPlanAsync(clientId, Guid.NewGuid(), new TrainingSession
        {
            SessionId = sessionId,
            Name = "Standalone + nested duplicate",
            Order = 1,
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
                            ExerciseId = nestedInstanceId,
                            ExerciseExternalId = sharedCatalogId,
                            ExerciseName = "Wall Ball",
                            Order = 0,
                            Sets = [new ExerciseSet { SetNumber = 1, Type = SetType.Normal, Reps = 10 }]
                        }
                    ]
                }
            ],
            StandaloneExercises =
            [
                new SessionExercise
                {
                    ExerciseId = standaloneInstanceId,
                    ExerciseExternalId = sharedCatalogId,
                    ExerciseName = "Wall Ball",
                    Order = 1,
                    Sets = [new ExerciseSet { SetNumber = 1, Type = SetType.Normal, Reps = 10 }]
                }
            ]
        });

        // Target the STANDALONE placement only.
        var response = await httpClient.PostAsJsonAsync(
            $"/client/training/sessions/{sessionId}/exercises/{standaloneInstanceId}/complete",
            new { },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var raw = await ReadRawExecutionAsync(clientId, sessionId);
        var completed = raw["completedExerciseInstanceIds"].AsBsonArray.Select(id => id.AsGuid).ToList();

        completed.Should().BeEquivalentTo([standaloneInstanceId],
            "only the placement addressed by ExerciseId may be completed");
        completed.Should().NotContain(nestedInstanceId,
            "the nested placement shares the same catalog exercise, and pre-857 keying would have completed both");
    }
}
