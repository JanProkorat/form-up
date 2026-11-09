using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.CoachSubscriptions.GetKeepClients;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Builders;
using FormUp.Tests.Endpoints.NutritionPlans;
using FormUp.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.CoachSubscriptions;

/// <summary>
/// How a coach's standing resolves through the real pipeline: an ended given plan is a plan choice (never a lapse), a gift
/// in force or without an end is neither, and a Canceled, Incomplete or PastDue paid plan keeps its own behaviour.
/// </summary>
[Collection(TestCollection.Name)]
public class PlanChoiceStateTests(FitnessApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> GetSubscriptionAsync(Actor coach)
    {
        var response = await coach.Http.GetAsync("/users/me/subscription", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement;
    }

    [Fact]
    public async Task GetMySubscription_EndedGift_IsAPlanChoiceNeverALapse()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeWithEndedGiftAsync(factory, coach, "pro", endedDaysAgo: 2);
        await TestActors.LinkClientsAsync(factory, coach, UserRole.Trainer, 30, 20, 10);

        var subscription = await GetSubscriptionAsync(coach);

        subscription.GetProperty("planChoiceRequired").GetBoolean().Should().BeTrue();
        subscription.GetProperty("isLapsed").GetBoolean().Should().BeFalse();
        subscription.GetProperty("planCode").GetString().Should().Be("pro");
        subscription.GetProperty("endedPlanNameEn").GetString().Should().Be(subscription.GetProperty("nameEn").GetString());
        subscription.GetProperty("endedPlanNameCs").GetString().Should().NotBeNullOrEmpty();
        subscription.GetProperty("endedPlanNameDe").GetString().Should().NotBeNullOrEmpty();
        subscription.GetProperty("freeUntil").GetDateTimeOffset().Should().BeBefore(DateTimeOffset.UtcNow);
        subscription.GetProperty("overCapClientCount").GetInt32().Should().Be(0);
        subscription.GetProperty("activeClientCount").GetInt32().Should().Be(3);
        subscription.GetProperty("features").GetProperty("canCreatePlans").GetBoolean().Should().BeTrue(
            "reads keep the ended plan's flags");
    }

    [Theory]
    [InlineData("gift-in-force")]
    [InlineData("gift-without-end")]
    [InlineData("paid-past-due")]
    [InlineData("no-subscription")]
    public async Task GetMySubscription_NotAPlanChoiceAndNotLapsed(string situation)
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var now = DateTimeOffset.UtcNow;

        switch (situation)
        {
            case "gift-in-force":
                await CoachSubscriptionBuilder.SubscribeToPlanAsync(
                    factory, coach, "pro", freeUntil: now.AddDays(3), givenFreeAt: now.AddDays(-3));
                break;
            case "gift-without-end":
                await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, "pro", givenFreeAt: now.AddDays(-3));
                break;
            case "paid-past-due":
                await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, "pro", SubscriptionStatus.PastDue);
                break;
        }

        var subscription = await GetSubscriptionAsync(coach);

        subscription.GetProperty("planChoiceRequired").GetBoolean().Should().BeFalse();
        subscription.GetProperty("isLapsed").GetBoolean().Should().BeFalse();
        subscription.GetProperty("endedPlanNameEn").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Theory]
    [InlineData(SubscriptionStatus.Canceled)]
    [InlineData(SubscriptionStatus.Incomplete)]
    public async Task GetMySubscription_CanceledOrIncompletePaidPlan_StaysALapse(SubscriptionStatus status)
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, "pro", status);
        await TestActors.LinkClientsAsync(factory, coach, UserRole.Trainer, 30, 20, 10);

        var subscription = await GetSubscriptionAsync(coach);

        subscription.GetProperty("isLapsed").GetBoolean().Should().BeTrue();
        subscription.GetProperty("planChoiceRequired").GetBoolean().Should().BeFalse();
        subscription.GetProperty("overCapClientCount").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task GetKeepClients_PaidPlanPastDue_Returns409NotLapsed()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, "pro", SubscriptionStatus.PastDue);

        var response = await coach.Http.GetAsync("/users/me/subscription/keep-clients", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.SubscriptionNotLapsed);
    }

    [Fact]
    public async Task GetKeepClients_EndedGift_AnswersWithTheRowsAndTheFreeCap()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeWithEndedGiftAsync(factory, coach);
        var clients = await TestActors.LinkClientsAsync(factory, coach, UserRole.Trainer, 30, 20, 10);

        var response = await coach.Http.GetAsync("/users/me/subscription/keep-clients", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<GetKeepClientsResponse>(JsonOptions, Ct))!;
        body.Cap.Should().Be(2);
        body.Clients.Select(client => client.ClientId).Should().BeEquivalentTo(clients.Select(client => client.PublicId));
        body.Clients.Where(client => client.IsKept).Select(client => client.ClientId).Should()
            .BeEquivalentTo([clients[0].PublicId, clients[1].PublicId]);
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

    private Task<HttpResponseMessage> InstantiateAsync(Actor coach, Guid templateId, Actor client) =>
        coach.Http.PostAsJsonAsync(
            $"/training/plan-templates/{templateId}/instantiate", new { ClientId = client.PublicId, Name = "Plan" }, Ct);

    [Fact]
    public async Task OverCap_WhileAPlanChoiceIsRequired_IsOffForEveryClient()
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeWithEndedGiftAsync(factory, coach);
        var clients = await TestActors.LinkClientsAsync(factory, coach, UserRole.Nutritionist, 30, 20, 10);
        var today = DateTime.UtcNow.Date;
        var plan = PlanTestHelpers.CreatePlan(
            nutritionistId: coach.UserId, clientId: clients[2].UserId, status: NutritionPlanStatus.Active);
        plan.StartDate = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        plan.Weeks[0].Status = WeekStatus.Published;
        plan.Weeks[0].DatePublished = DateTime.UtcNow;

        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IMongoContext>().NutritionPlans.InsertOneAsync(plan, cancellationToken: Ct);
        var entitlements = scope.ServiceProvider.GetRequiredService<EntitlementService>();

        foreach (var client in clients)
        {
            (await entitlements.IsClientOverCapAsync(coach.UserId, client.UserId, Ct)).Should().BeFalse();
        }

        var read = await clients[2].Http.GetAsync("/client/nutrition/plan/full", Ct);

        read.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonDocument.Parse(await read.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("runsUntil").ValueKind
            .Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task OverCap_CoachOnAnExplicitFreePlanWithTooManyClients_BehavesLikeALapse()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, SubscriptionPlanCodes.Free);
        var clients = await TestActors.LinkClientsAsync(factory, coach, UserRole.Trainer, 30, 20, 10);
        var templateId = await SeedTrainingTemplateAsync(coach.UserId);

        var refused = await InstantiateAsync(coach, templateId, clients[2]);
        var allowed = await InstantiateAsync(coach, templateId, clients[0]);
        var subscription = await GetSubscriptionAsync(coach);

        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await refused.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.ClientOverPlanLimit);
        allowed.StatusCode.Should().Be(HttpStatusCode.Created);
        subscription.GetProperty("isLapsed").GetBoolean().Should().BeFalse();
        subscription.GetProperty("planChoiceRequired").GetBoolean().Should().BeFalse();
        subscription.GetProperty("overCapClientCount").GetInt32().Should().Be(1);
        subscription.GetProperty("activeClientCount").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task OverCap_CoachOnAnExplicitFreePlanWithinTheCap_IsUnaffected()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, SubscriptionPlanCodes.Free);
        var clients = await TestActors.LinkClientsAsync(factory, coach, UserRole.Trainer, 30, 20);
        var templateId = await SeedTrainingTemplateAsync(coach.UserId);

        var response = await InstantiateAsync(coach, templateId, clients[1]);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await GetSubscriptionAsync(coach)).GetProperty("overCapClientCount").GetInt32().Should().Be(0);
    }
}
