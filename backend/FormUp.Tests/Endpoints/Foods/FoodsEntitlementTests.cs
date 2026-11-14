using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Domain.Constants;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;

namespace FormUp.Tests.Endpoints.Foods;

/// <summary>
/// A coach whose plan lacks ingredients cannot manage their own food library or tags; searching and reading foods
/// stays open.
/// </summary>
[Collection(TestCollection.Name)]
public class FoodsEntitlementTests(FitnessApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Actor> NutritionistAsync(bool canUseIngredients)
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.MoveToPlanAsync(factory, coach, plan => plan.CanUseIngredients = canUseIngredients);

        return coach;
    }

    [Fact]
    public async Task OwnLibraryEndpoints_PlanWithoutIngredients_Return403SubscriptionRequired()
    {
        var coach = await NutritionistAsync(canUseIngredients: false);
        var foodId = Guid.NewGuid();

        var responses = new Dictionary<string, HttpResponseMessage>
        {
            ["GET /foods/custom"] = await coach.Http.GetAsync("/foods/custom", Ct),
            ["DELETE /foods/{id}"] = await coach.Http.DeleteAsync($"/foods/{foodId}", Ct),
            ["GET /trainer/food-tags"] = await coach.Http.GetAsync("/trainer/food-tags", Ct),
            ["POST /trainer/food-tags"] = await coach.Http.PostAsJsonAsync("/trainer/food-tags", new { Name = "Gate tag", ColorHex = "#aabbcc" }, Ct),
            ["DELETE /foods/{id}/image"] = await coach.Http.DeleteAsync($"/foods/{foodId}/image", Ct),
        };

        foreach (var (call, response) in responses)
        {
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, call);
            (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.SubscriptionRequired, call);
        }
    }

    [Fact]
    public async Task CreateFood_PlanWithoutIngredients_Returns403SubscriptionRequired()
    {
        var coach = await NutritionistAsync(canUseIngredients: false);

        var response = await coach.Http.PostAsJsonAsync(
            "/foods",
            new
            {
                Name = "Gate food",
                NutrientValue = new { Kcal = 89, Protein = 10, Carbs = 10, Fat = 1 },
                Allergens = Array.Empty<string>(),
                CommonServings = new[] { new { Label = "1 piece", WeightGrams = 50 } },
            },
            Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.SubscriptionRequired);
    }

    [Fact]
    public async Task SearchAndGetFood_PlanWithoutIngredients_StayOpen()
    {
        var coach = await NutritionistAsync(canUseIngredients: false);

        var search = await coach.Http.GetAsync("/foods/search", Ct);
        var get = await coach.Http.GetAsync($"/foods/{Guid.NewGuid()}", Ct);

        search.StatusCode.Should().Be(HttpStatusCode.OK);
        get.StatusCode.Should().Be(HttpStatusCode.NotFound, "a missing food is a 404, not a subscription refusal");
    }

    [Fact]
    public async Task OwnLibraryEndpoints_PlanWithIngredients_ReachTheEndpoint()
    {
        var coach = await NutritionistAsync(canUseIngredients: true);

        var response = await coach.Http.GetAsync("/foods/custom", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
