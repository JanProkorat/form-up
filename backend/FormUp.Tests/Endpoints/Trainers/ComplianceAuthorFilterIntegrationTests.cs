using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.Trainers.GetClientProgress;
using FormUp.Application.Features.Trainers.GetDashboardSummary;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Builders;
using FormUp.Tests.Endpoints.NutritionPlans;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;

namespace FormUp.Tests.Endpoints.Trainers;

/// <summary>
/// A professional's compliance percentage and streak come only from plans that professional
/// wrote; another coach's plan for the same client must not move them. Real Mongo, because the
/// mock harness ignores filters.
/// </summary>
[Collection(TestCollection.Shard3)]
public class ComplianceAuthorFilterIntegrationTests(FitnessApiFactory factory)
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

    /// <summary>Seeds an Active plan with a published first week and the given number of meals planned for today.</summary>
    private async Task SeedPlanWithMealsTodayAsync(Guid clientUserId, Guid authorUserId, int mealsToday)
    {
        var today = DateTime.UtcNow.Date;
        var dayOfWeek = today.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)today.DayOfWeek;
        var monday = today.AddDays(-(dayOfWeek - 1));

        var plan = PlanTestHelpers.CreatePlan(
            clientId: clientUserId,
            nutritionistId: authorUserId,
            status: NutritionPlanStatus.Active,
            weekCount: 1);
        plan.Id = ObjectId.GenerateNewId();
        plan.StartDate = monday;
        plan.Weeks[0].Status = WeekStatus.Published;
        plan.Weeks[0].DatePublished = monday;
        plan.Weeks[0].Days[dayOfWeek - 1].Meals =
            Enumerable.Range(1, mealsToday).Select(order => PlanTestHelpers.CreateMeal(order: order)).ToList();

        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        await mongo.NutritionPlans.InsertOneAsync(plan, cancellationToken: Ct);
    }

    private async Task LogMealTodayAsync(Guid clientUserId)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        await mongo.MealLogs.InsertOneAsync(new MealLog
        {
            Id = ObjectId.GenerateNewId(),
            ClientId = clientUserId,
            PlanId = Guid.NewGuid(),
            MealId = Guid.NewGuid(),
            EatenAt = DateTime.UtcNow,
            FoodsEaten = [],
        }, cancellationToken: Ct);
    }

    private async Task<ClientDashboardItem> SummaryItemAsync(Actor trainer, Actor client)
    {
        var response = await trainer.Http.GetAsync("/trainer/dashboard-summary", Ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<GetDashboardSummaryResponse>(JsonOptions, Ct);
        return body!.Clients.Single(c => c.PublicId == client.PublicId);
    }

    private async Task<GetClientProgressResponse> ProgressAsync(Actor trainer, Actor client)
    {
        var response = await trainer.Http.GetAsync($"/trainer/clients/{client.PublicId}/progress", Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<GetClientProgressResponse>(JsonOptions, Ct))!;
    }

    [Fact]
    public async Task Compliance_OnlyAnotherCoachsPlan_IsZeroForTheCaller()
    {
        var (trainer, client) = await LinkedPairAsync();
        await SeedPlanWithMealsTodayAsync(client.UserId, Guid.NewGuid(), mealsToday: 1);
        await LogMealTodayAsync(client.UserId);

        var summary = await SummaryItemAsync(trainer, client);
        var progress = await ProgressAsync(trainer, client);

        summary.CompliancePercent.Should().Be(0m);
        summary.CurrentStreak.Should().Be(0);
        progress.CompliancePercent.Should().Be(0m);
        progress.CurrentStreak.Should().Be(0);
    }

    [Fact]
    public async Task Compliance_CallersOwnPlan_CountsLoggedMealAndStreak()
    {
        var (trainer, client) = await LinkedPairAsync();
        await SeedPlanWithMealsTodayAsync(client.UserId, trainer.UserId, mealsToday: 1);
        await LogMealTodayAsync(client.UserId);

        var summary = await SummaryItemAsync(trainer, client);
        var progress = await ProgressAsync(trainer, client);

        summary.CompliancePercent.Should().Be(100m);
        summary.CurrentStreak.Should().Be(1);
        progress.CompliancePercent.Should().Be(100m);
        progress.CurrentStreak.Should().Be(1);
    }

    [Fact]
    public async Task Compliance_AnotherCoachsLargerPlanAlongsideOwn_DoesNotDiluteCallersFigure()
    {
        var (trainer, client) = await LinkedPairAsync();
        await SeedPlanWithMealsTodayAsync(client.UserId, Guid.NewGuid(), mealsToday: 4);
        await SeedPlanWithMealsTodayAsync(client.UserId, trainer.UserId, mealsToday: 1);
        await LogMealTodayAsync(client.UserId);

        var summary = await SummaryItemAsync(trainer, client);

        summary.CompliancePercent.Should().Be(100m);
        summary.CurrentStreak.Should().Be(1);
    }
}
