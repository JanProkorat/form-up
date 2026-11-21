using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.Trainers.GetClientProgress;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Builders;
using FormUp.Tests.Endpoints.NutritionPlans;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;

namespace FormUp.Tests.Endpoints.Trainers;

/// <summary>
/// With several Active plans for one client, compliance and streak score each date against the plan
/// whose window covers that date. Real Mongo, because the mock harness ignores filters. The older
/// plan is always inserted first, which is the order an arbitrary first-match lookup would pick.
/// </summary>
[Collection(TestCollection.Shard2)]
public class ComplianceCurrentPlanIntegrationTests(FitnessApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    // The progress endpoint defaults to the last 8 UTC days; keep the seeded dates off the midnight edge.
    private static async Task<DateTime> TodayAsync()
    {
        var timeOfDay = DateTime.UtcNow.TimeOfDay;
        var margin = TimeSpan.FromSeconds(30);

        if (timeOfDay > TimeSpan.FromDays(1) - margin)
        {
            await Task.Delay(TimeSpan.FromDays(1) - timeOfDay + TimeSpan.FromSeconds(1), Ct);
        }

        return DateTime.UtcNow.Date;
    }

    private async Task<(Actor Trainer, Actor Client)> LinkedPairAsync()
    {
        var trainer = await TestActors.Trainer(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, trainer, client).CreateAsync(Ct);
        return (trainer, client);
    }

    private async Task SeedNutritionPlanAsync(Actor client, Actor trainer, DateTime start, int weekCount)
    {
        var plan = PlanTestHelpers.CreatePlan(
            clientId: client.UserId,
            nutritionistId: trainer.UserId,
            status: NutritionPlanStatus.Active,
            weekCount: weekCount);
        plan.Id = ObjectId.GenerateNewId();
        plan.StartDate = start;

        foreach (var week in plan.Weeks)
        {
            week.Status = WeekStatus.Published;
            week.DatePublished = start;

            foreach (var day in week.Days)
            {
                day.Meals = [PlanTestHelpers.CreateMeal()];
            }
        }

        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        await mongo.NutritionPlans.InsertOneAsync(plan, cancellationToken: Ct);
    }

    private async Task LogMealsAsync(Actor client, DateTime firstDay, DateTime lastDay)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
        {
            await mongo.MealLogs.InsertOneAsync(new MealLog
            {
                Id = ObjectId.GenerateNewId(),
                ClientId = client.UserId,
                PlanId = Guid.NewGuid(),
                MealId = Guid.NewGuid(),
                EatenAt = day.AddHours(12),
                FoodsEaten = [],
            }, cancellationToken: Ct);
        }
    }

    private static TrainingSession SessionWithOneExercise()
    {
        var exerciseId = Guid.NewGuid();
        return new TrainingSession
        {
            SessionId = Guid.NewGuid(),
            Name = "Session",
            Order = 1,
            StandaloneExercises =
            [
                new SessionExercise
                {
                    ExerciseId = exerciseId,
                    ExerciseExternalId = exerciseId,
                    ExerciseName = "Exercise",
                    Order = 1
                }
            ]
        };
    }

    private async Task<Guid> SeedTrainingPlanAsync(
        Actor client, Actor trainer, DateTime start, int weekCount, TrainingSession session)
    {
        var planId = Guid.NewGuid();

        var plan = new TrainingPlan
        {
            Id = ObjectId.GenerateNewId(),
            ExternalId = planId,
            ClientId = client.UserId,
            TrainerId = trainer.UserId,
            Name = $"Plan {planId:N}",
            Status = TrainingPlanStatus.Active,
            StartDate = start,
            Weeks = Enumerable.Range(1, weekCount).Select(weekNumber => new TrainingWeek
            {
                WeekNumber = weekNumber,
                Status = WeekStatus.Published,
                DatePublished = start,
                Days = Enumerable.Range(1, 7).Select(dayOfWeek => new TrainingDay
                {
                    DayOfWeek = dayOfWeek,
                    Sessions = [session]
                }).ToList()
            }).ToList(),
            Version = 1,
            DateCreated = DateTime.UtcNow
        };

        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        await mongo.TrainingPlans.InsertOneAsync(plan, cancellationToken: Ct);
        return planId;
    }

    private async Task CompleteSessionAsync(
        Actor client, Guid planId, TrainingSession session, DateTime firstDay, DateTime lastDay)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
        {
            await mongo.SessionExecutions.InsertOneAsync(new SessionExecution
            {
                Id = ObjectId.GenerateNewId(),
                ExternalId = Guid.NewGuid(),
                ClientId = client.UserId,
                PlanId = planId,
                SessionId = session.SessionId,
                Date = day,
                Status = SessionExecutionStatus.Partial,
                CompletedExerciseInstanceIds = session.StandaloneExercises.Select(e => e.ExerciseId).ToList(),
                DateCreated = DateTime.UtcNow,
                Version = 1
            }, cancellationToken: Ct);
        }
    }

    private async Task<GetClientProgressResponse> ProgressAsync(Actor trainer, Actor client)
    {
        var response = await trainer.Http.GetAsync($"/trainer/clients/{client.PublicId}/progress", Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<GetClientProgressResponse>(JsonOptions, Ct))!;
    }

    [Fact]
    public async Task Compliance_NutritionExpiredOlderPlanAndCurrentPlan_FiguresComeFromCurrentPlan()
    {
        var today = await TodayAsync();
        var (trainer, client) = await LinkedPairAsync();
        await SeedNutritionPlanAsync(client, trainer, today.AddDays(-28), weekCount: 2);
        await SeedNutritionPlanAsync(client, trainer, today.AddDays(-7), weekCount: 4);
        await LogMealsAsync(client, today.AddDays(-3), today);

        var progress = await ProgressAsync(trainer, client);

        progress.MealsPlanned.Should().Be(8);
        progress.MealsLogged.Should().Be(4);
        progress.CompliancePercent.Should().Be(50m);
        progress.CurrentStreak.Should().Be(4);
    }

    [Fact]
    public async Task Compliance_NutritionPlansMeetingMidRange_CountsBothPlansDays()
    {
        var today = await TodayAsync();
        var (trainer, client) = await LinkedPairAsync();
        await SeedNutritionPlanAsync(client, trainer, today.AddDays(-9), weekCount: 1);
        await SeedNutritionPlanAsync(client, trainer, today.AddDays(-2), weekCount: 2);
        await LogMealsAsync(client, today.AddDays(-7), today);

        var progress = await ProgressAsync(trainer, client);

        progress.MealsPlanned.Should().Be(8);
        progress.MealsLogged.Should().Be(8);
        progress.CompliancePercent.Should().Be(100m);
        progress.CurrentStreak.Should().Be(8);
    }

    [Fact]
    public async Task Compliance_TrainingExpiredOlderPlanAndCurrentPlan_FiguresComeFromCurrentPlan()
    {
        var today = await TodayAsync();
        var (trainer, client) = await LinkedPairAsync();
        var expiredSession = SessionWithOneExercise();
        var currentSession = SessionWithOneExercise();
        await SeedTrainingPlanAsync(client, trainer, today.AddDays(-28), weekCount: 2, expiredSession);
        var currentPlanId = await SeedTrainingPlanAsync(client, trainer, today.AddDays(-7), weekCount: 4, currentSession);
        await CompleteSessionAsync(client, currentPlanId, currentSession, today.AddDays(-3), today);

        var progress = await ProgressAsync(trainer, client);

        progress.CompliancePercent.Should().Be(50m);
        progress.CurrentStreak.Should().Be(4);
    }

    [Fact]
    public async Task Compliance_TrainingPlansMeetingMidRange_CountsBothPlansDays()
    {
        var today = await TodayAsync();
        var (trainer, client) = await LinkedPairAsync();
        var earlierSession = SessionWithOneExercise();
        var laterSession = SessionWithOneExercise();
        var earlierPlanId = await SeedTrainingPlanAsync(client, trainer, today.AddDays(-9), weekCount: 1, earlierSession);
        var laterPlanId = await SeedTrainingPlanAsync(client, trainer, today.AddDays(-2), weekCount: 2, laterSession);
        await CompleteSessionAsync(client, earlierPlanId, earlierSession, today.AddDays(-7), today.AddDays(-6));
        await CompleteSessionAsync(client, laterPlanId, laterSession, today.AddDays(-2), today);

        var progress = await ProgressAsync(trainer, client);

        // 8 days in range, every one scheduled by one of the two plans; 5 of them done.
        progress.CompliancePercent.Should().Be(62.5m);
        progress.CurrentStreak.Should().Be(3);
    }

    [Fact]
    public async Task Compliance_OnlyExpiredPlans_IsZeroWithoutError()
    {
        var today = await TodayAsync();
        var (trainer, client) = await LinkedPairAsync();
        await SeedNutritionPlanAsync(client, trainer, today.AddDays(-28), weekCount: 1);
        var session = SessionWithOneExercise();
        var planId = await SeedTrainingPlanAsync(client, trainer, today.AddDays(-28), weekCount: 1, session);
        await LogMealsAsync(client, today.AddDays(-2), today);
        await CompleteSessionAsync(client, planId, session, today.AddDays(-2), today);

        var progress = await ProgressAsync(trainer, client);

        progress.MealsPlanned.Should().Be(0);
        progress.CompliancePercent.Should().Be(0m);
        progress.CurrentStreak.Should().Be(0);
    }
}
