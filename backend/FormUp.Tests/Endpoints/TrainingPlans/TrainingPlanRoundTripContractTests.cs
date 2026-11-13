using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.TrainingPlans.UpdateTrainingPlan;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace FormUp.Tests.Endpoints.TrainingPlans;

/// <summary>
/// Testcontainers integration tests (real MongoDB + PostgreSQL) proving issue #874: the session
/// shape's field names mean the same thing on read and write, so a client that GETs a plan,
/// changes nothing, and PUTs the response back cannot corrupt it. Before this fix, the wire name
/// <c>exercises</c> meant the flat standalone+nested union on read but standalone-only on write —
/// so a naive round trip silently promoted every nested workout exercise into the standalone
/// list, compounding on every save.
/// </summary>
/// <remarks>
/// The read shape is <c>weeks[].days[].sessions[]</c> (with the day-level note); the write shape
/// is a flat <c>weeks[].sessions[]</c> plus a week-level <c>dayNotes</c> map. "PUT the response
/// back unmodified" therefore cannot mean byte-identical bodies — <see cref="BuildUpdateRequestFromReadResponse"/>
/// maps read to write MECHANICALLY BY FIELD NAME ONLY (<c>workouts</c>-&gt;<c>workouts</c>,
/// <c>standaloneExercises</c>-&gt;<c>standaloneExercises</c>, <c>day.dayOfWeek</c>-&gt;<c>session.dayOfWeek</c>,
/// <c>day.note</c>-&gt;<c>week.dayNotes[dayOfWeek]</c>), leaning on <c>System.Text.Json</c>'s own
/// name-based binding rather than any domain knowledge of which list means what — a hand-picked
/// field mapping would pass even with the original defect present.
/// </remarks>
[Collection(TestCollection.Name)]
public class TrainingPlanRoundTripContractTests(FitnessApiFactory factory)
{
    private static string UniqueEmail() => $"{Guid.NewGuid():N}@round-trip-contract-test.com";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    // ── shared plan-building helper ──────────────────────────────────────────────

    /// <summary>
    /// Seeds a plan whose single session carries BOTH a workout with one nested exercise AND one
    /// standalone exercise — the exact shape that exposed the original defect (a round trip
    /// promoting the nested exercise into the standalone list).
    /// </summary>
    private async Task<(TrainingPlan Plan, Guid SessionId, Guid WorkoutId, Guid NestedExerciseExternalId, Guid StandaloneExerciseExternalId)>
        SeedMixedSessionPlanAsync(
            Guid trainerUserId, Guid? clientUserId = null, DateTime? startDate = null,
            int dayOfWeek = 1, bool published = true)
    {
        var planId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var workoutId = Guid.NewGuid();
        var nestedExerciseExternalId = Guid.NewGuid();
        var standaloneExerciseExternalId = Guid.NewGuid();

        var session = new TrainingSession
        {
            SessionId = sessionId,
            Name = "Push Day",
            Order = 1,
            Notes = "Focus on form",
            Workouts =
            [
                new TrainingWorkout
                {
                    WorkoutId = workoutId,
                    Order = 0,
                    Name = "Hlavní",
                    Exercises =
                    [
                        new SessionExercise
                        {
                            ExerciseId = Guid.NewGuid(),
                            ExerciseExternalId = nestedExerciseExternalId,
                            ExerciseName = "Bench Press",
                            Order = 1,
                            MovementType = MovementType.Reps,
                            Sets = [new ExerciseSet { SetNumber = 1, Type = SetType.Normal, Reps = 10, WeightKg = 80 }]
                        }
                    ]
                }
            ],
            StandaloneExercises =
            [
                new SessionExercise
                {
                    ExerciseId = Guid.NewGuid(),
                    ExerciseExternalId = standaloneExerciseExternalId,
                    ExerciseName = "Plank",
                    Order = 1,
                    MovementType = MovementType.Time,
                    Sets = [new ExerciseSet { SetNumber = 1, Type = SetType.Normal, DurationSeconds = 60 }]
                }
            ]
        };

        var effectiveStartDate = startDate ?? TrainingPlanTestHelpers.LastMonday();

        // Plan-addressed routes authorize on the caller's live ClientProfessionalLink, so a plan
        // seeded against a fabricated client id is unreachable. Default to a real, linked client.
        var effectiveClientUserId = clientUserId ?? await TestHelpers.RegisterLinkedClientAsync(
            factory, trainerUserId, TestContext.Current.CancellationToken);

        var plan = new TrainingPlan
        {
            ExternalId = planId,
            ClientId = effectiveClientUserId,
            TrainerId = trainerUserId,
            Name = "Round-Trip Contract Plan",
            Status = TrainingPlanStatus.Active,
            StartDate = effectiveStartDate,
            Version = 1,
            DateCreated = DateTime.UtcNow.AddDays(-14),
            Weeks =
            [
                new TrainingWeek
                {
                    WeekNumber = 1,
                    Status = published ? WeekStatus.Published : WeekStatus.Draft,
                    DatePublished = published ? DateTime.UtcNow.AddDays(-7) : null,
                    Days = TrainingPlanTestHelpers.MaterializeDays((dayOfWeek, session))
                }
            ]
        };

        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        await mongo.TrainingPlans.InsertOneAsync(plan, cancellationToken: TestContext.Current.CancellationToken);

        return (plan, sessionId, workoutId, nestedExerciseExternalId, standaloneExerciseExternalId);
    }

    /// <summary>
    /// Registers + logs in a Trainer and resolves their <c>ApplicationUser.Id</c>.
    /// </summary>
    private async Task<(HttpClient HttpClient, string AccessToken, Guid TrainerUserId)> RegisterTrainerAsync()
    {
        var httpClient = factory.CreateClient();
        var email = UniqueEmail();
        await TestHelpers.RegisterAsync(httpClient, email, "TestPass1!", "RoundTrip", "Trainer", "Trainer");
        var (accessToken, _) = await TestHelpers.LoginAsync(httpClient, email, "TestPass1!");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await db.Users.FirstAsync(u => u.Email == email, TestContext.Current.CancellationToken);

        return (httpClient, accessToken, user.Id);
    }

    /// <summary>
    /// Maps a raw <c>GET /training/plans/{planId}</c> response body to a full-state
    /// <see cref="UpdateTrainingPlanRequest"/>, by field name only — see remarks on this class.
    /// </summary>
    private static UpdateTrainingPlanRequest BuildUpdateRequestFromReadResponse(string rawGetBody, Guid planId)
    {
        using var doc = JsonDocument.Parse(rawGetBody);
        var root = doc.RootElement;

        return new UpdateTrainingPlanRequest
        {
            PlanId = planId,
            Name = root.GetProperty("name").GetString() ?? string.Empty,
            Description = root.TryGetProperty("description", out var descriptionElement)
                && descriptionElement.ValueKind == JsonValueKind.String
                ? descriptionElement.GetString()
                : null,
            Version = root.GetProperty("version").GetInt32(),
            StartDate = root.TryGetProperty("startDate", out var startDateElement)
                && startDateElement.ValueKind == JsonValueKind.String
                ? startDateElement.GetDateTime()
                : null,
            Weeks = root.GetProperty("weeks").EnumerateArray().Select(MapWeek).ToList()
        };
    }

    private static UpdateTrainingWeekRequest MapWeek(JsonElement weekElement)
    {
        var dayNotes = new Dictionary<int, string>();
        var sessions = new List<UpdateSessionRequest>();

        foreach (var dayElement in weekElement.GetProperty("days").EnumerateArray())
        {
            var dayOfWeek = dayElement.GetProperty("dayOfWeek").GetInt32();

            if (dayElement.TryGetProperty("note", out var noteElement) && noteElement.ValueKind == JsonValueKind.String)
            {
                dayNotes[dayOfWeek] = noteElement.GetString()!;
            }

            foreach (var sessionElement in dayElement.GetProperty("sessions").EnumerateArray())
            {
                sessions.Add(MapSession(sessionElement, dayOfWeek));
            }
        }

        return new UpdateTrainingWeekRequest
        {
            WeekNumber = weekElement.GetProperty("weekNumber").GetInt32(),
            Sessions = sessions,
            DayNotes = dayNotes.Count > 0 ? dayNotes : null
        };
    }

    /// <summary>
    /// Maps one read-shape session element to <see cref="UpdateSessionRequest"/>. The owning
    /// <see cref="TrainingDay.DayOfWeek"/> is injected onto the node before binding — the read
    /// shape nests it on the day, the write shape wants it flat on the session — and every other
    /// field binds BY NAME via <c>System.Text.Json</c>. A read-only field with no matching
    /// <see cref="UpdateSessionRequest"/> property (<c>allExercises</c>, <c>isCompleted</c>,
    /// <c>lockState</c>, ...) is silently skipped by STJ's default unmapped-member handling —
    /// exactly the "ignored, not rejected" contract #874 requires.
    /// </summary>
    private static UpdateSessionRequest MapSession(JsonElement sessionElement, int dayOfWeek)
    {
        var sessionNode = JsonNode.Parse(sessionElement.GetRawText())!.AsObject();
        sessionNode["dayOfWeek"] = dayOfWeek;
        return sessionNode.Deserialize<UpdateSessionRequest>(JsonOptions)!;
    }

    /// <summary>
    /// Asserts the session-shape wire contract holds on a single response's session element:
    /// the retired session-level <c>exercises</c> key is absent, <c>allExercises</c> and
    /// <c>standaloneExercises</c> are present, and the workout-level <c>exercises</c> field
    /// (a different name collision entirely — out of #874's scope) still survives.
    /// </summary>
    private static void AssertSessionWireContract(JsonElement sessionElement, string context)
    {
        sessionElement.TryGetProperty("exercises", out _).Should().BeFalse(
            $"the session-level `exercises` key must be retired from the {context} response");
        sessionElement.TryGetProperty("allExercises", out _).Should().BeTrue(
            $"the {context} response must expose the read-only `allExercises` union");
        sessionElement.TryGetProperty("standaloneExercises", out _).Should().BeTrue(
            $"the {context} response must expose `standaloneExercises`");

        var workouts = sessionElement.GetProperty("workouts");
        workouts.GetArrayLength().Should().BeGreaterThan(0, $"the {context} fixture always seeds one workout");
        workouts[0].TryGetProperty("exercises", out _).Should().BeTrue(
            $"the workout-level `exercises` field must survive the {context} response — only the " +
            "session-level name was retired, not the workout's own nested list");
    }

    // ── AC round trip ─────────────────────────────────────────────────────────

    [Fact]
    public async Task RoundTrip_SessionWithNestedAndStandaloneExercises_PersistsUnchanged()
    {
        var (httpClient, accessToken, trainerUserId) = await RegisterTrainerAsync();
        var (plan, sessionId, _, nestedExerciseExternalId, standaloneExerciseExternalId) =
            await SeedMixedSessionPlanAsync(trainerUserId);

        TestHelpers.SetBearerToken(httpClient, accessToken);

        var getResponse = await httpClient.GetAsync($"/training/plans/{plan.ExternalId}", TestContext.Current.CancellationToken);
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var rawGetBody = await getResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        var updateRequest = BuildUpdateRequestFromReadResponse(rawGetBody, plan.ExternalId);

        var putResponse = await httpClient.PutAsJsonAsync(
            $"/training/plans/{plan.ExternalId}", updateRequest, JsonOptions, TestContext.Current.CancellationToken);
        var putBody = await putResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        putResponse.StatusCode.Should().Be(
            HttpStatusCode.OK,
            $"a mechanical GET->PUT round trip with no edits must not be rejected. Body: {putBody}");

        using var assertScope = factory.Services.CreateScope();
        var mongo = assertScope.ServiceProvider.GetRequiredService<IMongoContext>();
        var persisted = await mongo.TrainingPlans
            .Find(p => p.ExternalId == plan.ExternalId)
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);

        persisted.Should().NotBeNull();
        var persistedSession = persisted!.Weeks[0].Days
            .SelectMany(d => d.Sessions)
            .Single(s => s.SessionId == sessionId);

        persistedSession.StandaloneExercises.Should().HaveCount(1,
            "the round trip must not promote the nested workout exercise into the standalone list");
        persistedSession.StandaloneExercises.Select(e => e.ExerciseExternalId).Should()
            .BeEquivalentTo([standaloneExerciseExternalId]);

        persistedSession.Workouts.Should().HaveCount(1);
        persistedSession.Workouts[0].Exercises.Should().HaveCount(1,
            "the round trip must not drop the workout's own nested exercise");
        persistedSession.Workouts[0].Exercises.Select(e => e.ExerciseExternalId).Should()
            .BeEquivalentTo([nestedExerciseExternalId]);

        persisted.Version.Should().Be(2, "the PUT must bump the optimistic-concurrency version by one");
    }

    // ── Compounding guard ─────────────────────────────────────────────────────

    [Fact]
    public async Task RoundTrip_TwoPasses_StandaloneAndNestedCountsStayStable()
    {
        var (httpClient, accessToken, trainerUserId) = await RegisterTrainerAsync();
        var (plan, sessionId, _, nestedExerciseExternalId, standaloneExerciseExternalId) =
            await SeedMixedSessionPlanAsync(trainerUserId);

        TestHelpers.SetBearerToken(httpClient, accessToken);

        // ── Pass 1 ──
        var rawGetBody1 = await (await httpClient.GetAsync(
            $"/training/plans/{plan.ExternalId}", TestContext.Current.CancellationToken))
            .Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var updateRequest1 = BuildUpdateRequestFromReadResponse(rawGetBody1, plan.ExternalId);
        var putResponse1 = await httpClient.PutAsJsonAsync(
            $"/training/plans/{plan.ExternalId}", updateRequest1, JsonOptions, TestContext.Current.CancellationToken);
        var putBody1 = await putResponse1.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        putResponse1.StatusCode.Should().Be(HttpStatusCode.OK, $"pass 1 must succeed. Body: {putBody1}");

        // ── Pass 2 — the original defect compounded per round trip, so a single-pass
        // assertion alone would not catch it. Re-GET the now-updated plan and PUT it again. ──
        var rawGetBody2 = await (await httpClient.GetAsync(
            $"/training/plans/{plan.ExternalId}", TestContext.Current.CancellationToken))
            .Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var updateRequest2 = BuildUpdateRequestFromReadResponse(rawGetBody2, plan.ExternalId);
        var putResponse2 = await httpClient.PutAsJsonAsync(
            $"/training/plans/{plan.ExternalId}", updateRequest2, JsonOptions, TestContext.Current.CancellationToken);
        var putBody2 = await putResponse2.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        putResponse2.StatusCode.Should().Be(HttpStatusCode.OK, $"pass 2 must succeed. Body: {putBody2}");

        using var assertScope = factory.Services.CreateScope();
        var mongo = assertScope.ServiceProvider.GetRequiredService<IMongoContext>();
        var persisted = await mongo.TrainingPlans
            .Find(p => p.ExternalId == plan.ExternalId)
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);

        persisted.Should().NotBeNull();
        var persistedSession = persisted!.Weeks[0].Days
            .SelectMany(d => d.Sessions)
            .Single(s => s.SessionId == sessionId);

        persistedSession.StandaloneExercises.Should().HaveCount(1,
            "two round trips must not compound the standalone list — the original defect grew it on every save");
        persistedSession.StandaloneExercises.Select(e => e.ExerciseExternalId).Should()
            .BeEquivalentTo([standaloneExerciseExternalId]);

        persistedSession.Workouts.Should().HaveCount(1);
        persistedSession.Workouts[0].Exercises.Should().HaveCount(1);
        persistedSession.Workouts[0].Exercises.Select(e => e.ExerciseExternalId).Should()
            .BeEquivalentTo([nestedExerciseExternalId]);

        persisted.Version.Should().Be(3, "two successful PUTs from Version=1 must land on Version=3");
    }

    // ── allExercises is ignored on write, not rejected ───────────────────────

    [Fact]
    public async Task Update_SpuriousAllExercisesOnWrite_IsIgnored()
    {
        var (httpClient, accessToken, trainerUserId) = await RegisterTrainerAsync();
        var (plan, sessionId, _, _, standaloneExerciseExternalId) = await SeedMixedSessionPlanAsync(trainerUserId);

        TestHelpers.SetBearerToken(httpClient, accessToken);

        var rawGetBody = await (await httpClient.GetAsync(
            $"/training/plans/{plan.ExternalId}", TestContext.Current.CancellationToken))
            .Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var updateRequest = BuildUpdateRequestFromReadResponse(rawGetBody, plan.ExternalId);

        // Serialize the mechanically-mapped request to a mutable node tree so a spurious
        // `allExercises` array can be spliced onto the wire session object alongside the correct
        // `standaloneExercises` — proving the RUNNING ENDPOINT ignores it, not just that the C#
        // UpdateSessionRequest type happens to have no matching property.
        var requestNode = JsonSerializer.SerializeToNode(updateRequest, JsonOptions)!.AsObject();
        var sessionNode = requestNode["weeks"]![0]!["sessions"]![0]!.AsObject();

        var spuriousExerciseExternalId = Guid.NewGuid();
        sessionNode["allExercises"] = new JsonArray(new JsonObject
        {
            ["exerciseId"] = Guid.NewGuid().ToString(),
            ["exerciseExternalId"] = spuriousExerciseExternalId.ToString(),
            ["exerciseName"] = "Spurious Ghost Exercise",
            ["order"] = 99,
            ["movementType"] = "Reps",
            ["sets"] = new JsonArray()
        });

        using var content = JsonContent.Create(requestNode, options: JsonOptions);
        var putResponse = await httpClient.PutAsync(
            $"/training/plans/{plan.ExternalId}", content, TestContext.Current.CancellationToken);
        var putBody = await putResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        putResponse.StatusCode.Should().Be(
            HttpStatusCode.OK,
            $"a spurious `allExercises` array on write must be ignored, not rejected. Body: {putBody}");

        using var assertScope = factory.Services.CreateScope();
        var mongo = assertScope.ServiceProvider.GetRequiredService<IMongoContext>();
        var persisted = await mongo.TrainingPlans
            .Find(p => p.ExternalId == plan.ExternalId)
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);

        var persistedSession = persisted!.Weeks[0].Days
            .SelectMany(d => d.Sessions)
            .Single(s => s.SessionId == sessionId);

        persistedSession.StandaloneExercises.Should().HaveCount(1,
            "the persisted standalone list must match `standaloneExercises` exactly");
        persistedSession.StandaloneExercises.Select(e => e.ExerciseExternalId).Should()
            .BeEquivalentTo([standaloneExerciseExternalId],
                "the spurious `allExercises` entry must have zero effect on the persisted document");
    }

    // ── Read-name proof on all three session-shape wire surfaces ────────────

    [Fact]
    public async Task RoundTrip_AllThreeSessionWireSurfaces_ExposeAllExercisesAndStandaloneExercises()
    {
        // ── Trainer + client, both registered so all three GET surfaces are reachable. ──
        var trainerHttpClient = factory.CreateClient();
        var trainerEmail = UniqueEmail();
        await TestHelpers.RegisterAsync(trainerHttpClient, trainerEmail, "TestPass1!", "Wire", "Trainer", "Trainer");
        var (trainerAccessToken, _) = await TestHelpers.LoginAsync(trainerHttpClient, trainerEmail, "TestPass1!");

        var clientHttpClient = factory.CreateClient();
        var clientEmail = UniqueEmail();
        await TestHelpers.RegisterAsync(clientHttpClient, clientEmail, "TestPass1!", "Wire", "Client", "Client");
        var (clientAccessToken, _) = await TestHelpers.LoginAsync(clientHttpClient, clientEmail, "TestPass1!");

        Guid trainerUserId;
        Guid clientUserId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var trainerUser = await db.Users.FirstAsync(
                u => u.Email == trainerEmail, TestContext.Current.CancellationToken);
            trainerUserId = trainerUser.Id;

            var clientUser = await db.Users.FirstAsync(
                u => u.Email == clientEmail, TestContext.Current.CancellationToken);
            var clientProfile = await db.ClientProfiles.FirstAsync(
                cp => cp.UserId == clientUser.Id, TestContext.Current.CancellationToken);
            clientUserId = clientProfile.UserId;

            // The trainer GET surface authorizes on the live link, not on plan.TrainerId.
            var professionalProfile = await db.ProfessionalProfiles.FirstAsync(
                pp => pp.UserId == trainerUserId, TestContext.Current.CancellationToken);

            db.ClientProfessionalLinks.Add(new ClientProfessionalLink
            {
                PublicId = Guid.NewGuid(),
                ProfessionalProfileId = professionalProfile.Id,
                ClientProfileId = clientProfile.Id,
                ProfessionalRole = UserRole.Trainer,
                ConnectionState = ConnectionState.Connected,
                CanViewNutritionPlans = true,
                CanViewTrainingPlans = true,
                DateCreated = DateTime.UtcNow
            });

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Aligned to "today" so GetTodaySession finds it, matching the pattern used by the
        // sibling GetTodaySessionProjectionIntegrationTests fixture.
        var today = DateTime.UtcNow.Date;
        var todayDow = (int)today.DayOfWeek == 0 ? 7 : (int)today.DayOfWeek;
        var startOfWeek = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));

        var (plan, sessionId, _, _, _) = await SeedMixedSessionPlanAsync(
            trainerUserId, clientUserId, startDate: startOfWeek, dayOfWeek: todayDow, published: true);

        // ── Surface 1: trainer GET /training/plans/{planId} — raw TrainingWeek/TrainingSession ──
        TestHelpers.SetBearerToken(trainerHttpClient, trainerAccessToken);
        var trainerRawBody = await (await trainerHttpClient.GetAsync(
            $"/training/plans/{plan.ExternalId}", TestContext.Current.CancellationToken))
            .Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        using (var trainerDoc = JsonDocument.Parse(trainerRawBody))
        {
            var trainerSession = trainerDoc.RootElement.GetProperty("weeks")[0].GetProperty("days")
                .EnumerateArray()
                .SelectMany(d => d.GetProperty("sessions").EnumerateArray())
                .Single(s => s.GetProperty("sessionId").GetGuid() == sessionId);

            AssertSessionWireContract(trainerSession, "GET /training/plans/{planId}");
        }

        // ── Surface 2: client GET /client/training/plan/today — raw TrainingSession ──────────
        TestHelpers.SetBearerToken(clientHttpClient, clientAccessToken);
        var todayRawBody = await (await clientHttpClient.GetAsync(
            "/client/training/plan/today", TestContext.Current.CancellationToken))
            .Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        using (var todayDoc = JsonDocument.Parse(todayRawBody))
        {
            var todaySession = todayDoc.RootElement.GetProperty("sessions")
                .EnumerateArray()
                .Single(s => s.GetProperty("sessionId").GetGuid() == sessionId);

            AssertSessionWireContract(todaySession, "GET /client/training/plan/today");
        }

        // ── Surface 3: client GET /client/training/plans/{planId} — hand-mapped SessionDto ────
        var fullPlanRawBody = await (await clientHttpClient.GetAsync(
            $"/client/training/plans/{plan.ExternalId}", TestContext.Current.CancellationToken))
            .Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        using (var fullPlanDoc = JsonDocument.Parse(fullPlanRawBody))
        {
            var fullPlanSession = fullPlanDoc.RootElement.GetProperty("weeks")[0].GetProperty("sessions")
                .EnumerateArray()
                .Single(s => s.GetProperty("sessionId").GetGuid() == sessionId);

            AssertSessionWireContract(fullPlanSession, "GET /client/training/plans/{planId}");
        }
    }

    // ── Old-shaped client is rejected loudly, not silently emptied ──────────

    [Fact]
    public async Task Update_OldShapedClientSendingOnlySessionLevelExercises_Returns400()
    {
        var (httpClient, accessToken, trainerUserId) = await RegisterTrainerAsync();
        var (plan, sessionId, _, _, _) = await SeedMixedSessionPlanAsync(trainerUserId, published: false);

        var body = new
        {
            plan.Name,
            plan.Version,
            StartDate = plan.StartDate,
            Weeks = new[]
            {
                new
                {
                    WeekNumber = 1,
                    Sessions = new[]
                    {
                        new
                        {
                            SessionId = sessionId.ToString(),
                            DayOfWeek = 1,
                            Name = "Push Day",
                            Order = 1,
                            // Old wire name — UpdateSessionRequest no longer has a matching member,
                            // so this key is silently unmapped, leaving Workouts=[] and
                            // StandaloneExercises=[] (the class's defaults).
                            Exercises = new[]
                            {
                                new
                                {
                                    ExerciseExternalId = Guid.NewGuid().ToString(),
                                    ExerciseName = "Plank",
                                    Order = 1,
                                    MovementType = "Reps",
                                    Sets = Array.Empty<object>()
                                }
                            }
                        }
                    }
                }
            }
        };

        TestHelpers.SetBearerToken(httpClient, accessToken);
        var response = await httpClient.PutAsJsonAsync(
            $"/training/plans/{plan.ExternalId}", body, TestContext.Current.CancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(
            HttpStatusCode.BadRequest,
            $"a session with no workouts and no standaloneExercises (the old `exercises` key is " +
            $"unmapped) must fail validation loudly, not persist an emptied session. Body: {responseBody}");
        responseBody.Should().Contain(
            ErrorCodes.WorkoutsRequired,
            "the workouts-or-standalone-exercises validation rule must fire");
    }

    // ── Optimistic concurrency still works after the rename ─────────────────

    [Fact]
    public async Task Update_StaleVersion_Returns409()
    {
        var (httpClient, accessToken, trainerUserId) = await RegisterTrainerAsync();
        var (plan, _, _, _, _) = await SeedMixedSessionPlanAsync(trainerUserId, published: false);

        TestHelpers.SetBearerToken(httpClient, accessToken);

        var rawGetBody = await (await httpClient.GetAsync(
            $"/training/plans/{plan.ExternalId}", TestContext.Current.CancellationToken))
            .Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var updateRequest = BuildUpdateRequestFromReadResponse(rawGetBody, plan.ExternalId);

        var firstPutResponse = await httpClient.PutAsJsonAsync(
            $"/training/plans/{plan.ExternalId}", updateRequest, JsonOptions, TestContext.Current.CancellationToken);
        firstPutResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            "the first PUT (Version=1) must succeed and bump the version to 2");

        // Reuse the SAME (now-stale) request, still carrying the original Version=1.
        var secondPutResponse = await httpClient.PutAsJsonAsync(
            $"/training/plans/{plan.ExternalId}", updateRequest, JsonOptions, TestContext.Current.CancellationToken);
        var secondPutBody = await secondPutResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        secondPutResponse.StatusCode.Should().Be(
            HttpStatusCode.Conflict,
            $"a stale Version must be rejected 409, not silently accepted or mis-serialized due to " +
            $"the renamed member. Body: {secondPutBody}");
        secondPutBody.Should().Contain(ErrorCodes.PlanVersionConflict);
    }
}
