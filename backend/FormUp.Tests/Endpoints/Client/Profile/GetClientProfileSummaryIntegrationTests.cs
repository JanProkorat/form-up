using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Client.Profile;

/// <summary>
/// Integration tests for <c>GET /client/profile/summary</c>. Real PostgreSQL and MongoDB, because
/// the typed Guid filters and the visibility predicates cannot be proven against mocks.
/// </summary>
[Collection(TestCollection.Shard3)]
public class GetClientProfileSummaryIntegrationTests(FitnessApiFactory factory)
{
    private sealed record SummaryBody(
        string? Goal,
        DateTime MemberSince,
        decimal? LatestWeightKg,
        decimal? WeightChangeSinceStartKg,
        int PlanCount,
        int CompletedWorkoutCount,
        int ProgressPhotoCount,
        int CheckInCount);

    [Fact]
    public async Task GetSummary_ClientWithNoData_Returns200WithNullsAndZeroCounts()
    {
        var createdAt = new DateTime(2025, 3, 1, 8, 0, 0, DateTimeKind.Utc);
        var client = await TestActors.Client(factory).CreatedAt(createdAt).CreateAsync(TestContext.Current.CancellationToken);

        var response = await client.Http.GetAsync("/client/profile/summary", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<SummaryBody>(TestContext.Current.CancellationToken);
        body.Should().NotBeNull();
        body!.Goal.Should().BeNull();
        body.MemberSince.Should().BeCloseTo(createdAt, TimeSpan.FromSeconds(1));
        body.LatestWeightKg.Should().BeNull();
        body.WeightChangeSinceStartKg.Should().BeNull();
        body.PlanCount.Should().Be(0);
        body.CompletedWorkoutCount.Should().Be(0);
        body.ProgressPhotoCount.Should().Be(0);
        body.CheckInCount.Should().Be(0);
    }

    [Fact]
    public async Task GetSummary_PopulatedClient_CountsOnlyWhatTheClientCanSee()
    {
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        var coach = await TestActors.Trainer(factory).CreateAsync(TestContext.Current.CancellationToken);

        var exerciseId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var otherSessionId = Guid.NewGuid();
        var visiblePlanId = Guid.NewGuid();

        await SeedRelationalAsync(client, coach, weights: [82m, 80m, 78.5m]);
        await SeedPhotosAsync(client.ProfileId);

        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
            var ct = TestContext.Current.CancellationToken;

            await mongo.TrainingPlans.InsertManyAsync(
            [
                TrainingPlanFor(client.UserId, visiblePlanId, TrainingPlanStatus.Active, WeekStatus.Published, sessionId, exerciseId, otherSessionId),
                TrainingPlanFor(client.UserId, Guid.NewGuid(), TrainingPlanStatus.Draft, WeekStatus.Published, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()),
                TrainingPlanFor(client.UserId, Guid.NewGuid(), TrainingPlanStatus.Active, WeekStatus.Draft, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()),
            ], cancellationToken: ct);

            await mongo.NutritionPlans.InsertManyAsync(
            [
                NutritionPlanFor(client.UserId, NutritionPlanStatus.Active, WeekStatus.Published),
                NutritionPlanFor(client.UserId, NutritionPlanStatus.Draft, WeekStatus.Published),
            ], cancellationToken: ct);

            await mongo.SessionExecutions.InsertManyAsync(
            [
                // Checkbox completion: Partial status, every exercise ticked.
                ExecutionFor(client.UserId, visiblePlanId, sessionId, SessionExecutionStatus.Partial, [exerciseId]),
                // Partial with nothing ticked.
                ExecutionFor(client.UserId, visiblePlanId, otherSessionId, SessionExecutionStatus.Partial, []),
                // Completed, but the session is in no plan any more.
                ExecutionFor(client.UserId, visiblePlanId, Guid.NewGuid(), SessionExecutionStatus.Completed, []),
            ], cancellationToken: ct);
        }

        var response = await client.Http.GetAsync("/client/profile/summary", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<SummaryBody>(TestContext.Current.CancellationToken);
        body!.Goal.Should().Be("LoseFat");
        body.LatestWeightKg.Should().Be(78.5m);
        body.WeightChangeSinceStartKg.Should().Be(-3.5m);
        body.PlanCount.Should().Be(2, "one active training plan and one active nutrition plan have a published week");
        body.CompletedWorkoutCount.Should().Be(2);
        body.ProgressPhotoCount.Should().Be(3, "Body (planned and plan-less) and FreeForm count; Food and Training do not");
        body.CheckInCount.Should().Be(2);
    }

    [Fact]
    public async Task GetSummary_SingleWeightMeasurement_ChangeIsZero()
    {
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        await SeedRelationalAsync(client, null, weights: [75m], checkIns: 0);

        var response = await client.Http.GetAsync("/client/profile/summary", TestContext.Current.CancellationToken);

        var body = await response.Content.ReadFromJsonAsync<SummaryBody>(TestContext.Current.CancellationToken);
        body!.LatestWeightKg.Should().Be(75m);
        body.WeightChangeSinceStartKg.Should().Be(0m);
    }

    [Fact]
    public async Task GetSummary_OtherClientsData_IsNotCounted()
    {
        var caller = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        var other = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        var coach = await TestActors.Trainer(factory).CreateAsync(TestContext.Current.CancellationToken);

        var exerciseId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var otherPlanId = Guid.NewGuid();

        await SeedRelationalAsync(other, coach, weights: [90m, 70m]);
        await SeedPhotosAsync(other.ProfileId);

        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
            var ct = TestContext.Current.CancellationToken;
            await mongo.TrainingPlans.InsertOneAsync(
                TrainingPlanFor(other.UserId, otherPlanId, TrainingPlanStatus.Active, WeekStatus.Published, sessionId, exerciseId, Guid.NewGuid()),
                cancellationToken: ct);
            await mongo.NutritionPlans.InsertOneAsync(
                NutritionPlanFor(other.UserId, NutritionPlanStatus.Active, WeekStatus.Published), cancellationToken: ct);
            await mongo.SessionExecutions.InsertOneAsync(
                ExecutionFor(other.UserId, otherPlanId, sessionId, SessionExecutionStatus.Completed, [exerciseId]), cancellationToken: ct);
        }

        var response = await caller.Http.GetAsync("/client/profile/summary", TestContext.Current.CancellationToken);

        var body = await response.Content.ReadFromJsonAsync<SummaryBody>(TestContext.Current.CancellationToken);
        body!.Goal.Should().BeNull();
        body.LatestWeightKg.Should().BeNull();
        body.WeightChangeSinceStartKg.Should().BeNull();
        body.PlanCount.Should().Be(0);
        body.CompletedWorkoutCount.Should().Be(0);
        body.ProgressPhotoCount.Should().Be(0);
        body.CheckInCount.Should().Be(0);
    }

    private async Task SeedRelationalAsync(Actor client, Actor? coach, decimal[] weights, int checkIns = 2)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var ct = TestContext.Current.CancellationToken;

        var onboarding = EntityBuilder.ClientOnboardingData
            .WithGeneratedId()
            .WithClientProfileId(client.ProfileId)
            .Build();
        onboarding.PrimaryGoal = PrimaryGoal.LoseFat;
        db.ClientOnboardingData.Add(onboarding);

        var start = new DateTime(2025, 1, 1, 7, 0, 0, DateTimeKind.Utc);
        for (var index = 0; index < weights.Length; index++)
        {
            db.BodyMeasurements.Add(new BodyMeasurement
            {
                ClientProfileId = client.ProfileId,
                MeasuredAt = start.AddDays(index * 7),
                WeightKg = weights[index],
            });
        }

        // A measurement with no weight must not become the "latest" weight.
        db.BodyMeasurements.Add(new BodyMeasurement
        {
            ClientProfileId = client.ProfileId,
            MeasuredAt = start.AddDays(365),
            WaistCm = 80m,
        });

        await db.SaveChangesAsync(ct);

        if (checkIns > 0)
        {
            var scenario = await CheckInFixtures.CreateScenarioAsync(factory, coach, client);

            for (var index = 0; index < checkIns; index++)
            {
                await CheckInFixtures.AddCheckInAsync(
                    factory, scenario, new DateOnly(2025, 1, 6).AddDays(index * 7), start.AddDays(2));
            }
        }
    }

    private async Task SeedPhotosAsync(long clientProfileId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var uploader = await db.ClientProfiles
            .Where(profile => profile.Id == clientProfileId)
            .Select(profile => profile.UserId)
            .FirstAsync(TestContext.Current.CancellationToken);

        var planId = Guid.NewGuid();
        db.PlanPhotos.AddRange(
            Photo(clientProfileId, uploader, PlanPhotoCategory.Body, planId),
            Photo(clientProfileId, uploader, PlanPhotoCategory.Body, null),
            Photo(clientProfileId, uploader, PlanPhotoCategory.FreeForm, null),
            Photo(clientProfileId, uploader, PlanPhotoCategory.Food, planId),
            Photo(clientProfileId, uploader, PlanPhotoCategory.Training, planId));

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static PlanPhoto Photo(long clientProfileId, Guid uploaderUserId, PlanPhotoCategory category, Guid? planId) => new()
    {
        ClientProfileId = clientProfileId,
        PlanId = planId,
        LinkId = planId,
        PlanType = planId is null ? null : PlanPhotoType.Nutrition,
        Category = category,
        BlobUrl = $"plan-photos/{Guid.NewGuid()}.jpg",
        TakenAt = DateTime.UtcNow,
        UploadedByUserId = uploaderUserId,
    };

    private static TrainingPlan TrainingPlanFor(
        Guid clientUserId,
        Guid planId,
        TrainingPlanStatus status,
        WeekStatus weekStatus,
        Guid sessionId,
        Guid exerciseId,
        Guid otherSessionId) => new()
    {
        ExternalId = planId,
        ClientId = clientUserId,
        TrainerId = Guid.NewGuid(),
        Name = "Summary plan",
        Status = status,
        Version = 1,
        DateCreated = DateTime.UtcNow,
        Weeks =
        [
            new TrainingWeek
            {
                WeekNumber = 1,
                Status = weekStatus,
                DatePublished = weekStatus == WeekStatus.Published ? DateTime.UtcNow.AddDays(-1) : null,
                Days =
                [
                    new TrainingDay
                    {
                        DayOfWeek = 1,
                        Sessions =
                        [
                            new TrainingSession
                            {
                                SessionId = sessionId,
                                Name = "A",
                                StandaloneExercises = [new SessionExercise { ExerciseId = exerciseId, ExerciseExternalId = Guid.NewGuid() }],
                            },
                            new TrainingSession
                            {
                                SessionId = otherSessionId,
                                Name = "B",
                                StandaloneExercises = [new SessionExercise { ExerciseId = Guid.NewGuid(), ExerciseExternalId = Guid.NewGuid() }],
                            },
                        ],
                    },
                ],
            },
        ],
    };

    private static NutritionPlan NutritionPlanFor(Guid clientUserId, NutritionPlanStatus status, WeekStatus weekStatus) => new()
    {
        ExternalId = Guid.NewGuid(),
        ClientId = clientUserId,
        NutritionistId = Guid.NewGuid(),
        Name = "Summary nutrition plan",
        Status = status,
        Version = 1,
        DateCreated = DateTime.UtcNow,
        Weeks = [new PlanWeek { WeekNumber = 1, Status = weekStatus, DatePublished = DateTime.UtcNow.AddDays(-1) }],
    };

    private static SessionExecution ExecutionFor(
        Guid clientUserId,
        Guid planId,
        Guid sessionId,
        SessionExecutionStatus status,
        List<Guid> completedExerciseIds) => new()
    {
        ExternalId = Guid.NewGuid(),
        ClientId = clientUserId,
        PlanId = planId,
        SessionId = sessionId,
        Date = DateTime.UtcNow.Date,
        Status = status,
        CompletedExerciseInstanceIds = completedExerciseIds,
        DateCreated = DateTime.UtcNow,
    };
}
