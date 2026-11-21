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
/// Testcontainers integration tests for unticking a single meal item: the all-eaten to partly eaten
/// step, row deletion versus keeping photos, the whole-meal unmark cascade, and the 404 shape.
/// </summary>
[Collection(TestCollection.Shard2)]
public class UnlogMealItemEatenIntegrationTests(FitnessApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string ItemRoute(Guid mealId, Guid itemId) =>
        $"/client/nutrition/log/meals/{mealId}/items/{itemId}/eaten";

    private async Task TickAllAsync(Actor client, SeededItemMeal seeded)
    {
        var response = await client.Http.PostAsJsonAsync(
            $"/client/nutrition/log/meals/{seeded.MealId}/eaten", new { }, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Untick_OneItemOfAnEatenMeal_BecomesPartlyEatenAndClearsEatenAt()
    {
        var client = await TestActors.Client(factory).CreateAsync();
        var seeded = await MealItemPlanSeeder.SeedAsync(factory, client.UserId);
        await TickAllAsync(client, seeded);

        var response = await client.Http.DeleteAsync(ItemRoute(seeded.MealId, seeded.RecipeId), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var state = (await response.Content.ReadFromJsonAsync<StateDto>(JsonOptions, TestContext.Current.CancellationToken))!;
        state.EatenState.Should().Be(MealEatenState.PartlyEaten);
        state.EatenItemIds.Should().BeEquivalentTo([seeded.FoodAId, seeded.FoodBId]);
        state.EatenAt.Should().BeNull();
        var row = (await MealItemPlanSeeder.LoadLogsAsync(factory, client.UserId, seeded.MealId)).Should().ContainSingle().Subject;
        row.EatenAt.Should().BeNull();
        row.EatenItemIds.Should().BeEquivalentTo([seeded.FoodAId, seeded.FoodBId]);
    }

    [Fact]
    public async Task Untick_LastEatenItemWithoutPhotosOrNote_DeletesTheRow()
    {
        var client = await TestActors.Client(factory).CreateAsync();
        var seeded = await MealItemPlanSeeder.SeedAsync(factory, client.UserId);
        await client.Http.PostAsJsonAsync(ItemRoute(seeded.MealId, seeded.FoodAId), new { }, TestContext.Current.CancellationToken);

        var response = await client.Http.DeleteAsync(ItemRoute(seeded.MealId, seeded.FoodAId), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var state = (await response.Content.ReadFromJsonAsync<StateDto>(JsonOptions, TestContext.Current.CancellationToken))!;
        state.EatenState.Should().Be(MealEatenState.NotEaten);
        (await MealItemPlanSeeder.LoadLogsAsync(factory, client.UserId, seeded.MealId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Untick_LastEatenItemOfARowWithAPhoto_KeepsTheRowAsNotEaten()
    {
        var client = await TestActors.Client(factory).CreateAsync();
        var seeded = await MealItemPlanSeeder.SeedAsync(factory, client.UserId);
        await client.Http.PostAsJsonAsync(ItemRoute(seeded.MealId, seeded.FoodAId), new { }, TestContext.Current.CancellationToken);
        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
            await mongo.MealLogs.UpdateOneAsync(
                l => l.ClientId == client.UserId && l.MealId == seeded.MealId,
                Builders<MealLog>.Update.Set(l => l.Photos, [new MealPhoto { BlobUrl = "https://minio.local/b/p.jpg", UploadedAt = DateTime.UtcNow }]),
                cancellationToken: TestContext.Current.CancellationToken);
        }

        await client.Http.DeleteAsync(ItemRoute(seeded.MealId, seeded.FoodAId), TestContext.Current.CancellationToken);

        var row = (await MealItemPlanSeeder.LoadLogsAsync(factory, client.UserId, seeded.MealId)).Should().ContainSingle().Subject;
        row.EatenItemIds.Should().BeEmpty();
        row.EatenAt.Should().BeNull();
        row.Photos.Should().ContainSingle();
    }

    [Fact]
    public async Task UnlogWholeMeal_ClearsAPartlyEatenRow()
    {
        var client = await TestActors.Client(factory).CreateAsync();
        var seeded = await MealItemPlanSeeder.SeedAsync(factory, client.UserId);
        await client.Http.PostAsJsonAsync(ItemRoute(seeded.MealId, seeded.FoodAId), new { }, TestContext.Current.CancellationToken);

        var response = await client.Http.DeleteAsync(
            $"/client/nutrition/log/meals/{seeded.MealId}/eaten", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await MealItemPlanSeeder.LoadLogsAsync(factory, client.UserId, seeded.MealId)).Should().BeEmpty();
    }

    [Fact]
    public async Task UnlogWholeMeal_OnPartlyEatenRowWithPhoto_EmptiesEatenItemIds()
    {
        var client = await TestActors.Client(factory).CreateAsync();
        var seeded = await MealItemPlanSeeder.SeedAsync(factory, client.UserId);
        await client.Http.PostAsJsonAsync(ItemRoute(seeded.MealId, seeded.FoodAId), new { }, TestContext.Current.CancellationToken);
        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
            await mongo.MealLogs.UpdateOneAsync(
                l => l.ClientId == client.UserId && l.MealId == seeded.MealId,
                Builders<MealLog>.Update.Set(l => l.Note, "kept"),
                cancellationToken: TestContext.Current.CancellationToken);
        }

        await client.Http.DeleteAsync($"/client/nutrition/log/meals/{seeded.MealId}/eaten", TestContext.Current.CancellationToken);

        var row = (await MealItemPlanSeeder.LoadLogsAsync(factory, client.UserId, seeded.MealId)).Should().ContainSingle().Subject;
        row.EatenItemIds.Should().BeEmpty();
        row.EatenAt.Should().BeNull();
    }

    [Fact]
    public async Task Untick_UnknownItem_Returns404WithMealItemNotFoundCode()
    {
        var client = await TestActors.Client(factory).CreateAsync();
        var seeded = await MealItemPlanSeeder.SeedAsync(factory, client.UserId);

        var response = await client.Http.DeleteAsync(ItemRoute(seeded.MealId, Guid.NewGuid()), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        body.RootElement.GetProperty("errorCode").GetString().Should().Be("MEAL_ITEM_NOT_FOUND");
    }

    [Fact]
    public async Task Untick_MealNotInThePlan_Returns404()
    {
        var client = await TestActors.Client(factory).CreateAsync();
        var seeded = await MealItemPlanSeeder.SeedAsync(factory, client.UserId);

        var response = await client.Http.DeleteAsync(ItemRoute(Guid.NewGuid(), seeded.FoodAId), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private record StateDto(MealEatenState EatenState, List<Guid> EatenItemIds, DateTime? EatenAt);
}
