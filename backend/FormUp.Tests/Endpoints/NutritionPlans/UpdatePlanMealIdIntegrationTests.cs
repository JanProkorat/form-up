using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace FormUp.Tests.Endpoints.NutritionPlans;

/// <summary>
/// Integration tests for the meal id scoping of the full-state plan update: meal ids key the
/// client's diary photo folder, so an id that lives in another plan may never be stored here.
/// </summary>
[Collection(TestCollection.Shard2)]
public class UpdatePlanMealIdIntegrationTests(FitnessApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(Actor Nutritionist, Guid PlanId, Guid OtherPlanId)> SeedTwoClientsAsync(
        Guid[] planMealIds, Guid[] otherPlanMealIds)
    {
        var nutritionist = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var otherClient = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, nutritionist, client).AsRole(UserRole.Nutritionist).CreateAsync(Ct);
        await TestActors.Link(factory, nutritionist, otherClient).AsRole(UserRole.Nutritionist).CreateAsync(Ct);

        var planId = await SeedPlanAsync(nutritionist.UserId, client.UserId, planMealIds);
        var otherPlanId = await SeedPlanAsync(nutritionist.UserId, otherClient.UserId, otherPlanMealIds);
        return (nutritionist, planId, otherPlanId);
    }

    private async Task<Guid> SeedPlanAsync(Guid nutritionistId, Guid clientUserId, Guid[] mealIds)
    {
        var planId = Guid.NewGuid();
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        await mongo.NutritionPlans.InsertOneAsync(new NutritionPlan
        {
            ExternalId = planId,
            ClientId = clientUserId,
            NutritionistId = nutritionistId,
            Name = "Meal id plan",
            Status = NutritionPlanStatus.Draft,
            Version = 1,
            DateCreated = DateTime.UtcNow.AddDays(-1),
            Weeks =
            [
                new PlanWeek
                {
                    WeekNumber = 1,
                    Status = WeekStatus.Draft,
                    Days = mealIds
                        .Select((mealId, index) => new PlanDay
                        {
                            DayOfWeek = index + 1,
                            Meals = [new PlanMeal { MealId = mealId, Kind = MealKind.Breakfast, Order = 1 }],
                        })
                        .ToList(),
                },
            ],
        }, cancellationToken: Ct);
        return planId;
    }

    private async Task SeedMealLogAsync(Guid planId, Guid mealId)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        await mongo.MealLogs.InsertOneAsync(new MealLog
        {
            ClientId = Guid.NewGuid(),
            PlanId = planId,
            MealId = mealId,
            LogDate = DateTime.UtcNow.Date,
        }, cancellationToken: Ct);
    }

    /// <summary>Sends a full-state update with one breakfast per day; element N is day N+1's meal id.</summary>
    private static Task<HttpResponseMessage> PutAsync(Actor nutritionist, Guid planId, int version, Guid?[] dayMealIds) =>
        nutritionist.Http.PutAsJsonAsync($"/nutrition/plans/{planId}", new
        {
            PlanId = planId,
            Name = "Meal id plan",
            Version = version,
            Weeks = new[]
            {
                new
                {
                    WeekNumber = 1,
                    Days = dayMealIds.Select((mealId, index) => new
                    {
                        DayOfWeek = index + 1,
                        Meals = new[] { new { MealId = mealId, Kind = "Breakfast", Order = 1 } },
                    }).ToArray(),
                },
            },
        }, Ct);

    private async Task<List<Guid>> StoredMealIdsAsync(Guid planId)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        var plan = await mongo.NutritionPlans.Find(p => p.ExternalId == planId).FirstAsync(Ct);
        return plan.Weeks
            .SelectMany(w => w.Days.OrderBy(d => d.DayOfWeek))
            .SelectMany(d => d.Meals)
            .Select(m => m.MealId)
            .ToList();
    }

    [Fact]
    public async Task UpdatePlan_MealIdOfAnotherPlan_StoresFreshIdAndLeavesOtherPlanUnchanged()
    {
        var foreignMealId = Guid.NewGuid();
        var (nutritionist, planId, otherPlanId) = await SeedTwoClientsAsync([], [foreignMealId]);

        var response = await PutAsync(nutritionist, planId, 1, [foreignMealId]);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var stored = await StoredMealIdsAsync(planId);
        stored.Should().HaveCount(1);
        stored[0].Should().NotBe(foreignMealId).And.NotBe(Guid.Empty);
        (await StoredMealIdsAsync(otherPlanId)).Should().Equal(foreignMealId);
    }

    [Fact]
    public async Task UpdatePlan_MealIdOnlyOnAnotherPlansLogs_StoresFreshId()
    {
        var orphanedMealId = Guid.NewGuid();
        var (nutritionist, planId, otherPlanId) = await SeedTwoClientsAsync([], []);
        await SeedMealLogAsync(otherPlanId, orphanedMealId);

        var response = await PutAsync(nutritionist, planId, 1, [orphanedMealId]);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var stored = await StoredMealIdsAsync(planId);
        stored.Should().HaveCount(1);
        stored[0].Should().NotBe(orphanedMealId).And.NotBe(Guid.Empty);
    }

    [Fact]
    public async Task UpdatePlan_MealIdAlreadyInThisPlan_KeepsIt()
    {
        var ownMealId = Guid.NewGuid();
        var (nutritionist, planId, _) = await SeedTwoClientsAsync([ownMealId], []);

        var response = await PutAsync(nutritionist, planId, 1, [ownMealId]);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        (await StoredMealIdsAsync(planId)).Should().Equal(ownMealId);
    }

    [Fact]
    public async Task UpdatePlan_MealIdRemovedEarlierButOnThisPlansLogs_KeepsIt()
    {
        var removedMealId = Guid.NewGuid();
        var (nutritionist, planId, _) = await SeedTwoClientsAsync([], []);
        await SeedMealLogAsync(planId, removedMealId);

        var response = await PutAsync(nutritionist, planId, 1, [removedMealId]);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        (await StoredMealIdsAsync(planId)).Should().Equal(removedMealId);
    }

    [Fact]
    public async Task UpdatePlan_FreshClientMintedMealId_KeepsIt()
    {
        var freshMealId = Guid.NewGuid();
        var (nutritionist, planId, _) = await SeedTwoClientsAsync([], []);

        var response = await PutAsync(nutritionist, planId, 1, [freshMealId]);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        (await StoredMealIdsAsync(planId)).Should().Equal(freshMealId);
    }

    [Fact]
    public async Task UpdatePlan_SameMealIdOnTwoDays_KeepsFirstAndMintsSecond()
    {
        var repeatedMealId = Guid.NewGuid();
        var (nutritionist, planId, _) = await SeedTwoClientsAsync([], []);

        var response = await PutAsync(nutritionist, planId, 1, [repeatedMealId, repeatedMealId]);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var stored = await StoredMealIdsAsync(planId);
        stored.Should().HaveCount(2);
        stored[0].Should().Be(repeatedMealId);
        stored[1].Should().NotBe(repeatedMealId).And.NotBe(Guid.Empty);
    }

    [Fact]
    public async Task UpdatePlan_VersionMismatch_Returns409AndStoresNothing()
    {
        var foreignMealId = Guid.NewGuid();
        var (nutritionist, planId, _) = await SeedTwoClientsAsync([], [foreignMealId]);

        var response = await PutAsync(nutritionist, planId, 7, [foreignMealId]);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("PLAN_VERSION_CONFLICT");
        (await StoredMealIdsAsync(planId)).Should().BeEmpty();
    }
}
