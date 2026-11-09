using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Builders;
using FormUp.Tests.Endpoints.NutritionPlans;
using FormUp.Tests.Endpoints.TrainingPlans;
using FormUp.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.CoachSubscriptions;

/// <summary>
/// Over-cap behaviour through the real pipeline: the two instantiate endpoints refuse an over-cap client, and the
/// client's own plan reads carry <c>runsUntil</c> only for an over-cap client.
/// </summary>
[Collection(TestCollection.Name)]
public class OverCapIntegrationTests(FitnessApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record LapsedWorld(Actor Coach, Actor KeptClient, Actor OverCapClient);

    /// <summary>
    /// A coach whose paid plan was canceled with two clients the Free plan keeps (linked 30 and 20 days ago) and a third,
    /// linked 10 days ago, that is over the cap of 2.
    /// </summary>
    private async Task<LapsedWorld> LapsedCoachAsync(UserRole role)
    {
        var coach = role == UserRole.Nutritionist
            ? await TestActors.Nutritionist(factory).CreateAsync(Ct)
            : await TestActors.Trainer(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, "pro", SubscriptionStatus.Canceled);

        var clients = new List<Actor>();

        foreach (var daysAgo in new[] { 30, 20, 10 })
        {
            var client = await TestActors.Client(factory).CreateAsync(Ct);
            await TestActors.Link(factory, coach, client)
                .AsRole(role)
                .WithActivatedAtUtc(DateTime.UtcNow.AddDays(-daysAgo))
                .CreateAsync(Ct);
            clients.Add(client);
        }

        return new LapsedWorld(coach, clients[0], clients[2]);
    }

    private static DateTime ThisMonday()
    {
        var today = DateTime.UtcNow.Date;

        return today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
    }

    private async Task<Guid> SeedNutritionTemplateAsync(Guid ownerId)
    {
        var template = new NutritionPlanTemplate
        {
            ExternalId = Guid.NewGuid(),
            OwnerId = ownerId,
            Name = "Over-cap template",
            Visibility = LibraryVisibility.Private,
            Version = 1,
            DateCreated = DateTime.UtcNow,
            WeekCount = 1,
            Weeks = [new TemplateWeek { WeekNumber = 1, Days = [] }],
        };

        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IMongoContext>()
            .NutritionPlanTemplates.InsertOneAsync(template, cancellationToken: Ct);

        return template.ExternalId;
    }

    private async Task<Guid> SeedTrainingTemplateAsync(Guid ownerId)
    {
        var template = new TrainingPlanTemplateBuilder()
            .WithOwnerId(ownerId)
            .WithVisibility(LibraryVisibility.Private)
            .Build();

        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IMongoContext>()
            .TrainingPlanTemplates.InsertOneAsync(template, cancellationToken: Ct);

        return template.ExternalId;
    }

    private static async Task AssertOverCapAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.ClientOverPlanLimit);
    }

    [Fact]
    public async Task InstantiateNutritionTemplate_OverCapClient_Returns403ButAKeptClientStillWorks()
    {
        var world = await LapsedCoachAsync(UserRole.Nutritionist);
        var templateId = await SeedNutritionTemplateAsync(world.Coach.UserId);

        var refused = await world.Coach.Http.PostAsJsonAsync(
            $"/nutrition/plan-templates/{templateId}/instantiate", new { ClientId = world.OverCapClient.PublicId, Name = "Plan" }, Ct);
        var allowed = await world.Coach.Http.PostAsJsonAsync(
            $"/nutrition/plan-templates/{templateId}/instantiate", new { ClientId = world.KeptClient.PublicId, Name = "Plan" }, Ct);

        await AssertOverCapAsync(refused);
        allowed.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task InstantiateTrainingTemplate_OverCapClient_Returns403ButAKeptClientStillWorks()
    {
        var world = await LapsedCoachAsync(UserRole.Trainer);
        var templateId = await SeedTrainingTemplateAsync(world.Coach.UserId);

        var refused = await world.Coach.Http.PostAsJsonAsync(
            $"/training/plan-templates/{templateId}/instantiate", new { ClientId = world.OverCapClient.PublicId, Name = "Plan" }, Ct);
        var allowed = await world.Coach.Http.PostAsJsonAsync(
            $"/training/plan-templates/{templateId}/instantiate", new { ClientId = world.KeptClient.PublicId, Name = "Plan" }, Ct);

        await AssertOverCapAsync(refused);
        allowed.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    private async Task SeedRunningPlanAsync(Guid coachUserId, Guid clientUserId)
    {
        var plan = PlanTestHelpers.CreatePlan(
            nutritionistId: coachUserId, clientId: clientUserId, status: NutritionPlanStatus.Active);
        plan.StartDate = ThisMonday();
        plan.Weeks[0].Status = WeekStatus.Published;
        plan.Weeks[0].DatePublished = DateTime.UtcNow;

        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IMongoContext>()
            .NutritionPlans.InsertOneAsync(plan, cancellationToken: Ct);
    }

    private static async Task<JsonElement> GetFullPlanAsync(Actor client)
    {
        var response = await client.Http.GetAsync("/client/nutrition/plan/full", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
    }

    [Fact]
    public async Task GetFullPlan_OverCapClient_ReturnsTheEndOfTheLastPublishedWeekAsRunsUntil()
    {
        var world = await LapsedCoachAsync(UserRole.Nutritionist);
        await SeedRunningPlanAsync(world.Coach.UserId, world.OverCapClient.UserId);

        var plan = await GetFullPlanAsync(world.OverCapClient);

        plan.GetProperty("runsUntil").GetString().Should().Be(DateOnly.FromDateTime(ThisMonday().AddDays(6)).ToString("yyyy-MM-dd"));
    }

    /// <summary>Seeds a running, published plan of the right kind for the endpoint and returns the route that reads it.</summary>
    private async Task<string> SeedPlanForAsync(string endpoint, Guid coachUserId, Guid clientUserId)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        if (endpoint.StartsWith("training", StringComparison.Ordinal))
        {
            var training = TrainingPlanTestHelpers.CreatePlan(
                trainerId: coachUserId, clientId: clientUserId, status: TrainingPlanStatus.Active);
            training.StartDate = ThisMonday();
            training.Weeks[0].Status = WeekStatus.Published;
            training.Weeks[0].DatePublished = DateTime.UtcNow;
            await mongo.TrainingPlans.InsertOneAsync(training, cancellationToken: Ct);

            return endpoint == "training-full"
                ? $"/client/training/plans/{training.ExternalId}"
                : "/client/training/plan/today";
        }

        var nutrition = PlanTestHelpers.CreatePlan(
            nutritionistId: coachUserId, clientId: clientUserId, status: NutritionPlanStatus.Active);
        nutrition.StartDate = ThisMonday();
        nutrition.Weeks[0].Status = WeekStatus.Published;
        nutrition.Weeks[0].DatePublished = DateTime.UtcNow;
        await mongo.NutritionPlans.InsertOneAsync(nutrition, cancellationToken: Ct);

        return endpoint switch
        {
            "nutrition-today" => "/client/nutrition/plan/today",
            "nutrition-week" => "/client/nutrition/plan/week",
            _ => $"/client/nutrition/plans/{nutrition.ExternalId}",
        };
    }

    [Theory]
    [InlineData("nutrition-today", true)]
    [InlineData("nutrition-today", false)]
    [InlineData("nutrition-week", true)]
    [InlineData("nutrition-week", false)]
    [InlineData("nutrition-by-id", true)]
    [InlineData("nutrition-by-id", false)]
    [InlineData("training-full", true)]
    [InlineData("training-full", false)]
    [InlineData("training-today", true)]
    [InlineData("training-today", false)]
    public async Task ClientPlanRead_FillsRunsUntilOnlyForAnOverCapClient(string endpoint, bool overCap)
    {
        var world = await LapsedCoachAsync(endpoint.StartsWith("training", StringComparison.Ordinal) ? UserRole.Trainer : UserRole.Nutritionist);
        var client = overCap ? world.OverCapClient : world.KeptClient;
        var route = await SeedPlanForAsync(endpoint, world.Coach.UserId, client.UserId);

        var response = await client.Http.GetAsync(route, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var runsUntil = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("runsUntil");

        if (overCap)
        {
            runsUntil.GetString().Should().Be(DateOnly.FromDateTime(ThisMonday().AddDays(6)).ToString("yyyy-MM-dd"));
        }
        else
        {
            runsUntil.ValueKind.Should().Be(JsonValueKind.Null);
        }
    }

    [Fact]
    public async Task GetFullPlan_KeptClient_HasNoRunsUntil()
    {
        var world = await LapsedCoachAsync(UserRole.Nutritionist);
        await SeedRunningPlanAsync(world.Coach.UserId, world.KeptClient.UserId);

        var plan = await GetFullPlanAsync(world.KeptClient);

        plan.GetProperty("runsUntil").ValueKind.Should().Be(JsonValueKind.Null);
    }
}
