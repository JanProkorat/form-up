using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Builders;
using FormUp.Tests.Endpoints.Automations;
using FormUp.Tests.Endpoints.SessionExecutions;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace FormUp.Tests.Services;

/// <summary>
/// Integration tests for <see cref="IClientVerdictService.GetLastActiveAtAsync(IReadOnlyCollection{ValueTuple{Guid, long}}, CancellationToken)"/>
/// against the real Postgres and Mongo containers.
/// </summary>
[Collection(TestCollection.Shard4)]
public class ClientVerdictServiceLastActiveTests(FitnessApiFactory factory)
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static DateTime HoursAgo(int hours) => DateTime.UtcNow.AddHours(-hours);

    [Fact]
    public async Task GetLastActiveAtAsync_Batch_ReturnsEachClientsLatestFromWhicheverSourceHasIt()
    {
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IClientVerdictService>();
        var mongo = factory.Services.GetRequiredService<IMongoContext>();

        var measuredOnly = await TestActors.Client(factory).CreateAsync(Ct);
        var workoutOnly = await TestActors.Client(factory).CreateAsync(Ct);
        var mealOnly = await TestActors.Client(factory).CreateAsync(Ct);
        var logDateOnly = await TestActors.Client(factory).CreateAsync(Ct);
        var everything = await TestActors.Client(factory).CreateAsync(Ct);
        var nothing = await TestActors.Client(factory).CreateAsync(Ct);

        var measuredAt = HoursAgo(30);
        var workoutAt = HoursAgo(20);
        var eatenAt = HoursAgo(10);
        var logDate = HoursAgo(5);

        await AutomationTestData.AddMeasurementAsync(factory, measuredOnly.ProfileId, measuredAt);
        await mongo.SessionExecutions.InsertOneAsync(
            WorkoutLogTestHelpers.CreateLog(clientId: workoutOnly.UserId, isCompleted: true, startedAt: workoutAt), cancellationToken: Ct);

        // A newer log without EatenAt must not beat the eaten one: EatenAt wins when the client has any.
        await mongo.MealLogs.InsertManyAsync(
        [
            new MealLog { ClientId = mealOnly.UserId, LogDate = HoursAgo(40), EatenAt = eatenAt },
            new MealLog { ClientId = mealOnly.UserId, LogDate = logDate },
        ], cancellationToken: Ct);
        await mongo.MealLogs.InsertOneAsync(new MealLog { ClientId = logDateOnly.UserId, LogDate = logDate }, cancellationToken: Ct);

        await AutomationTestData.AddMeasurementAsync(factory, everything.ProfileId, HoursAgo(1));
        await mongo.SessionExecutions.InsertOneAsync(
            WorkoutLogTestHelpers.CreateLog(clientId: everything.UserId, isCompleted: true, startedAt: workoutAt), cancellationToken: Ct);
        await mongo.MealLogs.InsertOneAsync(new MealLog { ClientId = everything.UserId, LogDate = logDate, EatenAt = eatenAt }, cancellationToken: Ct);

        var clients = new[] { measuredOnly, workoutOnly, mealOnly, logDateOnly, everything, nothing }
            .Select(client => (client.UserId, client.ProfileId))
            .ToList();

        var result = await service.GetLastActiveAtAsync(clients, Ct);

        result.Should().HaveCount(6);
        result[measuredOnly.UserId].Should().BeCloseTo(measuredAt, TimeSpan.FromSeconds(1));
        result[workoutOnly.UserId].Should().BeCloseTo(workoutAt, TimeSpan.FromSeconds(1));
        result[mealOnly.UserId].Should().BeCloseTo(eatenAt, TimeSpan.FromSeconds(1));
        result[logDateOnly.UserId].Should().BeCloseTo(logDate, TimeSpan.FromSeconds(1));
        result[everything.UserId].Should().BeCloseTo(HoursAgo(1), TimeSpan.FromSeconds(2));
        result[nothing.UserId].Should().BeNull();

        foreach (var client in clients)
        {
            var single = await service.GetLastActiveAtAsync(client.UserId, client.ProfileId, Ct);

            single.Should().Be(result[client.UserId], "the single-client overload shares the batch definition");
        }
    }

    [Fact]
    public async Task GetLastActiveAtAsync_Batch_EmptyInputReturnsEmpty()
    {
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IClientVerdictService>();

        (await service.GetLastActiveAtAsync([], Ct)).Should().BeEmpty();
    }
}
