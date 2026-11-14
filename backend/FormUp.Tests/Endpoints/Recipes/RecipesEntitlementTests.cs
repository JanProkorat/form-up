using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Domain.Constants;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;

namespace FormUp.Tests.Endpoints.Recipes;

/// <summary>
/// A coach whose plan lacks recipes cannot write their own recipe library; searching and reading recipes stays open.
/// </summary>
[Collection(TestCollection.Name)]
public class RecipesEntitlementTests(FitnessApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Actor> NutritionistAsync(bool canUseRecipes)
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.MoveToPlanAsync(factory, coach, plan => plan.CanUseRecipes = canUseRecipes);

        return coach;
    }

    [Fact]
    public async Task OwnLibraryWrites_PlanWithoutRecipes_Return403SubscriptionRequired()
    {
        var coach = await NutritionistAsync(canUseRecipes: false);
        var recipeId = Guid.NewGuid();

        var responses = new Dictionary<string, HttpResponseMessage>
        {
            ["DELETE /recipes/{id}"] = await coach.Http.DeleteAsync($"/recipes/{recipeId}", Ct),
            ["PUT /trainer/recipes/{id}/tags"] = await coach.Http.PutAsJsonAsync(
                $"/trainer/recipes/{recipeId}/tags", new { TagIds = Array.Empty<Guid>() }, Ct),
            ["DELETE /recipes/{id}/image"] = await coach.Http.DeleteAsync($"/recipes/{recipeId}/image", Ct),
        };

        foreach (var (call, response) in responses)
        {
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, call);
            (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.SubscriptionRequired, call);
        }
    }

    [Fact]
    public async Task CreateRecipe_PlanWithoutRecipes_Returns403SubscriptionRequired()
    {
        var coach = await NutritionistAsync(canUseRecipes: false);

        var response = await coach.Http.PostAsJsonAsync(
            "/recipes", new
            {
                Name = "Gate recipe",
                Servings = 2,
                MealTypes = new[] { "Lunch" },
                Foods = new[] { new { FoodExternalId = Guid.NewGuid(), AmountGrams = 100 } },
            }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.SubscriptionRequired);
    }

    [Fact]
    public async Task SearchAndGetRecipe_PlanWithoutRecipes_StayOpen()
    {
        var coach = await NutritionistAsync(canUseRecipes: false);

        var search = await coach.Http.GetAsync("/recipes", Ct);
        var get = await coach.Http.GetAsync($"/recipes/{Guid.NewGuid()}", Ct);

        search.StatusCode.Should().Be(HttpStatusCode.OK);
        get.StatusCode.Should().Be(HttpStatusCode.NotFound, "a missing recipe is a 404, not a subscription refusal");
    }

    [Fact]
    public async Task OwnLibraryWrites_PlanWithRecipes_ReachTheEndpoint()
    {
        var coach = await NutritionistAsync(canUseRecipes: true);

        var response = await coach.Http.DeleteAsync($"/recipes/{Guid.NewGuid()}", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
