using System.Net;
using FluentAssertions;
using FormUp.Application.Domain.Enums;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;

namespace FormUp.Tests.Endpoints.ClientNutrition;

/// <summary>
/// Integration tests (real MongoDB) for the client recipe-visibility rule on
/// <c>GET /client/recipes/{RecipeId}</c>.
/// </summary>
[Collection(TestCollection.Shard3)]
public class GetRecipeDetailEndpointTests(FitnessApiFactory factory)
{
    private static readonly DateTime Start = ClientNutritionFixtures.CurrentWeekMondayUtc();

    [Theory]
    [InlineData(NutritionPlanStatus.Active)]
    [InlineData(NutritionPlanStatus.Completed)]
    public async Task GetRecipeDetail_RecipeInPublishedWeekOfOwnPlan_Returns200(NutritionPlanStatus status)
    {
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        var recipe = await ClientNutritionFixtures.InsertRecipeAsync(factory);
        await ClientNutritionFixtures.InsertPlanAsync(factory, client.UserId, status, Start, null, null,
            ClientNutritionFixtures.Week(1, WeekStatus.Published, ClientNutritionFixtures.MealWithRecipes(MealKind.Lunch, recipe)));

        var response = await client.Http.GetAsync($"/client/recipes/{recipe.ExternalId}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetRecipeDetail_RecipeInAnotherClientsPlan_Returns404()
    {
        var caller = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        var other = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        var recipe = await ClientNutritionFixtures.InsertRecipeAsync(factory);
        await ClientNutritionFixtures.InsertPlanAsync(factory, other.UserId, NutritionPlanStatus.Active, Start, null, null,
            ClientNutritionFixtures.Week(1, WeekStatus.Published, ClientNutritionFixtures.MealWithRecipes(MealKind.Lunch, recipe)));

        var response = await caller.Http.GetAsync($"/client/recipes/{recipe.ExternalId}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(NutritionPlanStatus.Draft)]
    [InlineData(NutritionPlanStatus.Archived)]
    public async Task GetRecipeDetail_RecipeInDraftOrArchivedPlan_Returns404(NutritionPlanStatus status)
    {
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        var recipe = await ClientNutritionFixtures.InsertRecipeAsync(factory);
        await ClientNutritionFixtures.InsertPlanAsync(factory, client.UserId, status, Start, null, null,
            ClientNutritionFixtures.Week(1, WeekStatus.Published, ClientNutritionFixtures.MealWithRecipes(MealKind.Lunch, recipe)));

        var response = await client.Http.GetAsync($"/client/recipes/{recipe.ExternalId}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetRecipeDetail_RecipeOnlyInUnpublishedWeek_Returns404()
    {
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        var recipe = await ClientNutritionFixtures.InsertRecipeAsync(factory);
        await ClientNutritionFixtures.InsertPlanAsync(factory, client.UserId, NutritionPlanStatus.Active, Start, null, null,
            ClientNutritionFixtures.Week(1, WeekStatus.Draft, ClientNutritionFixtures.MealWithRecipes(MealKind.Lunch, recipe)));

        var response = await client.Http.GetAsync($"/client/recipes/{recipe.ExternalId}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetRecipeDetail_LibraryRecipeNotInAnyPlan_Returns404()
    {
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        var recipe = await ClientNutritionFixtures.InsertRecipeAsync(factory);

        var response = await client.Http.GetAsync($"/client/recipes/{recipe.ExternalId}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
