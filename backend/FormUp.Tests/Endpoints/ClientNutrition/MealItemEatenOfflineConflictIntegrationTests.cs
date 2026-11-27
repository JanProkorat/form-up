using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace FormUp.Tests.Endpoints.ClientNutrition;

/// <summary>
/// Testcontainers integration tests for offline-sync conflicts on the per-item eaten endpoints: an offline
/// write (one carrying <c>BasedOnPlanVersion</c>) that collides with a plan change is recorded and the plan's
/// author is told; an online write and a replay record nothing.
/// </summary>
[Collection(TestCollection.Name)]
public class MealItemEatenOfflineConflictIntegrationTests(FitnessApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ItemRoute(Guid mealId, Guid itemId) =>
        $"/client/nutrition/log/meals/{mealId}/items/{itemId}/eaten";

    private static Task<HttpResponseMessage> TickAsync(
        Actor client, Guid mealId, Guid itemId, int? basedOnPlanVersion = null, Guid? clientRequestId = null) =>
        client.Http.PostAsJsonAsync(
            ItemRoute(mealId, itemId), new { BasedOnPlanVersion = basedOnPlanVersion, ClientRequestId = clientRequestId }, Ct);

    private static Task<HttpResponseMessage> UntickAsync(
        Actor client, Guid mealId, Guid itemId, int? basedOnPlanVersion = null, Guid? clientRequestId = null)
    {
        var query = new List<string>();

        if (basedOnPlanVersion is not null)
        {
            query.Add($"basedOnPlanVersion={basedOnPlanVersion}");
        }

        if (clientRequestId is not null)
        {
            query.Add($"clientRequestId={clientRequestId}");
        }

        return client.Http.DeleteAsync(
            ItemRoute(mealId, itemId) + (query.Count == 0 ? string.Empty : "?" + string.Join('&', query)), Ct);
    }

    private async Task<(Actor Client, Actor Coach, SeededItemMeal Seeded)> SeedAsync()
    {
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).AsRole(UserRole.Nutritionist).CreateAsync(Ct);
        var seeded = await MealItemPlanSeeder.SeedAsync(factory, client.UserId);
        await MealItemPlanSeeder.SetNutritionistAsync(factory, seeded.PlanId, coach.UserId);

        return (client, coach, seeded);
    }

    private async Task<List<OfflineSyncConflict>> LoadConflictsAsync(Guid clientId)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        return await mongo.OfflineSyncConflicts.Find(c => c.ClientId == clientId).ToListAsync(Ct);
    }

    private async Task<int> CountConflictNotificationsAsync(Guid recipientUserId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();

        return await db.Notifications
            .AsNoTracking()
            .CountAsync(n => n.RecipientUserId == recipientUserId && n.Type == NotificationType.OfflineSyncConflict, Ct);
    }

    private static async Task AssertItemNotFoundAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        body.RootElement.GetProperty("errorCode").GetString().Should().Be("MEAL_ITEM_NOT_FOUND");
    }

    [Fact]
    public async Task Tick_OfflineOnAnItemTheCoachRemoved_Returns404RecordsConflictAndNotifiesTheAuthor()
    {
        var (client, coach, seeded) = await SeedAsync();
        await MealItemPlanSeeder.RemoveItemAsync(factory, seeded, seeded.FoodBId);

        var response = await TickAsync(client, seeded.MealId, seeded.FoodBId, basedOnPlanVersion: 1);

        await AssertItemNotFoundAsync(response);
        var conflict = (await LoadConflictsAsync(client.UserId)).Should().ContainSingle().Subject;
        conflict.Kind.Should().Be(OfflineSyncConflictKind.MealRemoved);
        conflict.Operation.Should().Be("LogMealItemEaten");
        conflict.MealId.Should().Be(seeded.MealId);
        conflict.PlanId.Should().Be(seeded.PlanId);
        conflict.PlanAuthorId.Should().Be(coach.UserId);
        conflict.BasedOnPlanVersion.Should().Be(1);
        conflict.MealLogged.Should().BeFalse();
        (await CountConflictNotificationsAsync(coach.UserId)).Should().Be(1);
        (await MealItemPlanSeeder.LoadLogsAsync(factory, client.UserId, seeded.MealId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Tick_OfflineOnAMealTheCoachRemoved_Returns404RecordsConflictAndNotifiesTheAuthor()
    {
        var (client, coach, seeded) = await SeedAsync();
        await MealItemPlanSeeder.RemoveMealAsync(factory, seeded, planVersion: 2);

        var response = await TickAsync(client, seeded.MealId, seeded.FoodAId, basedOnPlanVersion: 1);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var conflict = (await LoadConflictsAsync(client.UserId)).Should().ContainSingle().Subject;
        conflict.Kind.Should().Be(OfflineSyncConflictKind.MealRemoved);
        conflict.PlanId.Should().Be(seeded.PlanId);
        conflict.MealLogged.Should().BeFalse();
        (await CountConflictNotificationsAsync(coach.UserId)).Should().Be(1);
    }

    [Fact]
    public async Task Tick_OfflineOnAnUnknownMeal_RecordsNothing()
    {
        var (client, coach, _) = await SeedAsync();

        var response = await TickAsync(client, Guid.NewGuid(), Guid.NewGuid(), basedOnPlanVersion: 1);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await LoadConflictsAsync(client.UserId)).Should().BeEmpty();
        (await CountConflictNotificationsAsync(coach.UserId)).Should().Be(0);
    }

    [Fact]
    public async Task Tick_OnlineOnAnItemTheCoachRemoved_Returns404AndRecordsNothing()
    {
        var (client, coach, seeded) = await SeedAsync();
        await MealItemPlanSeeder.RemoveItemAsync(factory, seeded, seeded.FoodBId);

        var response = await TickAsync(client, seeded.MealId, seeded.FoodBId);

        await AssertItemNotFoundAsync(response);
        (await LoadConflictsAsync(client.UserId)).Should().BeEmpty();
        (await CountConflictNotificationsAsync(coach.UserId)).Should().Be(0);
    }

    [Fact]
    public async Task Tick_OfflineOnAMealChangedAfterTheSnapshot_AppliesTheTickAndRecordsPlanChanged()
    {
        var (client, coach, seeded) = await SeedAsync();
        await MealItemPlanSeeder.StampMealChangeAsync(factory, seeded, planVersion: 3);

        var response = await TickAsync(client, seeded.MealId, seeded.FoodAId, basedOnPlanVersion: 2);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var row = (await MealItemPlanSeeder.LoadLogsAsync(factory, client.UserId, seeded.MealId)).Should().ContainSingle().Subject;
        row.EatenItemIds.Should().BeEquivalentTo([seeded.FoodAId]);
        var conflict = (await LoadConflictsAsync(client.UserId)).Should().ContainSingle().Subject;
        conflict.Kind.Should().Be(OfflineSyncConflictKind.PlanChanged);
        conflict.Operation.Should().Be("LogMealItemEaten");
        conflict.MealChangedAtPlanVersion.Should().Be(3);
        conflict.CurrentPlanVersion.Should().Be(3);
        conflict.MealLogged.Should().BeTrue();
        (await CountConflictNotificationsAsync(coach.UserId)).Should().Be(1);
    }

    [Fact]
    public async Task Tick_OnlineOnAMealChangedAfterTheSnapshot_RecordsNothing()
    {
        var (client, _, seeded) = await SeedAsync();
        await MealItemPlanSeeder.StampMealChangeAsync(factory, seeded, planVersion: 3);

        var response = await TickAsync(client, seeded.MealId, seeded.FoodAId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await LoadConflictsAsync(client.UserId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Tick_OfflineOnAnUnchangedMeal_RecordsNothing()
    {
        var (client, _, seeded) = await SeedAsync();

        var response = await TickAsync(client, seeded.MealId, seeded.FoodAId, basedOnPlanVersion: 1);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await LoadConflictsAsync(client.UserId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Tick_ReplayedConflictingRequest_RecordsOneConflictOnly()
    {
        var (client, _, seeded) = await SeedAsync();
        await MealItemPlanSeeder.StampMealChangeAsync(factory, seeded, planVersion: 3);
        var requestId = Guid.NewGuid();

        var first = await TickAsync(client, seeded.MealId, seeded.FoodAId, basedOnPlanVersion: 2, clientRequestId: requestId);
        var replay = await TickAsync(client, seeded.MealId, seeded.FoodAId, basedOnPlanVersion: 2, clientRequestId: requestId);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        (await LoadConflictsAsync(client.UserId)).Should().ContainSingle();
    }

    [Fact]
    public async Task Tick_ReplayedRequestForARemovedItem_RecordsOneConflictOnly()
    {
        var (client, _, seeded) = await SeedAsync();
        await MealItemPlanSeeder.RemoveItemAsync(factory, seeded, seeded.FoodBId);
        var requestId = Guid.NewGuid();

        await TickAsync(client, seeded.MealId, seeded.FoodBId, basedOnPlanVersion: 1, clientRequestId: requestId);
        var replay = await TickAsync(client, seeded.MealId, seeded.FoodBId, basedOnPlanVersion: 1, clientRequestId: requestId);

        await AssertItemNotFoundAsync(replay);
        (await LoadConflictsAsync(client.UserId)).Should().ContainSingle();
    }

    [Fact]
    public async Task Untick_OfflineOnAnItemRemovedAfterItWasEaten_AppliesFromTheSnapshotAndRecordsNothing()
    {
        var (client, coach, seeded) = await SeedAsync();
        await TickAsync(client, seeded.MealId, seeded.FoodBId);
        await MealItemPlanSeeder.RemoveItemAsync(factory, seeded, seeded.FoodBId);

        var response = await UntickAsync(client, seeded.MealId, seeded.FoodBId, basedOnPlanVersion: 1);

        // The row's snapshot still lists the item, so it is judged against what was eaten and the untick applies.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await LoadConflictsAsync(client.UserId)).Should().BeEmpty();
        (await CountConflictNotificationsAsync(coach.UserId)).Should().Be(0);
    }

    [Fact]
    public async Task Untick_OfflineOnAnItemNotInTheMeal_Returns404RecordsConflictAndNotifiesTheAuthor()
    {
        var (client, coach, seeded) = await SeedAsync();
        await MealItemPlanSeeder.RemoveItemAsync(factory, seeded, seeded.FoodBId);

        var response = await UntickAsync(client, seeded.MealId, seeded.FoodBId, basedOnPlanVersion: 1);

        await AssertItemNotFoundAsync(response);
        var conflict = (await LoadConflictsAsync(client.UserId)).Should().ContainSingle().Subject;
        conflict.Kind.Should().Be(OfflineSyncConflictKind.MealRemoved);
        conflict.Operation.Should().Be("UnlogMealItemEaten");
        conflict.MealLogged.Should().BeFalse();
        (await CountConflictNotificationsAsync(coach.UserId)).Should().Be(1);
    }

    [Fact]
    public async Task Untick_OfflineOnAMealTheCoachRemoved_Returns404AndRecordsConflict()
    {
        var (client, coach, seeded) = await SeedAsync();
        await MealItemPlanSeeder.RemoveMealAsync(factory, seeded, planVersion: 2);

        var response = await UntickAsync(client, seeded.MealId, seeded.FoodAId, basedOnPlanVersion: 1);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var conflict = (await LoadConflictsAsync(client.UserId)).Should().ContainSingle().Subject;
        conflict.Kind.Should().Be(OfflineSyncConflictKind.MealRemoved);
        conflict.Operation.Should().Be("UnlogMealItemEaten");
        (await CountConflictNotificationsAsync(coach.UserId)).Should().Be(1);
    }

    [Fact]
    public async Task Untick_OnlineOnAMealTheCoachRemoved_RecordsNothing()
    {
        var (client, _, seeded) = await SeedAsync();
        await MealItemPlanSeeder.RemoveMealAsync(factory, seeded, planVersion: 2);

        var response = await UntickAsync(client, seeded.MealId, seeded.FoodAId);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await LoadConflictsAsync(client.UserId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Untick_OfflineOnAMealChangedAfterTheSnapshot_AppliesTheUntickAndRecordsPlanChanged()
    {
        var (client, coach, seeded) = await SeedAsync();
        await TickAsync(client, seeded.MealId, seeded.FoodAId);
        await TickAsync(client, seeded.MealId, seeded.FoodBId);
        await MealItemPlanSeeder.StampMealChangeAsync(factory, seeded, planVersion: 3);

        var response = await UntickAsync(client, seeded.MealId, seeded.FoodAId, basedOnPlanVersion: 2);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var row = (await MealItemPlanSeeder.LoadLogsAsync(factory, client.UserId, seeded.MealId)).Should().ContainSingle().Subject;
        row.EatenItemIds.Should().BeEquivalentTo([seeded.FoodBId]);
        var conflict = (await LoadConflictsAsync(client.UserId)).Should().ContainSingle().Subject;
        conflict.Kind.Should().Be(OfflineSyncConflictKind.PlanChanged);
        conflict.Operation.Should().Be("UnlogMealItemEaten");
        conflict.MealLogged.Should().BeTrue("the client's untick was applied");
        (await CountConflictNotificationsAsync(coach.UserId)).Should().Be(1);
    }

    [Fact]
    public async Task Untick_ReplayedConflictingRequest_RecordsOneConflictOnly()
    {
        var (client, _, seeded) = await SeedAsync();
        await TickAsync(client, seeded.MealId, seeded.FoodAId);
        await MealItemPlanSeeder.StampMealChangeAsync(factory, seeded, planVersion: 3);
        var requestId = Guid.NewGuid();

        await UntickAsync(client, seeded.MealId, seeded.FoodAId, basedOnPlanVersion: 2, clientRequestId: requestId);
        var replay = await UntickAsync(client, seeded.MealId, seeded.FoodAId, basedOnPlanVersion: 2, clientRequestId: requestId);

        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        (await LoadConflictsAsync(client.UserId)).Should().ContainSingle();
    }
}
