using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.ClientNutrition.LogMealEaten;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Builders;
using FormUp.Tests.Endpoints.NutritionPlans;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace FormUp.Tests.Endpoints.ClientNutrition;

/// <summary>
/// Integration tests (real MongoDB and PostgreSQL) for offline-sync conflict detection on
/// <c>POST /client/nutrition/log/meals/{MealId}/eaten</c>, the coach notification it raises, and
/// the <c>PlanVersion</c> the client plan reads expose.
/// </summary>
[Collection(TestCollection.Shard4)]
public class OfflineSyncConflictIntegrationTests(FitnessApiFactory factory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static DateTime Today => DateTime.UtcNow.Date;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PlanMeal NewMeal(int? changedAtPlanVersion = null)
    {
        var meal = ClientNutritionFixtures.MealWithFoods(MealKind.Lunch, PlanTestHelpers.CreateMealFood(foodName: "Rice"));
        meal.ChangedAtPlanVersion = changedAtPlanVersion;
        return meal;
    }

    private async Task<NutritionPlan> InsertPlanAsync(
        Actor client, PlanMeal meal, Guid? nutritionistId = null, int version = 1,
        NutritionPlanStatus status = NutritionPlanStatus.Active, int startedDaysAgo = 5)
    {
        var plan = await ClientNutritionFixtures.InsertPlanAsync(
            factory, client.UserId, status, Today.AddDays(-startedDaysAgo), nutritionistId, null,
            ClientNutritionFixtures.Week(1, WeekStatus.Published, meal));

        if (version != 1)
        {
            using var scope = factory.Services.CreateScope();
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
            await mongo.NutritionPlans.UpdateOneAsync(
                p => p.ExternalId == plan.ExternalId, Builders<NutritionPlan>.Update.Set(p => p.Version, version), cancellationToken: Ct);
            plan.Version = version;
        }

        return plan;
    }

    private static Task<HttpResponseMessage> LogAsync(
        Actor client, Guid mealId, int? basedOnPlanVersion = null, Guid? clientRequestId = null,
        List<SeenMealItemRequest>? seenItems = null) =>
        client.Http.PostAsJsonAsync($"/client/nutrition/log/meals/{mealId}/eaten",
            new LogMealEatenRequest
            {
                MealId = mealId,
                BasedOnPlanVersion = basedOnPlanVersion,
                ClientRequestId = clientRequestId,
                SeenMealItems = seenItems,
            }, Ct);

    private static List<SeenMealItemRequest> SeenItems() =>
    [
        new()
        {
            ItemId = Guid.NewGuid(), Kind = SeenMealItemKind.Food, RefId = Guid.NewGuid(), Name = "Rice", Amount = 150m,
            Kcal = 195m, Protein = 4m, Carbs = 42m, Fat = 0.5m, Fibre = 1m,
        },
        new()
        {
            ItemId = Guid.NewGuid(), Kind = SeenMealItemKind.Recipe, RefId = Guid.NewGuid(), Name = "Chili", Amount = 1m,
            Kcal = 420m, Protein = 30m, Carbs = 40m, Fat = 12m, Fibre = 9m,
        },
    ];

    private static async Task<LogMealEatenResponse> ReadAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<LogMealEatenResponse>(Json, Ct))!;

    private async Task<List<OfflineSyncConflict>> LoadConflictsAsync(Guid clientId)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        return await mongo.OfflineSyncConflicts.Find(c => c.ClientId == clientId).ToListAsync(Ct);
    }

    private async Task<List<MealLog>> LoadMealLogsAsync(Guid clientId)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        return await mongo.MealLogs.Find(l => l.ClientId == clientId).ToListAsync(Ct);
    }

    private async Task<List<Notification>> LoadConflictNotificationsAsync(Guid recipientUserId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        return await db.Notifications
            .AsNoTracking()
            .Where(n => n.RecipientUserId == recipientUserId && n.Type == NotificationType.OfflineSyncConflict)
            .OrderBy(n => n.Id)
            .ToListAsync(Ct);
    }

    private async Task<(Actor Client, Actor Coach)> SeedLinkedPairAsync(string? coachLanguage = null)
    {
        var client = await TestActors.Client(factory).WithName("Petra", "Nováková").CreateAsync(Ct);
        var builder = TestActors.Nutritionist(factory);
        var coach = await (coachLanguage is null ? builder : builder.WithLanguage(coachLanguage)).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).AsRole(UserRole.Nutritionist).CreateAsync(Ct);
        return (client, coach);
    }

    // ── Rule (a): meal changed after the client's snapshot ──────────────

    [Fact]
    public async Task Log_MealChangedAfterSnapshot_WritesMealLogRecordsConflictAndFlagsBody()
    {
        var (client, coach) = await SeedLinkedPairAsync();
        var meal = NewMeal(changedAtPlanVersion: 3);
        var plan = await InsertPlanAsync(client, meal, coach.UserId, version: 3);

        var response = await LogAsync(client, meal.MealId, basedOnPlanVersion: 2);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await ReadAsync(response);
        body.MealLogged.Should().BeTrue();
        body.SyncConflict.Should().BeTrue();
        body.SyncConflictKind.Should().Be(OfflineSyncConflictKind.PlanChanged);
        (await LoadMealLogsAsync(client.UserId)).Should().ContainSingle();
        var conflict = (await LoadConflictsAsync(client.UserId)).Should().ContainSingle().Subject;
        conflict.PlanId.Should().Be(plan.ExternalId);
        conflict.PlanAuthorId.Should().Be(coach.UserId);
        conflict.Kind.Should().Be(OfflineSyncConflictKind.PlanChanged);
        conflict.BasedOnPlanVersion.Should().Be(2);
        conflict.CurrentPlanVersion.Should().Be(3);
        conflict.MealChangedAtPlanVersion.Should().Be(3);
        conflict.MealLogged.Should().BeTrue();
    }

    [Fact]
    public async Task Log_ConflictWithSeenItems_StoresThemOnTheConflict()
    {
        var (client, coach) = await SeedLinkedPairAsync();
        var meal = NewMeal(changedAtPlanVersion: 3);
        await InsertPlanAsync(client, meal, coach.UserId, version: 3);
        var seen = SeenItems();

        var response = await LogAsync(client, meal.MealId, basedOnPlanVersion: 2, seenItems: seen);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var conflict = (await LoadConflictsAsync(client.UserId)).Should().ContainSingle().Subject;
        conflict.SeenItems.Should().BeEquivalentTo(seen);
    }

    [Fact]
    public async Task Log_NoConflictWithSeenItems_IgnoresThem()
    {
        var (client, coach) = await SeedLinkedPairAsync();
        var meal = NewMeal(changedAtPlanVersion: 2);
        await InsertPlanAsync(client, meal, coach.UserId, version: 2);

        var response = await LogAsync(client, meal.MealId, basedOnPlanVersion: 2, seenItems: SeenItems());

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await LoadConflictsAsync(client.UserId)).Should().BeEmpty();
        (await LoadMealLogsAsync(client.UserId)).Should().ContainSingle();
    }

    [Fact]
    public async Task Log_TooManySeenItems_Returns400()
    {
        var (client, coach) = await SeedLinkedPairAsync();
        var meal = NewMeal();
        await InsertPlanAsync(client, meal, coach.UserId);
        var tooMany = Enumerable.Range(0, 41)
            .Select(_ => new SeenMealItemRequest { RefId = Guid.NewGuid(), Name = "Item" })
            .ToList();

        var response = await LogAsync(client, meal.MealId, basedOnPlanVersion: 1, seenItems: tooMany);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(null, 1)] // meal never edited since the marker existed
    [InlineData(2, 2)] // edited exactly at the version the client saw
    [InlineData(2, 5)] // edited before the version the client saw
    public async Task Log_MealNotChangedAfterSnapshot_RecordsNoConflict(int? marker, int basedOn)
    {
        var (client, coach) = await SeedLinkedPairAsync();
        var meal = NewMeal(marker);
        await InsertPlanAsync(client, meal, coach.UserId, version: 6);

        var response = await LogAsync(client, meal.MealId, basedOnPlanVersion: basedOn);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await ReadAsync(response);
        body.SyncConflict.Should().BeFalse();
        body.SyncConflictKind.Should().BeNull();
        body.MealLogged.Should().BeTrue();
        (await LoadConflictsAsync(client.UserId)).Should().BeEmpty();
        (await LoadConflictNotificationsAsync(coach.UserId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Log_WithoutBasedOnPlanVersion_NeverConflicts_EvenWhenMealChanged()
    {
        var (client, coach) = await SeedLinkedPairAsync();
        var meal = NewMeal(changedAtPlanVersion: 9);
        await InsertPlanAsync(client, meal, coach.UserId, version: 9);

        var response = await LogAsync(client, meal.MealId);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await ReadAsync(response)).SyncConflict.Should().BeFalse();
        (await LoadConflictsAsync(client.UserId)).Should().BeEmpty();
    }

    // ── Rule (b): meal left the plan ────────────────────────────────────

    [Fact]
    public async Task Log_MealRemovedFromCurrentPlan_RecordsConflictWithoutMealLog()
    {
        var (client, coach) = await SeedLinkedPairAsync();
        var removedMeal = NewMeal();
        var oldPlan = await InsertPlanAsync(client, removedMeal, coach.UserId, status: NutritionPlanStatus.Archived, startedDaysAgo: 40);
        await InsertPlanAsync(client, NewMeal(), coach.UserId, version: 4);

        var response = await LogAsync(client, removedMeal.MealId, basedOnPlanVersion: 1);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await ReadAsync(response);
        body.MealLogged.Should().BeFalse();
        body.SyncConflict.Should().BeTrue();
        body.SyncConflictKind.Should().Be(OfflineSyncConflictKind.MealRemoved);
        (await LoadMealLogsAsync(client.UserId)).Should().BeEmpty();
        var conflict = (await LoadConflictsAsync(client.UserId)).Should().ContainSingle().Subject;
        conflict.PlanId.Should().Be(oldPlan.ExternalId);
        conflict.Kind.Should().Be(OfflineSyncConflictKind.MealRemoved);
        conflict.MealLogged.Should().BeFalse();
    }

    [Fact]
    public async Task Log_MealOfNoPlanOfTheClient_Returns404AndWritesNothing()
    {
        var (client, coach) = await SeedLinkedPairAsync();
        await InsertPlanAsync(client, NewMeal(), coach.UserId);
        var otherClient = await TestActors.Client(factory).CreateAsync(Ct);
        var foreignMeal = NewMeal();
        await InsertPlanAsync(otherClient, foreignMeal, coach.UserId);
        var requestId = Guid.NewGuid();

        (await LogAsync(client, Guid.NewGuid(), basedOnPlanVersion: 1, clientRequestId: requestId))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await LogAsync(client, foreignMeal.MealId, basedOnPlanVersion: 1))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await LoadConflictsAsync(client.UserId)).Should().BeEmpty();
        (await LoadMealLogsAsync(client.UserId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Log_RemovedMealWithoutBasedOnPlanVersion_Returns404AsBefore()
    {
        var (client, coach) = await SeedLinkedPairAsync();
        var removedMeal = NewMeal();
        await InsertPlanAsync(client, removedMeal, coach.UserId, status: NutritionPlanStatus.Archived, startedDaysAgo: 40);
        await InsertPlanAsync(client, NewMeal(), coach.UserId);

        (await LogAsync(client, removedMeal.MealId)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await LoadConflictsAsync(client.UserId)).Should().BeEmpty();
    }

    // ── Replay ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Log_ReplayedConflictRequest_ReturnsSameBodyWithOneConflictAndOneNotification()
    {
        var (client, coach) = await SeedLinkedPairAsync();
        var meal = NewMeal(changedAtPlanVersion: 3);
        await InsertPlanAsync(client, meal, coach.UserId, version: 3);
        var requestId = Guid.NewGuid();

        var first = await ReadAsync(await LogAsync(client, meal.MealId, 2, requestId));
        var second = await ReadAsync(await LogAsync(client, meal.MealId, 2, requestId));

        second.Should().BeEquivalentTo(first);
        second.SyncConflict.Should().BeTrue();
        (await LoadConflictsAsync(client.UserId)).Should().ContainSingle();
        var notification = (await LoadConflictNotificationsAsync(coach.UserId)).Should().ContainSingle().Subject;
        notification.Data.Should().Contain("\"count\":\"1\"");
    }

    [Fact]
    public async Task Log_ReplayedRemovedMealRequest_KeepsMealLoggedFalse()
    {
        var (client, coach) = await SeedLinkedPairAsync();
        var removedMeal = NewMeal();
        await InsertPlanAsync(client, removedMeal, coach.UserId, status: NutritionPlanStatus.Archived, startedDaysAgo: 40);
        await InsertPlanAsync(client, NewMeal(), coach.UserId);
        var requestId = Guid.NewGuid();

        await LogAsync(client, removedMeal.MealId, 1, requestId);
        var replay = await ReadAsync(await LogAsync(client, removedMeal.MealId, 1, requestId));

        replay.MealLogged.Should().BeFalse();
        replay.SyncConflictKind.Should().Be(OfflineSyncConflictKind.MealRemoved);
        (await LoadConflictsAsync(client.UserId)).Should().ContainSingle();
    }

    [Fact]
    public async Task Log_RemovedMealWithSeenItems_WritesTheMealLogAndReplayDoesNotWriteASecond()
    {
        var (client, coach) = await SeedLinkedPairAsync();
        var removedMeal = NewMeal();
        var oldPlan = await InsertPlanAsync(client, removedMeal, coach.UserId, status: NutritionPlanStatus.Archived, startedDaysAgo: 40);
        await InsertPlanAsync(client, NewMeal(), coach.UserId, version: 4);
        var requestId = Guid.NewGuid();
        var seen = SeenItems();

        var first = await ReadAsync(await LogAsync(client, removedMeal.MealId, 1, requestId, seen));
        var replay = await ReadAsync(await LogAsync(client, removedMeal.MealId, 1, requestId, seen));

        first.MealLogged.Should().BeTrue();
        first.SyncConflictKind.Should().Be(OfflineSyncConflictKind.MealRemoved);
        replay.Should().BeEquivalentTo(first);
        var log = (await LoadMealLogsAsync(client.UserId)).Should().ContainSingle().Subject;
        log.PlanId.Should().Be(oldPlan.ExternalId);
        log.MealKind.Should().Be(MealKind.Lunch);
        log.EatenSnapshot!.Select(item => item.ItemId).Should().BeEquivalentTo(seen.Select(item => item.ItemId));
        var conflict = (await LoadConflictsAsync(client.UserId)).Should().ContainSingle().Subject;
        conflict.MealLogged.Should().BeTrue();
    }

    // ── Notification ────────────────────────────────────────────────────

    [Fact]
    public async Task Conflict_NotifiesNutritionCoachLocalizedWithPushAndSignalR()
    {
        var (client, coach) = await SeedLinkedPairAsync("cs");
        var meal = NewMeal(changedAtPlanVersion: 3);
        var plan = await InsertPlanAsync(client, meal, coach.UserId, version: 3);
        var notifier = factory.Services.GetRequiredService<FakeRealtimeNotifier>();
        var push = factory.Services.GetRequiredService<FakePushNotificationService>();
        notifier.Reset();

        (await LogAsync(client, meal.MealId, 2)).StatusCode.Should().Be(HttpStatusCode.Created);

        var notification = (await LoadConflictNotificationsAsync(coach.UserId)).Should().ContainSingle().Subject;
        notification.IsRead.Should().BeFalse();
        notification.Title.Should().Be("Offline změny ke kontrole");
        notification.Body.Should().Contain("Petra Nováková").And.Contain(plan.Name);
        notification.Data.Should().Contain(plan.ExternalId.ToString());
        var events = notifier.Calls.Where(c => c.UserId == coach.UserId).Select(c => c.EventType).ToList();
        events.Should().Contain("newnotification").And.Contain("offlinesyncconflict");
        push.Calls.Should().Contain(c => c.UserId == coach.UserId);
    }

    [Fact]
    public async Task Conflict_SecondConflictCoalescesIntoOneUnreadNotificationWithoutNewPush()
    {
        var (client, coach) = await SeedLinkedPairAsync("en");
        var first = NewMeal(changedAtPlanVersion: 3);
        var second = NewMeal(changedAtPlanVersion: 3);
        var plan = await ClientNutritionFixtures.InsertPlanAsync(
            factory, client.UserId, NutritionPlanStatus.Active, Today.AddDays(-5), coach.UserId, null,
            ClientNutritionFixtures.Week(1, WeekStatus.Published, first, second));
        var notifier = factory.Services.GetRequiredService<FakeRealtimeNotifier>();
        var push = factory.Services.GetRequiredService<FakePushNotificationService>();

        await LogAsync(client, first.MealId, 2);
        notifier.Reset();
        var pushCallsBefore = push.Calls.Count(c => c.UserId == coach.UserId);
        await LogAsync(client, second.MealId, 2);

        (await LoadConflictsAsync(client.UserId)).Should().HaveCount(2);
        var notification = (await LoadConflictNotificationsAsync(coach.UserId)).Should().ContainSingle().Subject;
        notification.Body.Should().Contain("synced 2 offline action(s)").And.Contain(plan.Name);
        notification.Data.Should().Contain("\"count\":\"2\"");
        notifier.Calls.Where(c => c.UserId == coach.UserId).Select(c => c.EventType)
            .Should().Contain("offlinesyncconflict").And.NotContain("newnotification");
        push.Calls.Count(c => c.UserId == coach.UserId).Should().Be(pushCallsBefore);
    }

    [Fact]
    public async Task Conflict_CountExcludesConflictsTheCoachAlreadyReviewed()
    {
        var (client, coach) = await SeedLinkedPairAsync("en");
        var meal = NewMeal(changedAtPlanVersion: 3);
        var plan = await InsertPlanAsync(client, meal, coach.UserId, version: 3);
        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
            await mongo.OfflineSyncConflicts.InsertOneAsync(new OfflineSyncConflict
            {
                ExternalId = Guid.NewGuid(),
                ClientId = client.UserId,
                PlanId = plan.ExternalId,
                PlanAuthorId = coach.UserId,
                Operation = "LogMealEaten",
                Kind = OfflineSyncConflictKind.PlanChanged,
                MealId = meal.MealId,
                PlanName = plan.Name,
                ReviewedAt = DateTime.UtcNow,
                ReviewedByUserId = coach.UserId,
            }, cancellationToken: Ct);
        }

        (await LogAsync(client, meal.MealId, 2)).StatusCode.Should().Be(HttpStatusCode.Created);

        (await LoadConflictsAsync(client.UserId)).Should().HaveCount(2);
        var notification = (await LoadConflictNotificationsAsync(coach.UserId)).Should().ContainSingle().Subject;
        notification.Data.Should().Contain("\"count\":\"1\"");
    }

    [Fact]
    public async Task Conflict_AfterNotificationWasRead_CreatesANewOne()
    {
        var (client, coach) = await SeedLinkedPairAsync();
        var first = NewMeal(changedAtPlanVersion: 3);
        var second = NewMeal(changedAtPlanVersion: 3);
        await ClientNutritionFixtures.InsertPlanAsync(
            factory, client.UserId, NutritionPlanStatus.Active, Today.AddDays(-5), coach.UserId, null,
            ClientNutritionFixtures.Week(1, WeekStatus.Published, first, second));

        await LogAsync(client, first.MealId, 2);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
            await db.Notifications
                .Where(n => n.RecipientUserId == coach.UserId && n.Type == NotificationType.OfflineSyncConflict)
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.IsRead, true), Ct);
        }

        await LogAsync(client, second.MealId, 2);

        var notifications = await LoadConflictNotificationsAsync(coach.UserId);
        notifications.Should().HaveCount(2);
        notifications.Count(n => !n.IsRead).Should().Be(1);
    }

    [Fact]
    public async Task Conflict_OnlyThePlanAuthorIsNotified_NotOtherLinkedCoaches()
    {
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var author = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var secondNutritionist = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var trainerOnly = await TestActors.Trainer(factory).CreateAsync(Ct);
        await TestActors.Link(factory, author, client).AsRole(UserRole.Nutritionist).CreateAsync(Ct);
        await TestActors.Link(factory, secondNutritionist, client).AsRole(UserRole.Nutritionist).CreateAsync(Ct);
        await TestActors.Link(factory, trainerOnly, client).CreateAsync(Ct);
        var meal = NewMeal(changedAtPlanVersion: 3);
        await InsertPlanAsync(client, meal, author.UserId, version: 3);

        (await LogAsync(client, meal.MealId, 2)).StatusCode.Should().Be(HttpStatusCode.Created);

        (await LoadConflictNotificationsAsync(author.UserId)).Should().ContainSingle();
        (await LoadConflictNotificationsAsync(secondNutritionist.UserId)).Should().BeEmpty();
        (await LoadConflictNotificationsAsync(trainerOnly.UserId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Conflict_AuthorWithEndedLink_NobodyIsNotified()
    {
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var author = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var otherNutritionist = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        await TestActors.Link(factory, author, client).AsRole(UserRole.Nutritionist).Inactive().CreateAsync(Ct);
        await TestActors.Link(factory, otherNutritionist, client).AsRole(UserRole.Nutritionist).CreateAsync(Ct);
        var meal = NewMeal(changedAtPlanVersion: 3);
        await InsertPlanAsync(client, meal, author.UserId, version: 3);

        (await LogAsync(client, meal.MealId, 2)).StatusCode.Should().Be(HttpStatusCode.Created);

        (await LoadConflictNotificationsAsync(author.UserId)).Should().BeEmpty();
        (await LoadConflictNotificationsAsync(otherNutritionist.UserId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Conflict_NotificationFailure_DoesNotFailTheClientWrite()
    {
        var (client, coach) = await SeedLinkedPairAsync();
        var meal = NewMeal(changedAtPlanVersion: 3);
        await InsertPlanAsync(client, meal, coach.UserId, version: 3);
        var notifier = factory.Services.GetRequiredService<FakeRealtimeNotifier>();
        notifier.Reset();
        notifier.SimulateThrowOnNextCall();

        var response = await LogAsync(client, meal.MealId, 2);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await ReadAsync(response)).SyncConflict.Should().BeTrue();
        (await LoadMealLogsAsync(client.UserId)).Should().ContainSingle();
        (await LoadConflictsAsync(client.UserId)).Should().ContainSingle();
    }

    // ── PlanVersion on the client plan reads ────────────────────────────

    [Theory]
    [InlineData("/client/nutrition/plan/today")]
    [InlineData("/client/nutrition/plan/week")]
    [InlineData("/client/nutrition/plan/full")]
    public async Task PlanReads_ExposeThePlanVersion(string route)
    {
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var plan = await ClientNutritionFixtures.InsertPlanAsync(
            factory, client.UserId, NutritionPlanStatus.Active, Today, null, null,
            ClientNutritionFixtures.Week(1, WeekStatus.Published, NewMeal()));
        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
            await mongo.NutritionPlans.UpdateOneAsync(
                p => p.ExternalId == plan.ExternalId, Builders<NutritionPlan>.Update.Set(p => p.Version, 7), cancellationToken: Ct);
        }

        var response = await client.Http.GetAsync(route, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        document.RootElement.GetProperty("planVersion").GetInt32().Should().Be(7);
    }
}
