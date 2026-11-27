using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.ClientNutrition.AddOffPlanFood;
using FormUp.Application.Features.ClientNutrition.GetTodayLog;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace FormUp.Tests.Endpoints.ClientNutrition;

/// <summary>
/// Integration tests (real MongoDB) for <c>POST /client/nutrition/log/off-plan-foods</c>,
/// <c>DELETE /client/nutrition/log/off-plan-foods/{EntryId}</c> and their effect on
/// <c>GET /client/nutrition/log/today</c>.
/// </summary>
[Collection(TestCollection.Shard4)]
public class OffPlanFoodEndpointTests(FitnessApiFactory factory)
{
    private static readonly DateTime Start = ClientNutritionFixtures.CurrentWeekMondayUtc();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private Task<NutritionPlan> InsertActivePlanAsync(Actor client, Guid? nutritionistId = null) =>
        ClientNutritionFixtures.InsertPlanAsync(factory, client.UserId, NutritionPlanStatus.Active, Start, nutritionistId, null,
            ClientNutritionFixtures.Week(1, WeekStatus.Published, ClientNutritionFixtures.MealWithFoods(MealKind.Lunch)));

    private static Task<HttpResponseMessage> AddAsync(Actor client, Guid foodId, decimal grams) =>
        client.Http.PostAsJsonAsync("/client/nutrition/log/off-plan-foods",
            new AddOffPlanFoodRequest { FoodExternalId = foodId, AmountGrams = grams },
            TestContext.Current.CancellationToken);

    private static async Task<GetTodayLogResponse> GetTodayLogAsync(Actor client)
    {
        var response = await client.Http.GetAsync("/client/nutrition/log/today", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<GetTodayLogResponse>(Json, TestContext.Current.CancellationToken))!;
    }

    private async Task<List<DayLog>> LoadDayLogsAsync(Guid clientId)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        return await mongo.DayLogs.Find(l => l.ClientId == clientId).ToListAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Add_ValidFood_ReturnsComputedMacros_AndCountsInTodayLogTotals()
    {
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        await InsertActivePlanAsync(client);
        var food = await ClientNutritionFixtures.InsertFoodAsync(factory, kcalPer100: 200);

        var response = await AddAsync(client, food.ExternalId, 150);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var entry = (await response.Content.ReadFromJsonAsync<AddOffPlanFoodResponse>(Json, TestContext.Current.CancellationToken))!;
        entry.Kcal.Should().Be(300);
        entry.Protein.Should().Be(15);
        entry.Carbs.Should().Be(30);
        entry.Fat.Should().Be(7.5m);
        entry.Fiber.Should().Be(3);

        var log = await GetTodayLogAsync(client);
        log.OffPlanFoods.Should().ContainSingle(e => e.EntryId == entry.EntryId && e.FoodExternalId == food.ExternalId);
        log.TotalConsumed.Kcal.Should().Be(300);
        log.TotalConsumed.Protein.Should().Be(15);
    }

    [Fact]
    public async Task Add_Twice_AppendsToSameDayLogAndBumpsVersion()
    {
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        await InsertActivePlanAsync(client);
        var food = await ClientNutritionFixtures.InsertFoodAsync(factory);

        (await AddAsync(client, food.ExternalId, 100)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await AddAsync(client, food.ExternalId, 50)).StatusCode.Should().Be(HttpStatusCode.OK);

        var logs = await LoadDayLogsAsync(client.UserId);
        var dayLog = logs.Should().ContainSingle().Subject;
        dayLog.OffPlanFoods.Should().HaveCount(2);
        dayLog.Version.Should().Be(2);
    }

    [Fact]
    public async Task Add_ToLegacyDayLogWithoutOffPlanField_Works()
    {
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        var plan = await InsertActivePlanAsync(client);
        var food = await ClientNutritionFixtures.InsertFoodAsync(factory);

        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
            var legacy = new DayLog { ClientId = client.UserId, PlanId = plan.ExternalId, LogDate = DateTime.UtcNow.Date };
            await mongo.DayLogs.InsertOneAsync(legacy, cancellationToken: TestContext.Current.CancellationToken);
            await mongo.DayLogs.UpdateOneAsync(
                Builders<DayLog>.Filter.Eq(l => l.Id, legacy.Id),
                Builders<DayLog>.Update.Unset("offPlanFoods"),
                cancellationToken: TestContext.Current.CancellationToken);
        }

        (await GetTodayLogAsync(client)).OffPlanFoods.Should().BeEmpty();

        (await AddAsync(client, food.ExternalId, 100)).StatusCode.Should().Be(HttpStatusCode.OK);

        var dayLog = (await LoadDayLogsAsync(client.UserId)).Should().ContainSingle().Subject;
        dayLog.OffPlanFoods.Should().ContainSingle();
        dayLog.Version.Should().Be(2);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(NutritionPlanStatus.Draft)]
    public async Task Add_WithoutActivePlan_Returns404(NutritionPlanStatus? planStatus)
    {
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        var food = await ClientNutritionFixtures.InsertFoodAsync(factory);

        if (planStatus.HasValue)
        {
            await ClientNutritionFixtures.InsertPlanAsync(factory, client.UserId, planStatus.Value, Start, null, null,
                ClientNutritionFixtures.Week(1, WeekStatus.Published));
        }

        var response = await AddAsync(client, food.ExternalId, 100);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Add_UnknownFood_Returns404()
    {
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        await InsertActivePlanAsync(client);

        var response = await AddAsync(client, Guid.NewGuid(), 100);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Add_PrivateFoodOfAnotherNutritionist_Returns404_ButPlanAuthorsPrivateFoodWorks()
    {
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        var planAuthor = Guid.NewGuid();
        await InsertActivePlanAsync(client, planAuthor);
        var foreign = await ClientNutritionFixtures.InsertFoodAsync(factory, visibility: FoodVisibility.Private, nutritionistId: Guid.NewGuid());
        var authors = await ClientNutritionFixtures.InsertFoodAsync(factory, visibility: FoodVisibility.Private, nutritionistId: planAuthor);

        (await AddAsync(client, foreign.ExternalId, 100)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await AddAsync(client, authors.ExternalId, 100)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Add_NonPositiveAmount_Returns400()
    {
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        await InsertActivePlanAsync(client);
        var food = await ClientNutritionFixtures.InsertFoodAsync(factory);

        var response = await AddAsync(client, food.ExternalId, 0);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Remove_OwnEntry_Returns204_AndTotalsDrop()
    {
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        await InsertActivePlanAsync(client);
        var food = await ClientNutritionFixtures.InsertFoodAsync(factory, kcalPer100: 100);
        var added = (await (await AddAsync(client, food.ExternalId, 100))
            .Content.ReadFromJsonAsync<AddOffPlanFoodResponse>(Json, TestContext.Current.CancellationToken))!;
        var other = (await (await AddAsync(client, food.ExternalId, 200))
            .Content.ReadFromJsonAsync<AddOffPlanFoodResponse>(Json, TestContext.Current.CancellationToken))!;

        var response = await client.Http.DeleteAsync($"/client/nutrition/log/off-plan-foods/{added.EntryId}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var log = await GetTodayLogAsync(client);
        log.OffPlanFoods.Select(e => e.EntryId).Should().Equal(other.EntryId);
        log.TotalConsumed.Kcal.Should().Be(200);
        (await LoadDayLogsAsync(client.UserId)).Single().Version.Should().Be(3);
    }

    [Fact]
    public async Task Remove_UnknownEntry_Returns404()
    {
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        await InsertActivePlanAsync(client);

        var response = await client.Http.DeleteAsync($"/client/nutrition/log/off-plan-foods/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Remove_AnotherClientsEntry_Returns404_AndLeavesItUntouched()
    {
        var owner = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        var intruder = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        await InsertActivePlanAsync(owner);
        var food = await ClientNutritionFixtures.InsertFoodAsync(factory);
        var added = (await (await AddAsync(owner, food.ExternalId, 100))
            .Content.ReadFromJsonAsync<AddOffPlanFoodResponse>(Json, TestContext.Current.CancellationToken))!;

        var response = await intruder.Http.DeleteAsync($"/client/nutrition/log/off-plan-foods/{added.EntryId}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await LoadDayLogsAsync(owner.UserId)).Single().OffPlanFoods.Should().ContainSingle();
    }

    [Fact]
    public async Task Remove_EntryFromAnEarlierDay_Returns404()
    {
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        var plan = await InsertActivePlanAsync(client);
        var entryId = Guid.NewGuid();
        var yesterday = DateTime.UtcNow.Date.AddDays(-1);

        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
            await mongo.DayLogs.InsertOneAsync(new DayLog
            {
                ClientId = client.UserId,
                PlanId = plan.ExternalId,
                LogDate = yesterday,
                CreatedAt = yesterday.AddHours(12),
                OffPlanFoods = [new OffPlanFoodEntry { EntryId = entryId, FoodName = "Old" }],
            }, cancellationToken: TestContext.Current.CancellationToken);
        }

        var response = await client.Http.DeleteAsync($"/client/nutrition/log/off-plan-foods/{entryId}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
