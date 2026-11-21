using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.SessionTemplates.Shared;
using FormUp.Application.Features.SessionTemplates.UpdateSessionTemplate;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using FluentValidation.TestHelper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace FormUp.Tests.Endpoints.SessionTemplates;

/// <summary>
/// Integration test for <c>POST /training/session-templates/from-plan</c>
/// (<see cref="Application.Features.SessionTemplates.SaveSessionTemplateFromPlan.SaveSessionTemplateFromPlanEndpoint"/>) —
/// only the success path, which calls <c>Send.CreatedAtAsync</c> and therefore needs the real
/// <c>LinkGenerator</c> that <see cref="FitnessApiFactory"/> provides (unavailable in the
/// lightweight <c>Factory.Create&lt;T&gt;()</c> host used by <see cref="SessionTemplateEndpointTests"/>).
/// Same precedent as <c>SaveMealTemplateFromPlanEndpointTests</c>.
/// </summary>
[Collection(TestCollection.Shard2)]
public class SaveSessionTemplateFromPlanEndpointTests(FitnessApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static string UniqueEmail() => $"{Guid.NewGuid():N}@save-session-template-from-plan-test.com";

    private async Task<(HttpClient Client, Guid TrainerId)> RegisterTrainerAsync()
    {
        var client = factory.CreateClient();
        var email = UniqueEmail();
        await TestHelpers.RegisterAsync(client, email, "TestPass1!", "SaveFromPlan", "SessionTemplateTest", "Trainer");
        var (accessToken, _) = await TestHelpers.LoginAsync(client, email, "TestPass1!");
        TestHelpers.SetBearerToken(client, accessToken);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await db.Users.FirstAsync(
            u => u.Email == email, TestContext.Current.CancellationToken);

        return (client, user.Id);
    }

    private async Task<(TrainingPlan Plan, TrainingSession Session)> InsertPlanWithSessionAsync(Guid trainerId)
    {
        // The route authorizes on the caller's live link to the plan's client, not on authorship
        // alone, so the source plan needs a real linked client rather than a fabricated id.
        var clientUserId = await TestHelpers.RegisterLinkedClientAsync(
            factory, trainerId, TestContext.Current.CancellationToken);

        var session = new TrainingSession
        {
            SessionId = Guid.NewGuid(),
            Name = "Push Day",
            Order = 1,
            Workouts =
            [
                new TrainingWorkout
                {
                    WorkoutId = Guid.NewGuid(),
                    Order = 0,
                    Name = "Main",
                    Exercises =
                    [
                        new SessionExercise
                        {
                            ExerciseExternalId = Guid.NewGuid(),
                            ExerciseName = "Bench Press",
                            Order = 1
                        }
                    ]
                }
            ],
            StandaloneExercises =
            [
                new SessionExercise
                {
                    ExerciseExternalId = Guid.NewGuid(),
                    ExerciseName = "Plank",
                    Order = 1
                }
            ]
        };

        var plan = new TrainingPlan
        {
            ExternalId = Guid.NewGuid(),
            TrainerId = trainerId,
            ClientId = clientUserId,
            Name = "Test Plan",
            Weeks =
            [
                new TrainingWeek
                {
                    WeekNumber = 1,
                    Days = [new TrainingDay { DayOfWeek = 1, Sessions = [session] }]
                }
            ]
        };

        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        await mongo.TrainingPlans.InsertOneAsync(plan, cancellationToken: TestContext.Current.CancellationToken);
        return (plan, session);
    }

    [Fact]
    public async Task SaveSessionTemplateFromPlan_ValidRequest_CopiesWorkoutsAndStandaloneExercises()
    {
        var (client, trainerId) = await RegisterTrainerAsync();
        var (plan, session) = await InsertPlanWithSessionAsync(trainerId);

        var response = await client.PostAsJsonAsync(
            "/training/session-templates/from-plan",
            new
            {
                PlanId = plan.ExternalId,
                WeekNumber = 1,
                DayOfWeek = 1,
                SessionId = session.SessionId,
                Name = "From Plan Session",
                Visibility = LibraryVisibility.Private
            },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await response.Content.ReadFromJsonAsync<SessionTemplateDetailResponse>(
            JsonOptions, TestContext.Current.CancellationToken);
        body!.Workouts.Should().HaveCount(1);
        body.Workouts[0].Exercises.Should().ContainSingle(e => e.ExerciseName == "Bench Press");
        body.StandaloneExercises.Should().ContainSingle(e => e.ExerciseName == "Plank");

        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        var persisted = await mongo.SessionTemplates
            .Find(t => t.ExternalId == body.TemplateId)
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);

        persisted.Should().NotBeNull();
        persisted!.OwnerId.Should().Be(trainerId);
        persisted.Workouts.Should().HaveCount(1);
        persisted.StandaloneExercises.Should().HaveCount(1);
    }

    /// <summary>
    /// Deny-path test for the link-authorization guard itself (not authorship). The plan is
    /// owned by the caller, but the caller's link to the plan's client no longer grants training
    /// access — this must 404 (same shaped denial as a missing plan), never a 200. If
    /// <see cref="IClientLinkAuthorizationService"/> were removed from this guard, this test
    /// would regress to 201.
    /// </summary>
    [Fact]
    public async Task SaveSessionTemplateFromPlan_LinkGrantsOnlyNutrition_Returns404()
    {
        var (client, trainerId) = await RegisterTrainerAsync();

        // Link exists but grants only the nutrition domain — must not admit a training route.
        var clientUserId = await TestHelpers.RegisterLinkedClientAsync(
            factory, trainerId, TestContext.Current.CancellationToken,
            canViewNutritionPlans: true, canViewTrainingPlans: false);

        var sessionId = Guid.NewGuid();
        var plan = new TrainingPlan
        {
            ExternalId = Guid.NewGuid(),
            TrainerId = trainerId,
            ClientId = clientUserId,
            Name = "Test Plan",
            Weeks =
            [
                new TrainingWeek
                {
                    WeekNumber = 1,
                    Days =
                    [
                        new TrainingDay
                        {
                            DayOfWeek = 1,
                            Sessions =
                            [
                                new TrainingSession { SessionId = sessionId, Name = "Push Day", Order = 1 }
                            ]
                        }
                    ]
                }
            ]
        };

        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
            await mongo.TrainingPlans.InsertOneAsync(plan, cancellationToken: TestContext.Current.CancellationToken);
        }

        var response = await client.PostAsJsonAsync(
            "/training/session-templates/from-plan",
            new
            {
                PlanId = plan.ExternalId,
                WeekNumber = 1,
                DayOfWeek = 1,
                SessionId = sessionId,
                Name = "Denied Template",
                Visibility = LibraryVisibility.Private
            },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// #892 regression: <see cref="TrainingSession.Format"/> is nullable and its own doc comment
    /// says "Null when Format is null or Standard" — an invariant <c>UpdateTrainingPlanValidator</c>
    /// does not actually enforce (its Null()/NotNull() rules both skip via <c>When()</c> when
    /// Format is null), so a plan session with <c>Format = null</c> and a non-null
    /// <see cref="WodConfig"/> is a reachable, plan-valid state. Before the fix, the clone
    /// coalesced that null to <see cref="WorkoutFormat.Standard"/> while copying
    /// <c>FormatConfig</c> verbatim, persisting a template <c>SessionTemplateRuleSet</c> would
    /// then reject on every subsequent edit — a permanent lockout on a template this same POST
    /// just returned 201 for.
    /// </summary>
    [Fact]
    public async Task SaveSessionTemplateFromPlan_SourceFormatNullWithNonNullFormatConfig_ProducesSaveableTemplate()
    {
        var (client, trainerId) = await RegisterTrainerAsync();
        var clientUserId = await TestHelpers.RegisterLinkedClientAsync(
            factory, trainerId, TestContext.Current.CancellationToken);

        var sessionId = Guid.NewGuid();
        var session = new TrainingSession
        {
            SessionId = sessionId,
            Name = "Push Day",
            Order = 1,
            Format = null, // the plan write path's guard never fires on a null Format
            FormatConfig = new WodConfig { IntervalSeconds = 60, TotalRounds = 10 },
            Workouts =
            [
                new TrainingWorkout
                {
                    WorkoutId = Guid.NewGuid(),
                    Order = 0,
                    Name = "Main",
                    Exercises =
                    [
                        new SessionExercise { ExerciseExternalId = Guid.NewGuid(), ExerciseName = "Bench Press", Order = 1 }
                    ]
                }
            ]
        };

        var plan = new TrainingPlan
        {
            ExternalId = Guid.NewGuid(),
            TrainerId = trainerId,
            ClientId = clientUserId,
            Name = "Test Plan",
            Weeks =
            [
                new TrainingWeek
                {
                    WeekNumber = 1,
                    Days = [new TrainingDay { DayOfWeek = 1, Sessions = [session] }]
                }
            ]
        };

        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
            await mongo.TrainingPlans.InsertOneAsync(plan, cancellationToken: TestContext.Current.CancellationToken);
        }

        var response = await client.PostAsJsonAsync(
            "/training/session-templates/from-plan",
            new
            {
                PlanId = plan.ExternalId,
                WeekNumber = 1,
                DayOfWeek = 1,
                SessionId = sessionId,
                Name = "From Null-Format Session",
                Visibility = LibraryVisibility.Private
            },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await response.Content.ReadFromJsonAsync<SessionTemplateDetailResponse>(
            JsonOptions, TestContext.Current.CancellationToken);

        using var verifyScope = factory.Services.CreateScope();
        var verifyMongo = verifyScope.ServiceProvider.GetRequiredService<IMongoContext>();
        var persisted = await verifyMongo.SessionTemplates
            .Find(t => t.ExternalId == body!.TemplateId)
            .FirstOrDefaultAsync(TestContext.Current.CancellationToken);

        persisted.Should().NotBeNull();

        // The exact edit a coach would perform next: fetch, then PUT the same content back.
        var updateRequest = new UpdateSessionTemplateRequest
        {
            TemplateId = persisted!.ExternalId,
            Name = persisted.Name,
            Description = persisted.Description,
            Difficulty = persisted.Difficulty,
            EstimatedDurationMinutes = persisted.EstimatedDurationMinutes,
            Format = persisted.Format,
            FormatConfig = persisted.FormatConfig,
            Workouts = persisted.Workouts,
            StandaloneExercises = persisted.StandaloneExercises,
            Visibility = persisted.Visibility,
            Version = persisted.Version
        };

        new UpdateSessionTemplateValidator().TestValidate(updateRequest).IsValid.Should().BeTrue(
            "a template cloned from a plan session with Format=null must never coalesce to " +
            "Standard while keeping a non-null FormatConfig, or every subsequent edit 400s forever");
    }
}
