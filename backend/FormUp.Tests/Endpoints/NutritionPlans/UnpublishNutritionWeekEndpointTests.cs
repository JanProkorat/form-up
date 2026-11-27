using System.Net;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace FormUp.Tests.Endpoints.NutritionPlans;

/// <summary>
/// Integration tests for unpublishing a nutrition plan week.
/// </summary>
[Collection(TestCollection.Name)]
public class UnpublishNutritionWeekEndpointTests(FitnessApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTime FutureStart = DateTime.UtcNow.Date.AddDays(14);

    private async Task<(Actor Nutritionist, Actor Client)> CreateLinkedPairAsync(Action<LinkBuilder>? configureLink = null)
    {
        var nutritionist = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var link = TestActors.Link(factory, nutritionist, client).AsRole(UserRole.Nutritionist);
        configureLink?.Invoke(link);
        await link.CreateAsync(Ct);
        return (nutritionist, client);
    }

    private async Task<Guid> SeedPlanAsync(
        Guid nutritionistId, Guid clientUserId, DateTime startDate, params WeekStatus[] weekStatuses)
    {
        var planId = Guid.NewGuid();
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        await mongo.NutritionPlans.InsertOneAsync(new NutritionPlan
        {
            ExternalId = planId,
            ClientId = clientUserId,
            NutritionistId = nutritionistId,
            Name = "Unpublish Plan",
            Status = NutritionPlanStatus.Active,
            StartDate = startDate,
            Version = 1,
            DateCreated = DateTime.UtcNow.AddDays(-1),
            Weeks = weekStatuses
                .Select((status, index) => new PlanWeek
                {
                    WeekNumber = index + 1,
                    Status = status,
                    DatePublished = status == WeekStatus.Published ? DateTime.UtcNow.AddDays(-1) : null,
                })
                .ToList(),
        }, cancellationToken: Ct);
        return planId;
    }

    private async Task<NutritionPlan> LoadPlanAsync(Guid planId)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        return await mongo.NutritionPlans.Find(p => p.ExternalId == planId).SingleAsync(Ct);
    }

    private Task<HttpResponseMessage> UnpublishAsync(Actor nutritionist, Guid planId, int weekNumber) =>
        nutritionist.Http.PostAsync(
            $"/nutrition/plans/{planId}/weeks/{weekNumber}/unpublish",
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"), Ct);

    private int NotificationCallCount(Guid clientUserId, string eventType) =>
        factory.Services.GetRequiredService<FakeRealtimeNotifier>().Calls
            .Count(c => c.UserId == clientUserId && c.EventType == eventType);

    [Fact]
    public async Task Unpublish_OtherWeekStillPublished_DraftsWeekKeepsPlanActiveAndNotifiesClient()
    {
        var (nutritionist, client) = await CreateLinkedPairAsync();
        var planId = await SeedPlanAsync(
            nutritionist.UserId, client.UserId, FutureStart, WeekStatus.Published, WeekStatus.Published);

        var response = await UnpublishAsync(nutritionist, planId, 2);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var plan = await LoadPlanAsync(planId);
        plan.Status.Should().Be(NutritionPlanStatus.Active);
        plan.Version.Should().Be(2);
        plan.Weeks.Single(w => w.WeekNumber == 2).Status.Should().Be(WeekStatus.Draft);
        plan.Weeks.Single(w => w.WeekNumber == 2).DatePublished.Should().BeNull();
        plan.Weeks.Single(w => w.WeekNumber == 1).Status.Should().Be(WeekStatus.Published);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var notification = await db.Notifications.AsNoTracking()
            .SingleAsync(n => n.RecipientUserId == client.UserId, Ct);
        notification.Type.Should().Be(NotificationType.PlanPublished);
        notification.Body.Should().Be("Week 2 of your nutrition plan is no longer published.");
        NotificationCallCount(client.UserId, "nutritionplanupdated").Should().Be(1);
        NotificationCallCount(client.UserId, "newnotification").Should().Be(1);
    }

    [Fact]
    public async Task Unpublish_LastPublishedWeek_SetsPlanToDraft()
    {
        var (nutritionist, client) = await CreateLinkedPairAsync();
        var planId = await SeedPlanAsync(
            nutritionist.UserId, client.UserId, FutureStart, WeekStatus.Published, WeekStatus.Draft);

        var response = await UnpublishAsync(nutritionist, planId, 1);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        (await LoadPlanAsync(planId)).Status.Should().Be(NutritionPlanStatus.Draft);
    }

    [Fact]
    public async Task Unpublish_WeekWithMealLog_Returns409AndChangesNothing()
    {
        var (nutritionist, client) = await CreateLinkedPairAsync();
        var planId = await SeedPlanAsync(nutritionist.UserId, client.UserId, FutureStart, WeekStatus.Published);

        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
            await mongo.MealLogs.InsertOneAsync(new MealLog
            {
                ClientId = client.UserId,
                PlanId = planId,
                MealId = Guid.NewGuid(),
                LogDate = FutureStart.AddDays(2),
                Note = "logged",
            }, cancellationToken: Ct);
        }

        var response = await UnpublishAsync(nutritionist, planId, 1);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("WEEK_HAS_MEAL_LOGS");
        var plan = await LoadPlanAsync(planId);
        plan.Version.Should().Be(1);
        plan.Weeks.Single().Status.Should().Be(WeekStatus.Published);
        NotificationCallCount(client.UserId, "nutritionplanupdated").Should().Be(0);
    }

    [Fact]
    public async Task Unpublish_WeekAlreadyStarted_Returns409AndChangesNothing()
    {
        var (nutritionist, client) = await CreateLinkedPairAsync();
        var planId = await SeedPlanAsync(
            nutritionist.UserId, client.UserId, DateTime.UtcNow.Date, WeekStatus.Published);

        var response = await UnpublishAsync(nutritionist, planId, 1);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("WEEK_ALREADY_STARTED");
        (await LoadPlanAsync(planId)).Weeks.Single().Status.Should().Be(WeekStatus.Published);
    }

    [Fact]
    public async Task Unpublish_PlanOfAnotherNutritionist_Returns404()
    {
        var (owner, client) = await CreateLinkedPairAsync();
        var other = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var planId = await SeedPlanAsync(owner.UserId, client.UserId, FutureStart, WeekStatus.Published);

        var response = await UnpublishAsync(other, planId, 1);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await LoadPlanAsync(planId)).Weeks.Single().Status.Should().Be(WeekStatus.Published);
    }

    [Fact]
    public async Task Unpublish_LinkWithoutNutritionCapability_Returns404()
    {
        var (nutritionist, client) = await CreateLinkedPairAsync(link => link.CanViewNutritionPlans(false));
        var planId = await SeedPlanAsync(nutritionist.UserId, client.UserId, FutureStart, WeekStatus.Published);

        var response = await UnpublishAsync(nutritionist, planId, 1);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await LoadPlanAsync(planId)).Weeks.Single().Status.Should().Be(WeekStatus.Published);
    }

    [Fact]
    public async Task Unpublish_DraftWeek_Returns400()
    {
        var (nutritionist, client) = await CreateLinkedPairAsync();
        var planId = await SeedPlanAsync(
            nutritionist.UserId, client.UserId, FutureStart, WeekStatus.Published, WeekStatus.Draft);

        var response = await UnpublishAsync(nutritionist, planId, 2);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
