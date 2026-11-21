using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Features.ClientTraining;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Builders;
using FormUp.Tests.Endpoints.NutritionPlans;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using NSubstitute;

namespace FormUp.Tests.Endpoints.ClientTraining;

/// <summary>
/// The compliance and streak in the <c>trainingprogressupdated</c> event come only from plans the
/// receiving trainer wrote. Real Mongo, because the mock harness ignores filters.
/// </summary>
[Collection(TestCollection.Shard3)]
public class TrainingProgressBroadcasterAuthorFilterIntegrationTests(FitnessApiFactory factory)
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task BroadcastWholeDay_AnotherCoachsNutritionPlan_DoesNotFeedTheTrainersComplianceOrStreak()
    {
        var trainer = await TestActors.Trainer(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, trainer, client).CreateAsync(Ct);

        var today = DateTime.UtcNow.Date;
        var dayOfWeek = today.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)today.DayOfWeek;
        var monday = today.AddDays(-(dayOfWeek - 1));

        var foreignPlan = PlanTestHelpers.CreatePlan(
            clientId: client.UserId, nutritionistId: Guid.NewGuid(), status: NutritionPlanStatus.Active, weekCount: 1);
        foreignPlan.Id = ObjectId.GenerateNewId();
        foreignPlan.StartDate = monday;
        foreignPlan.Weeks[0].Status = WeekStatus.Published;
        foreignPlan.Weeks[0].DatePublished = monday;
        foreignPlan.Weeks[0].Days[dayOfWeek - 1].Meals = [PlanTestHelpers.CreateMeal()];

        var trainerPlan = new TrainingPlan
        {
            Id = ObjectId.GenerateNewId(),
            ExternalId = Guid.NewGuid(),
            ClientId = client.UserId,
            TrainerId = trainer.UserId,
            Name = "Broadcast author filter plan",
            Status = TrainingPlanStatus.Active,
            StartDate = monday,
            Weeks = [new TrainingWeek { WeekNumber = 1, Status = WeekStatus.Published, DatePublished = monday, Days = [] }],
            Version = 1,
            DateCreated = DateTime.UtcNow,
        };

        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        await mongo.NutritionPlans.InsertOneAsync(foreignPlan, cancellationToken: Ct);
        await mongo.MealLogs.InsertOneAsync(new MealLog
        {
            Id = ObjectId.GenerateNewId(),
            ClientId = client.UserId,
            PlanId = foreignPlan.ExternalId,
            MealId = Guid.NewGuid(),
            EatenAt = DateTime.UtcNow,
            FoodsEaten = [],
        }, cancellationToken: Ct);

        object? captured = null;
        var notifier = Substitute.For<IRealtimeNotifier>();
        await notifier.NotifyAsync(
            trainer.UserId, "trainingprogressupdated",
            Arg.Do<object>(payload => captured = payload), Arg.Any<CancellationToken>());

        await TrainingProgressBroadcaster.BroadcastWholeDayAsync(
            notifier,
            scope.ServiceProvider.GetRequiredService<IComplianceService>(),
            mongo,
            scope.ServiceProvider.GetRequiredService<IClientLinkAuthorizationService>(),
            trainerPlan,
            client.UserId,
            DateOnly.FromDateTime(today),
            aggregateCompletedExercises: 0,
            aggregateTotalExercises: 0,
            Substitute.For<ILogger>(),
            Ct);

        var payloadSent = captured.Should().BeOfType<TrainingProgressUpdatedEvent>().Subject;
        payloadSent.NewCompliancePercent.Should().Be(0m, "the other coach's meal plan is not the trainer's");
        payloadSent.NewStreak.Should().Be(0);
    }
}
