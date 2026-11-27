using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace FormUp.Tests.Endpoints.ClientNutrition;

/// <summary>
/// Testcontainers integration tests (real MongoDB, which honours filters and indexes) for requests that act on
/// one meal at the same moment: no lost item id, never a second row for the meal and day, and no 500.
/// </summary>
[Collection(TestCollection.Name)]
public class MealItemEatenConcurrencyIntegrationTests(FitnessApiFactory factory)
{
    private const int Rounds = 5;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ItemRoute(Guid mealId, Guid itemId) =>
        $"/client/nutrition/log/meals/{mealId}/items/{itemId}/eaten";

    private static Task<HttpResponseMessage> TickAsync(Actor client, Guid mealId, Guid itemId) =>
        client.Http.PostAsJsonAsync(ItemRoute(mealId, itemId), new { }, Ct);

    private static Task<HttpResponseMessage> UntickAsync(Actor client, Guid mealId, Guid itemId, Guid? clientRequestId = null) =>
        client.Http.DeleteAsync(
            ItemRoute(mealId, itemId) + (clientRequestId is null ? string.Empty : $"?clientRequestId={clientRequestId}"), Ct);

    private static async Task<ItemStateDto> ReadStateAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<ItemStateDto>(JsonOptions, Ct))!;
    }

    private async Task<(Actor Client, SeededItemMeal Seeded)> SeedAsync()
    {
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var seeded = await MealItemPlanSeeder.SeedAsync(factory, client.UserId);

        return (client, seeded);
    }

    [Fact]
    public async Task Tick_ThreeDifferentItemsAtOnce_LeavesOneRowHoldingEveryId()
    {
        for (var round = 0; round < Rounds; round++)
        {
            var (client, seeded) = await SeedAsync();

            var responses = await Task.WhenAll(
                TickAsync(client, seeded.MealId, seeded.FoodAId),
                TickAsync(client, seeded.MealId, seeded.FoodBId),
                TickAsync(client, seeded.MealId, seeded.RecipeId));

            responses.Should().OnlyContain(response => response.StatusCode == HttpStatusCode.OK);
            var row = (await MealItemPlanSeeder.LoadLogsAsync(factory, client.UserId, seeded.MealId)).Should().ContainSingle().Subject;
            row.EatenItemIds.Should().BeEquivalentTo([seeded.FoodAId, seeded.FoodBId, seeded.RecipeId]);
            row.EatenAt.Should().NotBeNull("every item is eaten");
            row.EatenSnapshot.Should().NotBeNull();
            row.EatenSnapshot!.Select(item => item.ItemId).Should().BeEquivalentTo(row.EatenItemIds);
        }
    }

    [Fact]
    public async Task Tick_TwoOfThreeItemsAtOnce_LeavesOneRowPartlyEatenWithBothIds()
    {
        for (var round = 0; round < Rounds; round++)
        {
            var (client, seeded) = await SeedAsync();

            var responses = await Task.WhenAll(
                TickAsync(client, seeded.MealId, seeded.FoodAId),
                TickAsync(client, seeded.MealId, seeded.FoodBId));

            responses.Should().OnlyContain(response => response.StatusCode == HttpStatusCode.OK);
            var row = (await MealItemPlanSeeder.LoadLogsAsync(factory, client.UserId, seeded.MealId)).Should().ContainSingle().Subject;
            row.EatenItemIds.Should().BeEquivalentTo([seeded.FoodAId, seeded.FoodBId]);
            row.EatenAt.Should().BeNull();
            row.EatenSnapshot.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task TickAndUntick_MixedAtOnce_LosesNoIdAndKeepsOneRow()
    {
        for (var round = 0; round < Rounds; round++)
        {
            var (client, seeded) = await SeedAsync();
            await TickAsync(client, seeded.MealId, seeded.FoodAId);

            var responses = await Task.WhenAll(
                UntickAsync(client, seeded.MealId, seeded.FoodAId),
                TickAsync(client, seeded.MealId, seeded.FoodBId),
                TickAsync(client, seeded.MealId, seeded.RecipeId));

            responses.Should().OnlyContain(response => response.StatusCode == HttpStatusCode.OK);
            var row = (await MealItemPlanSeeder.LoadLogsAsync(factory, client.UserId, seeded.MealId)).Should().ContainSingle().Subject;
            row.EatenItemIds.Should().BeEquivalentTo([seeded.FoodBId, seeded.RecipeId]);
            row.EatenAt.Should().BeNull("food A is not eaten");
        }
    }

    [Fact]
    public async Task Tick_RacingTheUntickThatDeletesTheRow_RecreatesTheRowWithTheId()
    {
        for (var round = 0; round < Rounds; round++)
        {
            var (client, seeded) = await SeedAsync();
            await TickAsync(client, seeded.MealId, seeded.FoodAId);

            var responses = await Task.WhenAll(
                UntickAsync(client, seeded.MealId, seeded.FoodAId),
                TickAsync(client, seeded.MealId, seeded.FoodBId));

            responses.Should().OnlyContain(response => response.StatusCode == HttpStatusCode.OK);
            var row = (await MealItemPlanSeeder.LoadLogsAsync(factory, client.UserId, seeded.MealId)).Should().ContainSingle().Subject;
            row.EatenItemIds.Should().BeEquivalentTo([seeded.FoodBId]);
            row.EatenSnapshot.Should().NotBeNull("the row holds an eaten item again");
        }
    }

    [Fact]
    public async Task LogMealEaten_RacingAnItemTick_Returns2xxAndLeavesOneRow()
    {
        for (var round = 0; round < Rounds; round++)
        {
            var (client, seeded) = await SeedAsync();

            var responses = await Task.WhenAll(
                TickAsync(client, seeded.MealId, seeded.FoodAId),
                client.Http.PostAsJsonAsync($"/client/nutrition/log/meals/{seeded.MealId}/eaten", new { }, Ct));

            responses[0].StatusCode.Should().Be(HttpStatusCode.OK);
            responses[1].StatusCode.Should().Be(HttpStatusCode.Created);
            var row = (await MealItemPlanSeeder.LoadLogsAsync(factory, client.UserId, seeded.MealId)).Should().ContainSingle().Subject;
            row.EatenItemIds.Should().Contain(seeded.FoodAId);
        }
    }

    [Fact]
    public async Task Untick_ReplayedOnAMealThatBecameFullyEaten_ReturnsTheStoredEatenAt()
    {
        var (client, seeded) = await SeedAsync();
        var requestId = Guid.NewGuid();
        await TickAsync(client, seeded.MealId, seeded.FoodAId);
        await TickAsync(client, seeded.MealId, seeded.FoodBId);
        await TickAsync(client, seeded.MealId, seeded.RecipeId);
        var first = await ReadStateAsync(await UntickAsync(client, seeded.MealId, seeded.FoodAId, requestId));
        await TickAsync(client, seeded.MealId, seeded.FoodAId);

        var replay = await ReadStateAsync(await UntickAsync(client, seeded.MealId, seeded.FoodAId, requestId));

        first.EatenState.Should().Be(MealEatenState.PartlyEaten);
        var row = (await MealItemPlanSeeder.LoadLogsAsync(factory, client.UserId, seeded.MealId)).Should().ContainSingle().Subject;
        row.EatenAt.Should().NotBeNull();
        replay.EatenState.Should().Be(MealEatenState.Eaten, "the replay must not re-apply the untick");
        replay.EatenAt.Should().NotBeNull();
        replay.EatenAt!.Value.Should().BeCloseTo(row.EatenAt!.Value, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task Tick_OnALegacyEatenRow_KeepsTheMealEatenWithEveryItem()
    {
        var (client, seeded) = await SeedAsync();
        await MealItemPlanSeeder.InsertLogAsync(factory, client.UserId, seeded, DateTime.UtcNow.AddHours(-1));

        var state = await ReadStateAsync(await TickAsync(client, seeded.MealId, seeded.FoodAId));

        state.EatenState.Should().Be(MealEatenState.Eaten);
        state.EatenAt.Should().NotBeNull();
        var row = (await MealItemPlanSeeder.LoadLogsAsync(factory, client.UserId, seeded.MealId)).Should().ContainSingle().Subject;
        row.EatenItemIds.Should().BeEquivalentTo([seeded.FoodAId, seeded.FoodBId, seeded.RecipeId]);
        row.EatenAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Untick_OnALegacyEatenRow_LeavesTheOtherItemsEaten()
    {
        var (client, seeded) = await SeedAsync();
        await MealItemPlanSeeder.InsertLogAsync(factory, client.UserId, seeded, DateTime.UtcNow.AddHours(-1));

        var state = await ReadStateAsync(await UntickAsync(client, seeded.MealId, seeded.FoodAId));

        state.EatenState.Should().Be(MealEatenState.PartlyEaten);
        state.EatenItemIds.Should().BeEquivalentTo([seeded.FoodBId, seeded.RecipeId]);
        var row = (await MealItemPlanSeeder.LoadLogsAsync(factory, client.UserId, seeded.MealId)).Should().ContainSingle().Subject;
        row.EatenItemIds.Should().BeEquivalentTo([seeded.FoodBId, seeded.RecipeId]);
        row.EatenAt.Should().BeNull();
    }

    [Fact]
    public async Task Untick_LastItemOfARowWithAPhoto_KeepsTheRowWithoutSnapshotAndEatenAt()
    {
        var (client, seeded) = await SeedAsync();
        await TickAsync(client, seeded.MealId, seeded.FoodAId);
        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
            await mongo.MealLogs.UpdateOneAsync(
                l => l.ClientId == client.UserId && l.MealId == seeded.MealId,
                Builders<MealLog>.Update.Set(l => l.Photos, [new MealPhoto { BlobUrl = "https://blob.example/photo.jpg" }]),
                cancellationToken: Ct);
        }

        var state = await ReadStateAsync(await UntickAsync(client, seeded.MealId, seeded.FoodAId));

        state.EatenState.Should().Be(MealEatenState.NotEaten);
        var row = (await MealItemPlanSeeder.LoadLogsAsync(factory, client.UserId, seeded.MealId)).Should().ContainSingle().Subject;
        row.EatenItemIds.Should().BeEmpty();
        row.EatenSnapshot.Should().BeNull();
        row.EatenAt.Should().BeNull();
        row.Photos.Should().ContainSingle();
    }

    [Fact]
    public async Task UniqueIndex_SecondRowForTheSameMealAndDay_IsRejectedButLegacyRowsAreNot()
    {
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var planId = Guid.NewGuid();
        var mealId = Guid.NewGuid();
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        MealLog NewRow(DateTime logDate) => new()
        {
            ClientId = client.UserId, PlanId = planId, MealId = mealId, LogDate = logDate,
        };

        await mongo.MealLogs.InsertOneAsync(NewRow(DateTime.UtcNow.Date), cancellationToken: Ct);
        var duplicate = async () => await mongo.MealLogs.InsertOneAsync(NewRow(DateTime.UtcNow.Date), cancellationToken: Ct);
        await mongo.MealLogs.InsertOneAsync(NewRow(DateTime.MinValue), cancellationToken: Ct);
        var secondLegacy = async () => await mongo.MealLogs.InsertOneAsync(NewRow(DateTime.MinValue), cancellationToken: Ct);

        (await duplicate.Should().ThrowAsync<MongoWriteException>())
            .Which.WriteError.Category.Should().Be(ServerErrorCategory.DuplicateKey);
        await secondLegacy.Should().NotThrowAsync();
    }

    private record ItemStateDto(Guid MealId, Guid ItemId, MealEatenState EatenState, List<Guid> EatenItemIds, DateTime? EatenAt);
}
