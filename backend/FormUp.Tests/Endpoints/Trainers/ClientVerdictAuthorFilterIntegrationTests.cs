using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.Trainers.GetClientVerdict;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Builders;
using FormUp.Tests.Endpoints.NutritionPlans;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;

namespace FormUp.Tests.Endpoints.Trainers;

/// <summary>
/// A professional's client verdict is computed only from plans that professional wrote: another
/// coach's plan neither produces nor alters the caller's signals. Real Mongo, because the mock
/// harness ignores filters.
/// </summary>
[Collection(TestCollection.Shard4)]
public class ClientVerdictAuthorFilterIntegrationTests(FitnessApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private async Task<(Actor Trainer, Actor Client)> LinkedPairAsync()
    {
        var trainer = await TestActors.Trainer(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, trainer, client).CreateAsync(Ct);
        return (trainer, client);
    }

    private async Task SeedNutritionPlanAsync(Guid clientUserId, Guid authorUserId)
    {
        var plan = PlanTestHelpers.CreatePlan(
            clientId: clientUserId, nutritionistId: authorUserId, status: NutritionPlanStatus.Active, weekCount: 1);
        plan.Id = ObjectId.GenerateNewId();
        plan.StartDate = DateTime.UtcNow.Date.AddDays(-1);
        plan.Weeks[0].Status = WeekStatus.Published;
        plan.Weeks[0].DatePublished = DateTime.UtcNow.AddDays(-1);

        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        await mongo.NutritionPlans.InsertOneAsync(plan, cancellationToken: Ct);
    }

    private async Task SeedTrainingPlanAsync(Guid clientUserId, Guid authorUserId)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        await mongo.TrainingPlans.InsertOneAsync(new TrainingPlan
        {
            Id = ObjectId.GenerateNewId(),
            ExternalId = Guid.NewGuid(),
            ClientId = clientUserId,
            TrainerId = authorUserId,
            Name = "Verdict author filter training plan",
            Status = TrainingPlanStatus.Active,
            StartDate = DateTime.UtcNow.Date.AddDays(-1),
            Weeks = [new TrainingWeek { WeekNumber = 1, Status = WeekStatus.Published, DatePublished = DateTime.UtcNow.AddDays(-1), Days = [] }],
            Version = 1,
            DateCreated = DateTime.UtcNow,
        }, cancellationToken: Ct);
    }

    private async Task<GetClientVerdictResponse> VerdictAsync(Actor trainer, Actor client)
    {
        var response = await trainer.Http.GetAsync($"/trainer/clients/{client.PublicId}/verdict", Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<GetClientVerdictResponse>(JsonOptions, Ct))!;
    }

    [Fact]
    public async Task Verdict_OnlyAnotherCoachsPlans_ProducesNoPlanSignals()
    {
        var (trainer, client) = await LinkedPairAsync();
        await SeedNutritionPlanAsync(client.UserId, Guid.NewGuid());
        await SeedTrainingPlanAsync(client.UserId, Guid.NewGuid());

        var verdict = await VerdictAsync(trainer, client);

        verdict.CompliancePercent.Should().BeNull();
        verdict.TrainingFrequencyActual.Should().BeNull();
        verdict.TrainingFrequencyPrescribed.Should().BeNull();
    }

    [Fact]
    public async Task Verdict_CallersOwnPlans_ProduceThePlanSignals()
    {
        var (trainer, client) = await LinkedPairAsync();
        await SeedNutritionPlanAsync(client.UserId, trainer.UserId);
        await SeedTrainingPlanAsync(client.UserId, trainer.UserId);

        var verdict = await VerdictAsync(trainer, client);

        verdict.CompliancePercent.Should().NotBeNull();
        verdict.TrainingFrequencyActual.Should().NotBeNull();
        verdict.TrainingFrequencyPrescribed.Should().NotBeNull();
    }

    [Fact]
    public async Task Verdict_AnotherCoachsPlanAlongsideOwn_DoesNotChangeTheCallersSignals()
    {
        var (trainer, client) = await LinkedPairAsync();
        await SeedNutritionPlanAsync(client.UserId, trainer.UserId);
        var ownOnly = await VerdictAsync(trainer, client);

        await SeedNutritionPlanAsync(client.UserId, Guid.NewGuid());
        await SeedTrainingPlanAsync(client.UserId, Guid.NewGuid());
        var withForeign = await VerdictAsync(trainer, client);

        withForeign.Verdict.Should().Be(ownOnly.Verdict);
        withForeign.CompliancePercent.Should().Be(ownOnly.CompliancePercent);
        withForeign.TrainingFrequencyPrescribed.Should().BeNull("the foreign training plan must not appear");
    }
}
