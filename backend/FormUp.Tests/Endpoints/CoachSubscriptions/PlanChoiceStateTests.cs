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
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Builders;
using FormUp.Tests.Endpoints.NutritionPlans;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
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

    private const string KeepClientsRoute = "/users/me/subscription/keep-clients";

    private static bool ChoiceRequired(JsonElement subscription) =>
        subscription.GetProperty("keepClientsChoiceRequired").GetBoolean();

    private static Task<HttpResponseMessage> PutKeepAsync(Actor coach, params Guid[] clientIds) =>
        coach.Http.PutAsJsonAsync(KeepClientsRoute, new { ClientIds = clientIds }, Ct);

    /// <summary>A coach on a paid plan limited to two clients, with three live clients linked 30, 20 and 10 days ago.</summary>
    private async Task<(Actor Coach, List<Actor> Clients, string PlanCode)> SmallPlanCoachAsync(
        int? maxActiveClients = 2, UserRole role = UserRole.Trainer)
    {
        var coach = role == UserRole.Trainer
            ? await TestActors.Trainer(factory).CreateAsync(Ct)
            : await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var planCode = await PaidPlanBuilder.CreateAsync(factory, maxActiveClients: maxActiveClients);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, planCode);
        var clients = await TestActors.LinkClientsAsync(factory, coach, role, 30, 20, 10);

        return (coach, clients, planCode);
    }

    [Fact]
    public async Task GetMySubscription_SmallerPaidPlanOverCap_ChoiceRequiredUntilTheClientsAreKept()
    {
        var (coach, clients, _) = await SmallPlanCoachAsync();

        var before = await GetSubscriptionAsync(coach);

        ChoiceRequired(before).Should().BeTrue();
        before.GetProperty("planChoiceRequired").GetBoolean().Should().BeFalse();
        before.GetProperty("isLapsed").GetBoolean().Should().BeFalse();
        before.GetProperty("clientLimit").GetInt32().Should().Be(2);
        before.GetProperty("overCapClientCount").GetInt32().Should().Be(1);
        before.GetProperty("activeClientCount").GetInt32().Should().Be(2);

        var picker = await coach.Http.GetAsync(KeepClientsRoute, Ct);
        picker.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await picker.Content.ReadFromJsonAsync<GetKeepClientsResponse>(JsonOptions, Ct))!;
        body.Cap.Should().Be(2);
        body.Clients.Where(client => client.IsKept).Select(client => client.ClientId).Should()
            .BeEquivalentTo([clients[0].PublicId, clients[1].PublicId]);

        (await PutKeepAsync(coach, clients[1].PublicId, clients[2].PublicId)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var after = await GetSubscriptionAsync(coach);

        ChoiceRequired(after).Should().BeFalse();
        after.GetProperty("overCapClientCount").GetInt32().Should().Be(1, "the released client stays counted, as after a lapse");
        after.GetProperty("activeClientCount").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task KeepClients_SmallerPaidPlanOverCap_RejectsAWrongCountAndAForeignClient()
    {
        var (coach, clients, _) = await SmallPlanCoachAsync();
        var (_, otherClients, _) = await SmallPlanCoachAsync();

        var wrongCount = await PutKeepAsync(coach, clients[0].PublicId);
        var foreign = await PutKeepAsync(coach, clients[0].PublicId, otherClients[0].PublicId);

        wrongCount.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await wrongCount.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.KeepClientsCountInvalid);
        foreign.StatusCode.Should().Be(HttpStatusCode.NotFound);
        ChoiceRequired(await GetSubscriptionAsync(coach)).Should().BeTrue("nothing was saved");
    }

    [Fact]
    public async Task KeepClients_SmallerPaidPlanWithinItsLimit_Returns409AndNoChoiceIsRequired()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var planCode = await PaidPlanBuilder.CreateAsync(factory, maxActiveClients: 3);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, planCode);
        await TestActors.LinkClientsAsync(factory, coach, UserRole.Trainer, 30, 20, 10);

        var get = await coach.Http.GetAsync(KeepClientsRoute, Ct);
        var put = await PutKeepAsync(coach);
        var subscription = await GetSubscriptionAsync(coach);

        get.StatusCode.Should().Be(HttpStatusCode.Conflict);
        put.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await put.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.SubscriptionNotLapsed);
        ChoiceRequired(subscription).Should().BeFalse();
        subscription.GetProperty("overCapClientCount").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task GetMySubscription_UnlimitedPlan_IsNeverOverCap()
    {
        var (coach, _, _) = await SmallPlanCoachAsync(maxActiveClients: null);

        var subscription = await GetSubscriptionAsync(coach);

        ChoiceRequired(subscription).Should().BeFalse();
        subscription.GetProperty("overCapClientCount").GetInt32().Should().Be(0);
        subscription.GetProperty("clientLimit").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task GetMySubscription_LapseAndExplicitFreeOverCap_ChoiceRequiredUntilSaved()
    {
        var lapsed = await TestActors.Trainer(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, lapsed, "pro", SubscriptionStatus.Canceled);
        var lapsedClients = await TestActors.LinkClientsAsync(factory, lapsed, UserRole.Trainer, 30, 20, 10);
        var free = await TestActors.Trainer(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, free, SubscriptionPlanCodes.Free);
        var freeClients = await TestActors.LinkClientsAsync(factory, free, UserRole.Trainer, 30, 20, 10);

        ChoiceRequired(await GetSubscriptionAsync(lapsed)).Should().BeTrue();
        ChoiceRequired(await GetSubscriptionAsync(free)).Should().BeTrue();

        (await PutKeepAsync(lapsed, lapsedClients[0].PublicId, lapsedClients[2].PublicId)).StatusCode
            .Should().Be(HttpStatusCode.NoContent);
        (await PutKeepAsync(free, freeClients[0].PublicId, freeClients[2].PublicId)).StatusCode
            .Should().Be(HttpStatusCode.NoContent);

        ChoiceRequired(await GetSubscriptionAsync(lapsed)).Should().BeFalse();
        ChoiceRequired(await GetSubscriptionAsync(free)).Should().BeFalse();
    }

    [Fact]
    public async Task GetMySubscription_WithinTheLimitOfThePlanInForce_NoChoiceRequired()
    {
        var onFree = await TestActors.Trainer(factory).CreateAsync(Ct);
        await TestActors.LinkClientsAsync(factory, onFree, UserRole.Trainer, 30, 20);
        var onPaid = await TestActors.Trainer(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, onPaid, await PaidPlanBuilder.CreateAsync(factory, maxActiveClients: 3));
        await TestActors.LinkClientsAsync(factory, onPaid, UserRole.Trainer, 30, 20, 10);

        foreach (var coach in new[] { onFree, onPaid })
        {
            var subscription = await GetSubscriptionAsync(coach);

            ChoiceRequired(subscription).Should().BeFalse();
            subscription.GetProperty("overCapClientCount").GetInt32().Should().Be(0);
        }
    }

    [Fact]
    public async Task GetMySubscription_EndedGiftWaitingForAPlanChoice_NeverAlsoRequiresAKeepChoice()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeWithEndedGiftAsync(factory, coach, "pro", endedDaysAgo: 2);
        await TestActors.LinkClientsAsync(factory, coach, UserRole.Trainer, 30, 20, 10);

        var subscription = await GetSubscriptionAsync(coach);

        subscription.GetProperty("planChoiceRequired").GetBoolean().Should().BeTrue();
        ChoiceRequired(subscription).Should().BeFalse();
    }

    [Fact]
    public async Task GetMySubscription_AdminChangesToASmallerPlan_TheChoiceIsRequiredAgainAfterEachChange()
    {
        var (coach, clients, _) = await SmallPlanCoachAsync();
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        (await PutKeepAsync(coach, clients[1].PublicId, clients[2].PublicId)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        ChoiceRequired(await GetSubscriptionAsync(coach)).Should().BeFalse();

        var smaller = await PaidPlanBuilder.CreateAsync(factory, maxActiveClients: 1);
        var change = await admin.Http.PostAsJsonAsync(
            $"/admin/coaches/{coach.UserId}/give-free", new { PlanCode = smaller, FreeUntil = (DateTimeOffset?)null, Reason = "downgrade" }, Ct);

        change.StatusCode.Should().Be(HttpStatusCode.OK);
        var subscription = await GetSubscriptionAsync(coach);
        ChoiceRequired(subscription).Should().BeTrue("the plan change reset the earlier decision");
        subscription.GetProperty("clientLimit").GetInt32().Should().Be(1);
        subscription.GetProperty("overCapClientCount").GetInt32().Should().Be(2);

        (await PutKeepAsync(coach, clients[0].PublicId)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        ChoiceRequired(await GetSubscriptionAsync(coach)).Should().BeFalse();
    }

    [Fact]
    public async Task OverCap_ReleasedClientOnASmallerPaidPlan_IsRestrictedLikeAfterALapse()
    {
        var (coach, clients, planCode) = await SmallPlanCoachAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.SubscriptionPlans.SingleAsync(p => p.Code == planCode, Ct)).CanCreatePlans = true;
            await db.SaveChangesAsync(Ct);
        }

        var templateId = await SeedTrainingTemplateAsync(coach.UserId);
        (await PutKeepAsync(coach, clients[0].PublicId, clients[1].PublicId)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var refused = await InstantiateAsync(coach, templateId, clients[2]);
        var allowed = await InstantiateAsync(coach, templateId, clients[0]);

        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await refused.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.ClientOverPlanLimit);
        allowed.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task OverCap_ReleasedClientOnASmallerPaidPlanWithAPublishedWeek_RunsUntilItsEnd()
    {
        var (coach, clients, _) = await SmallPlanCoachAsync(role: UserRole.Nutritionist);
        var today = DateTime.UtcNow.Date;
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var plan = PlanTestHelpers.CreatePlan(
            nutritionistId: coach.UserId, clientId: clients[2].UserId, status: NutritionPlanStatus.Active);
        plan.StartDate = monday;
        plan.Weeks[0].Status = WeekStatus.Published;
        plan.Weeks[0].DatePublished = DateTime.UtcNow;
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IMongoContext>().NutritionPlans.InsertOneAsync(plan, cancellationToken: Ct);
        }

        (await PutKeepAsync(coach, clients[0].PublicId, clients[1].PublicId)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var read = await clients[2].Http.GetAsync("/client/nutrition/plan/full", Ct);

        read.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonDocument.Parse(await read.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("runsUntil").ValueKind
            .Should().NotBe(JsonValueKind.Null);
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
