using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;

namespace FormUp.Tests.Endpoints.Client.PersonalRecords;

/// <summary>
/// Integration tests for GET /client/records.
/// All tests use real MongoDB via Testcontainers so that filter semantics and
/// sort ordering are validated against actual driver behavior.
/// </summary>
[Collection(TestCollection.Shard3)]
public class GetClientRecordsEndpointTests(FitnessApiFactory factory)
{
    private static string UniqueEmail() => $"{Guid.NewGuid():N}@pr-records-test.com";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    // ── shared setup helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Registers a new client, logs in, resolves their user.Id (= the ClientId used
    /// in PersonalRecord.ClientId) and returns the authenticated HttpClient and ClientId.
    /// </summary>
    private async Task<(HttpClient Http, Guid ClientId)> SetupClientAsync()
    {
        var http = factory.CreateClient();
        var email = UniqueEmail();

        await TestHelpers.RegisterAsync(http, email, "TestPass1!", "Records", "Tester", "Client");
        var (token, _) = await TestHelpers.LoginAsync(http, email, "TestPass1!");
        TestHelpers.SetBearerToken(http, token);

        // PersonalRecord.ClientId is set to user.Id (not ClientProfile.PublicId),
        // matching the exact pattern used by UpdateWorkoutEndpoint (issue #11).
        Guid userId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await db.Users.FirstAsync(
                u => u.Email == email,
                TestContext.Current.CancellationToken);
            userId = user.Id;
        }

        return (http, userId);
    }

    /// <summary>
    /// Inserts a PersonalRecord document directly into MongoDB for the given client.
    /// </summary>
    private static async Task InsertPersonalRecordAsync(
        IMongoContext mongo,
        Guid clientId,
        Guid exerciseExternalId,
        string exerciseName,
        decimal weightKg,
        int reps,
        DateTime achievedAt)
    {
        var pr = new PersonalRecord
        {
            Id = ObjectId.GenerateNewId(),
            ExternalId = Guid.NewGuid(),
            ClientId = clientId,
            ExerciseExternalId = exerciseExternalId,
            ExerciseName = exerciseName,
            WeightKg = weightKg,
            Reps = reps,
            AchievedAt = achievedAt,
            WorkoutLogId = Guid.NewGuid(),
            SetNumber = 1,
            Version = 1,
            DateCreated = DateTime.UtcNow
        };

        await mongo.PersonalRecords.InsertOneAsync(
            pr, cancellationToken: TestContext.Current.CancellationToken);
    }

    // ── test cases ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Test 1: Client with no PRs → empty items list, X-Total-Count: 0.
    /// </summary>
    [Fact]
    public async Task GetRecords_NoPrsForClient_ReturnsEmptyWithZeroCount()
    {
        var (http, _) = await SetupClientAsync();

        var response = await http.GetAsync(
            "/client/records",
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var totalCountHeader = response.Headers.GetValues("X-Total-Count").FirstOrDefault();
        totalCountHeader.Should().Be("0", "no PRs exist for this client");

        var body = await response.Content.ReadFromJsonAsync<RecordsResponse>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken);

        body!.Items.Should().BeEmpty("no PRs exist for this client");
    }

    /// <summary>
    /// Test 2: Three PRs with distinct AchievedAt → returned newest first.
    /// </summary>
    [Fact]
    public async Task GetRecords_MultiplePrs_ReturnedNewestFirst()
    {
        var (http, clientId) = await SetupClientAsync();
        var exerciseId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        // Insert in reverse order to confirm sort is applied, not insertion order
        var oldest = now.AddDays(-3);
        var middle = now.AddDays(-2);
        var newest = now.AddDays(-1);

        await InsertPersonalRecordAsync(mongo, clientId, exerciseId, "Squat", 80m, 5, oldest);
        await InsertPersonalRecordAsync(mongo, clientId, exerciseId, "Squat", 90m, 5, newest);
        await InsertPersonalRecordAsync(mongo, clientId, exerciseId, "Squat", 85m, 5, middle);

        var response = await http.GetAsync(
            "/client/records",
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var totalCountHeader = response.Headers.GetValues("X-Total-Count").FirstOrDefault();
        totalCountHeader.Should().Be("3");

        var body = await response.Content.ReadFromJsonAsync<RecordsResponse>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken);

        body!.Items.Should().HaveCount(3);
        body.Items[0].WeightKg.Should().Be(90m, "newest PR (90 kg) should come first");
        body.Items[1].WeightKg.Should().Be(85m, "middle PR (85 kg) should come second");
        body.Items[2].WeightKg.Should().Be(80m, "oldest PR (80 kg) should come last");
    }

    /// <summary>
    /// Test 3: Two PRs with the same AchievedAt → deterministic ordering by _id (ASC).
    /// </summary>
    [Fact]
    public async Task GetRecords_TiedAchievedAt_StableOrderByObjectId()
    {
        var (http, clientId) = await SetupClientAsync();
        var exerciseId = Guid.NewGuid();
        var tiedTime = DateTime.UtcNow.AddMinutes(-5);

        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        // Insert first PR — gets an earlier ObjectId
        var firstId = ObjectId.GenerateNewId();
        await mongo.PersonalRecords.InsertOneAsync(new PersonalRecord
        {
            Id = firstId,
            ExternalId = Guid.NewGuid(),
            ClientId = clientId,
            ExerciseExternalId = exerciseId,
            ExerciseName = "Bench Press",
            WeightKg = 100m,
            Reps = 5,
            AchievedAt = tiedTime,
            WorkoutLogId = Guid.NewGuid(),
            SetNumber = 1,
            Version = 1,
            DateCreated = DateTime.UtcNow
        }, cancellationToken: TestContext.Current.CancellationToken);

        // Insert second PR with same AchievedAt — gets a later ObjectId
        var secondId = ObjectId.GenerateNewId();
        await mongo.PersonalRecords.InsertOneAsync(new PersonalRecord
        {
            Id = secondId,
            ExternalId = Guid.NewGuid(),
            ClientId = clientId,
            ExerciseExternalId = exerciseId,
            ExerciseName = "Bench Press",
            WeightKg = 100m,
            Reps = 8,
            AchievedAt = tiedTime,
            WorkoutLogId = Guid.NewGuid(),
            SetNumber = 2,
            Version = 1,
            DateCreated = DateTime.UtcNow
        }, cancellationToken: TestContext.Current.CancellationToken);

        var response = await http.GetAsync(
            "/client/records",
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<RecordsResponse>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken);

        body!.Items.Should().HaveCount(2);

        // _id ASC tiebreaker: the document with the earlier ObjectId (smaller _id) appears first
        body.Items[0].Reps.Should().Be(5, "first inserted (_id ASC) should be first within same AchievedAt");
        body.Items[1].Reps.Should().Be(8, "second inserted (_id ASC) should be second within same AchievedAt");
    }

    /// <summary>
    /// Test 4: Pagination — 5 PRs; page=1 pageSize=2 returns 2 newest with X-Total-Count=5;
    /// page=2 returns the next 2; page=3 returns 1 remaining.
    /// </summary>
    [Fact]
    public async Task GetRecords_Pagination_ReturnsCorrectPages()
    {
        var (http, clientId) = await SetupClientAsync();
        var exerciseId = Guid.NewGuid();
        var baseTime = DateTime.UtcNow;

        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        // Insert 5 PRs with distinct times (newest = day -1, oldest = day -5)
        for (var i = 1; i <= 5; i++)
        {
            await InsertPersonalRecordAsync(
                mongo, clientId, exerciseId, "Deadlift",
                weightKg: 100m + i,   // 101–105 kg, maps to days -1...-5 reverse
                reps: i,
                achievedAt: baseTime.AddDays(-i));
        }

        // page=1
        var page1Response = await http.GetAsync(
            "/client/records?page=1&pageSize=2",
            TestContext.Current.CancellationToken);

        page1Response.StatusCode.Should().Be(HttpStatusCode.OK);
        page1Response.Headers.GetValues("X-Total-Count").FirstOrDefault().Should().Be("5");

        var page1Body = await page1Response.Content.ReadFromJsonAsync<RecordsResponse>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken);

        page1Body!.Items.Should().HaveCount(2, "page 1 of 2 should have 2 items");
        // Newest first: day-1 = 102 kg × 1, day-2 = 103 kg × 2
        page1Body.Items[0].WeightKg.Should().Be(101m, "newest PR (day -1, 101 kg) is first");
        page1Body.Items[1].WeightKg.Should().Be(102m, "second newest (day -2, 102 kg) is second");

        // page=2
        var page2Response = await http.GetAsync(
            "/client/records?page=2&pageSize=2",
            TestContext.Current.CancellationToken);

        page2Response.StatusCode.Should().Be(HttpStatusCode.OK);
        page2Response.Headers.GetValues("X-Total-Count").FirstOrDefault().Should().Be("5");

        var page2Body = await page2Response.Content.ReadFromJsonAsync<RecordsResponse>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken);

        page2Body!.Items.Should().HaveCount(2, "page 2 of 2 should have 2 items");
        page2Body.Items[0].WeightKg.Should().Be(103m);
        page2Body.Items[1].WeightKg.Should().Be(104m);

        // page=3
        var page3Response = await http.GetAsync(
            "/client/records?page=3&pageSize=2",
            TestContext.Current.CancellationToken);

        page3Response.StatusCode.Should().Be(HttpStatusCode.OK);

        var page3Body = await page3Response.Content.ReadFromJsonAsync<RecordsResponse>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken);

        page3Body!.Items.Should().HaveCount(1, "page 3 should have only 1 remaining item");
        page3Body.Items[0].WeightKg.Should().Be(105m, "oldest PR (day -5, 105 kg) is the last remaining");
    }

    /// <summary>
    /// Test 5: Exercise filter — 3 PRs on Exercise A, 2 on Exercise B;
    /// filter by Exercise B → 2 items, X-Total-Count: 2.
    /// </summary>
    [Fact]
    public async Task GetRecords_ExerciseFilter_ReturnsOnlyMatchingExercise()
    {
        var (http, clientId) = await SetupClientAsync();
        var exerciseA = Guid.NewGuid();
        var exerciseB = Guid.NewGuid();
        var baseTime = DateTime.UtcNow;

        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        await InsertPersonalRecordAsync(mongo, clientId, exerciseA, "Squat", 100m, 5, baseTime.AddDays(-3));
        await InsertPersonalRecordAsync(mongo, clientId, exerciseA, "Squat", 105m, 5, baseTime.AddDays(-2));
        await InsertPersonalRecordAsync(mongo, clientId, exerciseA, "Squat", 110m, 5, baseTime.AddDays(-1));
        await InsertPersonalRecordAsync(mongo, clientId, exerciseB, "Bench Press", 80m, 8, baseTime.AddDays(-4));
        await InsertPersonalRecordAsync(mongo, clientId, exerciseB, "Bench Press", 85m, 8, baseTime.AddDays(-2));

        var response = await http.GetAsync(
            $"/client/records?exerciseExternalId={exerciseB}",
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var totalCountHeader = response.Headers.GetValues("X-Total-Count").FirstOrDefault();
        totalCountHeader.Should().Be("2", "only 2 PRs exist for Exercise B");

        var body = await response.Content.ReadFromJsonAsync<RecordsResponse>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken);

        body!.Items.Should().HaveCount(2);
        body.Items.Should().AllSatisfy(
            item => item.ExerciseExternalId.Should().Be(exerciseB),
            "filter should return only Exercise B records");

        // Newest first within Exercise B
        body.Items[0].WeightKg.Should().Be(85m, "most recent Exercise B PR (85 kg) first");
        body.Items[1].WeightKg.Should().Be(80m, "older Exercise B PR (80 kg) second");
    }

    /// <summary>
    /// Test 6: Isolation — a PR owned by a different client is NOT returned.
    /// </summary>
    [Fact]
    public async Task GetRecords_OtherClientsRecords_AreNotReturned()
    {
        var (http, clientId) = await SetupClientAsync();
        var (_, otherClientId) = await SetupClientAsync();

        var exerciseId = Guid.NewGuid();
        var baseTime = DateTime.UtcNow;

        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        // Insert one PR for the authenticated client and one for the other client
        await InsertPersonalRecordAsync(mongo, clientId, exerciseId, "Overhead Press", 60m, 5, baseTime.AddDays(-1));
        await InsertPersonalRecordAsync(mongo, otherClientId, exerciseId, "Overhead Press", 70m, 5, baseTime.AddDays(-1));

        var response = await http.GetAsync(
            "/client/records",
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var totalCountHeader = response.Headers.GetValues("X-Total-Count").FirstOrDefault();
        totalCountHeader.Should().Be("1", "only the authenticated client's PR should be counted");

        var body = await response.Content.ReadFromJsonAsync<RecordsResponse>(
            JsonOptions,
            cancellationToken: TestContext.Current.CancellationToken);

        body!.Items.Should().HaveCount(1);
        body.Items[0].WeightKg.Should().Be(60m, "only the authenticated client's PR should be returned");
    }

    // ── local response DTOs (per slice rules — never imported across features) ────

    private record RecordsResponse(List<RecordItem> Items);

    private record RecordItem(
        Guid ExternalId,
        Guid ExerciseExternalId,
        string ExerciseName,
        decimal WeightKg,
        int Reps,
        DateTime AchievedAt,
        Guid WorkoutLogId);
}
