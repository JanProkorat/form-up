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

    [Fact]
    public async Task GetFullPlan_KeptClient_HasNoRunsUntil()
    {
        var world = await LapsedCoachAsync(UserRole.Nutritionist);
        await SeedRunningPlanAsync(world.Coach.UserId, world.KeptClient.UserId);

        var plan = await GetFullPlanAsync(world.KeptClient);

        plan.GetProperty("runsUntil").ValueKind.Should().Be(JsonValueKind.Null);
    }
}
