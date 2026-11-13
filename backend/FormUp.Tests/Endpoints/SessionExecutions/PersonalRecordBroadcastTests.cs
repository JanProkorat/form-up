using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.SessionExecutions;

/// <summary>
/// Integration tests verifying the <c>personalrecordachieved</c> SignalR broadcast
/// wired into <c>PUT /client/training/logs/{LogId}</c>.
///
/// Uses real PostgreSQL (for trainer-link lookups) and real MongoDB (for PR detection)
/// via Testcontainers. The <see cref="IRealtimeNotifier"/> is replaced with
/// <see cref="FakeRealtimeNotifier"/> in <see cref="FitnessApiFactory"/>.
/// </summary>
[Collection(TestCollection.Name)]
public class PersonalRecordBroadcastTests(FitnessApiFactory factory)
{
    private static string UniqueEmail() => $"{Guid.NewGuid():N}@pr-broadcast-test.com";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    // ── helpers ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Registers and logs in a client, returns Http client (with token), UserId, and a started log id.
    /// </summary>
    private async Task<(HttpClient Http, Guid UserId, Guid LogId)> SetupClientWithLogAsync()
    {
        var http = factory.CreateClient();
        var email = UniqueEmail();

        await TestHelpers.RegisterAsync(http, email, "TestPass1!", "PR", "BroadcastClient", "Client");
        var (token, _) = await TestHelpers.LoginAsync(http, email, "TestPass1!");
        TestHelpers.SetBearerToken(http, token);

        Guid userId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await db.Users.FirstAsync(
                u => u.Email == email,
                TestContext.Current.CancellationToken);
            userId = user.Id;
        }

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

    /// <summary>
    /// Registers a trainer and returns their UserId and ClientProfile/ProfessionalProfile IDs.
    /// </summary>
    private async Task<(Guid TrainerUserId, long ProfessionalProfileId)> SetupTrainerAsync()
    {
        var http = factory.CreateClient();
        var email = UniqueEmail();

        await TestHelpers.RegisterAsync(http, email, "TestPass1!", "PR", "BroadcastTrainer", "Trainer");

        Guid trainerUserId;
        long professionalProfileId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await db.Users.FirstAsync(
                u => u.Email == email,
                TestContext.Current.CancellationToken);
            trainerUserId = user.Id;

            var profile = await db.ProfessionalProfiles.FirstAsync(
                p => p.UserId == trainerUserId,
                TestContext.Current.CancellationToken);
            professionalProfileId = profile.Id;
        }

        return (trainerUserId, professionalProfileId);
    }

    /// <summary>
    /// Creates an active <see cref="ClientProfessionalLink"/> between an existing client and trainer
    /// by seeding directly into Postgres.
    /// </summary>
    private async Task LinkTrainerToClientAsync(
        Guid clientUserId,
        long professionalProfileId,
        bool isActive = true,
        bool canViewTrainingPlans = true)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var clientProfile = await db.ClientProfiles.FirstAsync(
            p => p.UserId == clientUserId,
            TestContext.Current.CancellationToken);

        db.ClientProfessionalLinks.Add(new ClientProfessionalLink
        {
            ClientProfileId = clientProfile.Id,
            ProfessionalProfileId = professionalProfileId,
            ProfessionalRole = UserRole.Trainer,
            ConnectionState = isActive ? ConnectionState.Connected : ConnectionState.Ended,
            CanViewTrainingPlans = canViewTrainingPlans,
            CanViewNutritionPlans = !canViewTrainingPlans,
            DateCreated = DateTime.UtcNow
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
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

    private FakeRealtimeNotifier GetNotifier()
    {
        using var scope = factory.Services.CreateScope();
        // FakeRealtimeNotifier is a singleton — resolve it once and return the singleton.
        return factory.Services.GetRequiredService<FakeRealtimeNotifier>();
    }

    // ── test cases ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Test 1: First PR with one linked trainer → exactly 2 events: one to client, one to trainer.
    /// </summary>
    [Fact]
    public async Task FirstPR_WithLinkedTrainer_BroadcastsToClientAndTrainer()
    {
        var notifier = GetNotifier();
        notifier.Reset();

        var (http, clientId, logId) = await SetupClientWithLogAsync();
        var (trainerUserId, profProfileId) = await SetupTrainerAsync();
        await LinkTrainerToClientAsync(clientId, profProfileId);

        var exerciseId = Guid.NewGuid();
        var response = await http.PutAsJsonAsync(
            $"/client/training/logs/{logId}",
            BuildUpdateRequest(exerciseId, "Squat", weight: 100m, reps: 5),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var calls = notifier.Calls
            .Where(c => c.EventType == "personalrecordachieved")
            .ToList();

        calls.Should().HaveCount(2,
            "one event to the client and one to the linked trainer");

        calls.Should().Contain(c => c.UserId == clientId,
            "client must receive the personalrecordachieved event");

        calls.Should().Contain(c => c.UserId == trainerUserId,
            "trainer must receive the personalrecordachieved event");
    }

    /// <summary>
    /// Test 2: Idempotent re-submit — no re-broadcast on the second call.
    /// </summary>
    [Fact]
    public async Task IdempotentReSubmit_NoAdditionalBroadcast()
    {
        var notifier = GetNotifier();
        notifier.Reset();

        var (http, clientId, logId) = await SetupClientWithLogAsync();
        var (_, profProfileId) = await SetupTrainerAsync();
        await LinkTrainerToClientAsync(clientId, profProfileId);

        var exerciseId = Guid.NewGuid();
        var body = BuildUpdateRequest(exerciseId, "Bench Press", weight: 80m, reps: 8);

        // First call — PR detected, 2 events fired
        var first = await http.PutAsJsonAsync(
            $"/client/training/logs/{logId}",
            body,
            TestContext.Current.CancellationToken);
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var countAfterFirst = notifier.Calls.Count(c => c.EventType == "personalrecordachieved");
        countAfterFirst.Should().Be(2, "first call: client + trainer");

        notifier.Reset();

        // Second call with identical payload — idempotent, no new PR, no new broadcast
        var second = await http.PutAsJsonAsync(
            $"/client/training/logs/{logId}",
            body,
            TestContext.Current.CancellationToken);
        second.StatusCode.Should().Be(HttpStatusCode.OK);

        var countAfterSecond = notifier.Calls.Count(c => c.EventType == "personalrecordachieved");
        countAfterSecond.Should().Be(0,
            "re-submitting the same sets must not fire additional PR broadcasts");
    }

    /// <summary>
    /// Test 3: Two exercises each at new max → 4 broadcasts (2 PRs × 2 audience members).
    /// </summary>
    [Fact]
    public async Task MultiplePRsInOneUpdate_BroadcastsOncePerPrPerAudienceMember()
    {
        var notifier = GetNotifier();
        notifier.Reset();

        var (http, clientId, logId) = await SetupClientWithLogAsync();
        var (trainerUserId, profProfileId) = await SetupTrainerAsync();
        await LinkTrainerToClientAsync(clientId, profProfileId);

        var exerciseAId = Guid.NewGuid();
        var exerciseBId = Guid.NewGuid();

        var body = new
        {
            Exercises = new[]
            {
                new
                {
                    ExerciseExternalId = exerciseAId,
                    ExerciseName = "Overhead Press",
                    Sets = new[]
                    {
                        new { SetNumber = 1, Reps = 6, WeightKg = 60m, CompletedAt = DateTime.UtcNow }
                    }
                },
                new
                {
                    ExerciseExternalId = exerciseBId,
                    ExerciseName = "Pull-up",
                    Sets = new[]
                    {
                        new { SetNumber = 1, Reps = 10, WeightKg = 10m, CompletedAt = DateTime.UtcNow }
                    }
                }
            }
        };

        var response = await http.PutAsJsonAsync(
            $"/client/training/logs/{logId}",
            body,
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var calls = notifier.Calls
            .Where(c => c.EventType == "personalrecordachieved")
            .ToList();

        calls.Should().HaveCount(4,
            "2 PRs × 2 audience members (client + trainer) = 4 events");

        // Both client and trainer receive two events each
        calls.Count(c => c.UserId == clientId).Should().Be(2,
            "client receives one event per PR");
        calls.Count(c => c.UserId == trainerUserId).Should().Be(2,
            "trainer receives one event per PR");
    }

    /// <summary>
    /// Test 4: No linked trainer → only the client receives the event (1 event total).
    /// </summary>
    [Fact]
    public async Task NoProfessionalLinked_OnlyClientReceivesBroadcast()
    {
        var notifier = GetNotifier();
        notifier.Reset();

        var (http, clientId, logId) = await SetupClientWithLogAsync();
        // Deliberately do NOT link any trainer

        var exerciseId = Guid.NewGuid();
        var response = await http.PutAsJsonAsync(
            $"/client/training/logs/{logId}",
            BuildUpdateRequest(exerciseId, "Deadlift", weight: 120m, reps: 3),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var calls = notifier.Calls
            .Where(c => c.EventType == "personalrecordachieved")
            .ToList();

        calls.Should().HaveCount(1,
            "with no trainer linked, only the client receives the event");

        calls[0].UserId.Should().Be(clientId,
            "the single event must be addressed to the client");
    }

    /// <summary>
    /// F6 (claude-security review): a link that grants no training capability must not receive
    /// personal-record data over SignalR — this is training-domain data, and the REST route
    /// serving the same collection (GetClientTimeline) is gated on CanViewTrainingPlans.
    /// </summary>
    [Fact]
    public async Task NutritionOnlyLink_DoesNotReceiveBroadcast()
    {
        var notifier = GetNotifier();
        notifier.Reset();

        var (http, clientId, logId) = await SetupClientWithLogAsync();
        var (trainerUserId, profProfileId) = await SetupTrainerAsync();
        await LinkTrainerToClientAsync(clientId, profProfileId, canViewTrainingPlans: false);

        var exerciseId = Guid.NewGuid();
        var response = await http.PutAsJsonAsync(
            $"/client/training/logs/{logId}",
            BuildUpdateRequest(exerciseId, "Front Squat", weight: 70m, reps: 5),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var calls = notifier.Calls
            .Where(c => c.EventType == "personalrecordachieved")
            .ToList();

        calls.Should().HaveCount(1,
            "a link with no training capability must not receive the client's PR data");
        calls.Should().NotContain(c => c.UserId == trainerUserId,
            "the nutrition-only professional must not be broadcast to");
        calls[0].UserId.Should().Be(clientId,
            "the single remaining event is the client's own");
    }

    /// <summary>
    /// F6 (claude-security review): a professional whose collaboration ended (link deactivated)
    /// must stop receiving the client's PR data, even though they authored no plan-authorship
    /// state that would otherwise keep this channel open.
    /// </summary>
    [Fact]
    public async Task RevokedLink_DoesNotReceiveBroadcast()
    {
        var notifier = GetNotifier();
        notifier.Reset();

        var (http, clientId, logId) = await SetupClientWithLogAsync();
        var (trainerUserId, profProfileId) = await SetupTrainerAsync();
        await LinkTrainerToClientAsync(clientId, profProfileId, isActive: false);

        var exerciseId = Guid.NewGuid();
        var response = await http.PutAsJsonAsync(
            $"/client/training/logs/{logId}",
            BuildUpdateRequest(exerciseId, "Incline Bench", weight: 65m, reps: 6),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var calls = notifier.Calls
            .Where(c => c.EventType == "personalrecordachieved")
            .ToList();

        calls.Should().HaveCount(1,
            "a revoked link must not receive the client's PR data");
        calls.Should().NotContain(c => c.UserId == trainerUserId,
            "the ex-professional must not be broadcast to");
        calls[0].UserId.Should().Be(clientId,
            "the single remaining event is the client's own");
    }

    /// <summary>
    /// Test 5: Two trainers linked → 3 events per PR (1 client + 2 trainers).
    /// </summary>
    [Fact]
    public async Task TwoTrainersLinked_AllThreeAudienceMembersReceiveEvent()
    {
        var notifier = GetNotifier();
        notifier.Reset();

        var (http, clientId, logId) = await SetupClientWithLogAsync();
        var (trainer1UserId, profProfileId1) = await SetupTrainerAsync();
        var (trainer2UserId, profProfileId2) = await SetupTrainerAsync();
        await LinkTrainerToClientAsync(clientId, profProfileId1);
        await LinkTrainerToClientAsync(clientId, profProfileId2);

        var exerciseId = Guid.NewGuid();
        var response = await http.PutAsJsonAsync(
            $"/client/training/logs/{logId}",
            BuildUpdateRequest(exerciseId, "Romanian Deadlift", weight: 90m, reps: 8),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var calls = notifier.Calls
            .Where(c => c.EventType == "personalrecordachieved")
            .ToList();

        calls.Should().HaveCount(3,
            "1 PR × 3 audience members (client + 2 trainers) = 3 events");

        calls.Should().Contain(c => c.UserId == clientId,
            "client must receive the event");
        calls.Should().Contain(c => c.UserId == trainer1UserId,
            "trainer 1 must receive the event");
        calls.Should().Contain(c => c.UserId == trainer2UserId,
            "trainer 2 must receive the event");
    }

    // ── local response DTOs ───────────────────────────────────────────────────────

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
