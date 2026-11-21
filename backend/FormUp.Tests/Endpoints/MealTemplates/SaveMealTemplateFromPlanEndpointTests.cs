using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Features.MealTemplates.Shared;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.MealTemplates;

/// <summary>
/// Integration test for <c>POST /nutrition/meal-templates/from-plan</c>
/// (<see cref="Application.Features.MealTemplates.SaveMealTemplateFromPlan.SaveMealTemplateFromPlanEndpoint"/>) —
/// only the success path, which calls <c>Send.CreatedAtAsync</c> and therefore needs the real
/// <c>LinkGenerator</c> that <see cref="FitnessApiFactory"/> provides (unavailable in the
/// lightweight <c>Factory.Create&lt;T&gt;()</c> host used by <see cref="MealTemplateEndpointTests"/>
/// for the 404 guard-branch cases). Same precedent as <c>SendClientRequestEndpointTests</c>.
/// </summary>
[Collection(TestCollection.Shard3)]
public class SaveMealTemplateFromPlanEndpointTests(FitnessApiFactory factory)
{
    // The API serializes enums as strings (JsonStringEnumConverter globally), so use matching
    // options when deserializing the test response — see the same pattern in
    // GetFullTrainingPlanIntegrationTests / QaSeedRunnerTests.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static string UniqueEmail() => $"{Guid.NewGuid():N}@save-meal-template-from-plan-test.com";

    private async Task<(HttpClient Client, Guid NutritionistId)> RegisterNutritionistAsync()
    {
        var client = factory.CreateClient();
        var email = UniqueEmail();
        await TestHelpers.RegisterAsync(client, email, "TestPass1!", "SaveFromPlan", "MealTemplateTest", "Nutritionist");
        var (accessToken, _) = await TestHelpers.LoginAsync(client, email, "TestPass1!");
        TestHelpers.SetBearerToken(client, accessToken);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await db.Users.FirstAsync(
            u => u.Email == email, TestContext.Current.CancellationToken);

        return (client, user.Id);
    }

    private async Task<(NutritionPlan Plan, PlanMeal Meal)> InsertPlanWithMealAsync(
        Guid nutritionistId, Guid clientUserId)
    {
        var meal = new PlanMeal
        {
            MealId = Guid.NewGuid(),
            Kind = MealKind.Lunch,
            Order = 1,
            Foods =
            [
                new MealFood
                {
                    FoodExternalId = Guid.NewGuid(),
                    FoodName = "Rice",
                    NutrientValuePer100Grams = new NutrientValue { Kcal = 130, Protein = 2.7m, Carbs = 28.2m, Fat = 0.3m },
                    AmountGrams = 200
                }
            ]
        };

        var plan = new NutritionPlan
        {
            ExternalId = Guid.NewGuid(),
            NutritionistId = nutritionistId,
            ClientId = clientUserId,
            Name = "Test Plan",
            Weeks =
            [
                new PlanWeek
                {
                    WeekNumber = 1,
                    Days = [new PlanDay { DayOfWeek = 1, Meals = [meal] }]
                }
            ]
        };

        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        await mongo.NutritionPlans.InsertOneAsync(plan, cancellationToken: TestContext.Current.CancellationToken);
        return (plan, meal);
    }

    [Fact]
    public async Task SaveMealTemplateFromPlan_ValidRequest_CopiesFoodsAndInheritsKind()
    {
        var (client, nutritionistId) = await RegisterNutritionistAsync();
        // Plan routes authorize on the live link, so the plan must belong to a client
        // this nutritionist is actually linked to.
        var linkedClientId = await TestHelpers.RegisterLinkedClientAsync(
            factory, nutritionistId, TestContext.Current.CancellationToken);
        var (plan, meal) = await InsertPlanWithMealAsync(nutritionistId, linkedClientId);

        var response = await client.PostAsJsonAsync(
            "/nutrition/meal-templates/from-plan",
            new
            {
                PlanId = plan.ExternalId,
                WeekNumber = 1,
                DayOfWeek = 1,
                MealId = meal.MealId,
                Name = "From Plan Meal",
                Visibility = LibraryVisibility.Private
            },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var body = await response.Content.ReadFromJsonAsync<MealTemplateDetailResponse>(
            JsonOptions, TestContext.Current.CancellationToken);
        body!.Kind.Should().Be(MealKind.Lunch);
        body.Foods.Should().HaveCount(1);
        body.Foods[0].FoodExternalId.Should().Be(meal.Foods[0].FoodExternalId);

        // Same underlying foods/recipes must report identical totals whether inside the plan
        // (via RecalculateTotals) or the new template (#859 single-summation AC).
        using var scope = factory.Services.CreateScope();
        var macroCalculator = scope.ServiceProvider.GetRequiredService<IMacroCalculatorService>();
        var expectedTotals = macroCalculator.CalculateMealTotals(meal.Foods, meal.Recipes);
        body.TotalNutrients.Kcal.Should().Be(expectedTotals.Kcal);
    }

    /// <summary>
    /// Deny-path test for the link-authorization guard itself (not authorship). The plan is
    /// owned by the caller, but the caller's link to the plan's client no longer grants nutrition
    /// access — this must 404 (same shaped denial as a missing plan), never a 200. If
    /// <see cref="IClientLinkAuthorizationService"/> were removed from this guard, this test
    /// would regress to 201.
    /// </summary>
    [Fact]
    public async Task SaveMealTemplateFromPlan_LinkGrantsOnlyTraining_Returns404()
    {
        var (client, nutritionistId) = await RegisterNutritionistAsync();

        // Link exists but grants only the training domain — must not admit a nutrition route.
        var linkedClientId = await TestHelpers.RegisterLinkedClientAsync(
            factory, nutritionistId, TestContext.Current.CancellationToken,
            canViewNutritionPlans: false, canViewTrainingPlans: true);
        var (plan, meal) = await InsertPlanWithMealAsync(nutritionistId, linkedClientId);

        var response = await client.PostAsJsonAsync(
            "/nutrition/meal-templates/from-plan",
            new
            {
                PlanId = plan.ExternalId,
                WeekNumber = 1,
                DayOfWeek = 1,
                MealId = meal.MealId,
                Name = "Denied Meal Template",
                Visibility = LibraryVisibility.Private
            },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
