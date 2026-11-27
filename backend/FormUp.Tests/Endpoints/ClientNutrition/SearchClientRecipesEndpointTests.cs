using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.ClientNutrition.SearchClientRecipes;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;

namespace FormUp.Tests.Endpoints.ClientNutrition;

/// <summary>
/// Integration tests (real MongoDB) for <c>GET /client/nutrition/recipes</c>.
/// </summary>
[Collection(TestCollection.Shard3)]
public class SearchClientRecipesEndpointTests(FitnessApiFactory factory)
{
    private static readonly DateTime Start = ClientNutritionFixtures.CurrentWeekMondayUtc();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static async Task<SearchClientRecipesResponse> ReadAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<SearchClientRecipesResponse>(Json, TestContext.Current.CancellationToken))!;

    [Fact]
    public async Task Search_AcrossCurrentAndPastPlans_ReturnsPairsNewestPlanFirstWithFlagsAndTotalHeader()
    {
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        var nutritionist = await TestActors.Nutritionist(factory).WithName("Nina", "Novak").CreateAsync(TestContext.Current.CancellationToken);
        var currentRecipe = await ClientNutritionFixtures.InsertRecipeAsync(factory, "Current soup");
        var nextWeekRecipe = await ClientNutritionFixtures.InsertRecipeAsync(factory, "Next week stew");
        var pastRecipe = await ClientNutritionFixtures.InsertRecipeAsync(factory, "Past salad");

        var current = await ClientNutritionFixtures.InsertPlanAsync(factory, client.UserId, NutritionPlanStatus.Active, Start,
            nutritionist.UserId, "Current plan",
            ClientNutritionFixtures.Week(1, WeekStatus.Published, ClientNutritionFixtures.MealWithRecipes(MealKind.Lunch, currentRecipe)),
            ClientNutritionFixtures.Week(2, WeekStatus.Published, ClientNutritionFixtures.MealWithRecipes(MealKind.Dinner, nextWeekRecipe)));
        var past = await ClientNutritionFixtures.InsertPlanAsync(factory, client.UserId, NutritionPlanStatus.Completed, Start.AddDays(-28),
            nutritionist.UserId, "Past plan",
            ClientNutritionFixtures.Week(1, WeekStatus.Published, ClientNutritionFixtures.MealWithRecipes(MealKind.Breakfast, pastRecipe)));

        var response = await client.Http.GetAsync("/client/nutrition/recipes", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("X-Total-Count").Single().Should().Be("3");
        var items = (await ReadAsync(response)).Items;
        items.Select(i => i.Name).Should().Equal("Current soup", "Next week stew", "Past salad");

        var soup = items[0];
        soup.PlanId.Should().Be(current.ExternalId);
        soup.PlanName.Should().Be("Current plan");
        soup.NutritionistName.Should().Be("Nina Novak");
        soup.IsCurrentPlan.Should().BeTrue();
        soup.IsThisWeek.Should().BeTrue();
        soup.Kcal.Should().Be(400);
        soup.Protein.Should().Be(20);
        soup.PrepTimeMinutes.Should().Be(15);
        soup.MealKinds.Should().Equal(MealKind.Lunch);
        soup.StartDate.Should().Be(Start);
        soup.EndDate.Should().Be(Start.AddDays(13));

        items[1].IsCurrentPlan.Should().BeTrue();
        items[1].IsThisWeek.Should().BeFalse("week 2 is not the current week");

        items[2].PlanId.Should().Be(past.ExternalId);
        items[2].IsCurrentPlan.Should().BeFalse();
        items[2].IsThisWeek.Should().BeFalse();
    }

    [Fact]
    public async Task Search_OtherClientsDraftArchivedAndUnpublishedRecipes_NeverAppear()
    {
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        var other = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        var visible = await ClientNutritionFixtures.InsertRecipeAsync(factory);
        var otherClients = await ClientNutritionFixtures.InsertRecipeAsync(factory);
        var inDraftPlan = await ClientNutritionFixtures.InsertRecipeAsync(factory);
        var inArchivedPlan = await ClientNutritionFixtures.InsertRecipeAsync(factory);
        var inDraftWeek = await ClientNutritionFixtures.InsertRecipeAsync(factory);

        await ClientNutritionFixtures.InsertPlanAsync(factory, client.UserId, NutritionPlanStatus.Active, Start, null, null,
            ClientNutritionFixtures.Week(1, WeekStatus.Published, ClientNutritionFixtures.MealWithRecipes(MealKind.Lunch, visible)),
            ClientNutritionFixtures.Week(2, WeekStatus.Draft, ClientNutritionFixtures.MealWithRecipes(MealKind.Lunch, inDraftWeek)));
        await ClientNutritionFixtures.InsertPlanAsync(factory, other.UserId, NutritionPlanStatus.Active, Start, null, null,
            ClientNutritionFixtures.Week(1, WeekStatus.Published, ClientNutritionFixtures.MealWithRecipes(MealKind.Lunch, otherClients)));
        await ClientNutritionFixtures.InsertPlanAsync(factory, client.UserId, NutritionPlanStatus.Draft, Start.AddDays(-70), null, null,
            ClientNutritionFixtures.Week(1, WeekStatus.Published, ClientNutritionFixtures.MealWithRecipes(MealKind.Lunch, inDraftPlan)));
        await ClientNutritionFixtures.InsertPlanAsync(factory, client.UserId, NutritionPlanStatus.Archived, Start.AddDays(-140), null, null,
            ClientNutritionFixtures.Week(1, WeekStatus.Published, ClientNutritionFixtures.MealWithRecipes(MealKind.Lunch, inArchivedPlan)));

        var response = await client.Http.GetAsync("/client/nutrition/recipes", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("X-Total-Count").Single().Should().Be("1");
        (await ReadAsync(response)).Items.Select(i => i.RecipeId).Should().Equal(visible.ExternalId);
    }

    [Fact]
    public async Task Search_PlanIdFilter_ReturnsOnlyThatPlansRecipes()
    {
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        var recipeA = await ClientNutritionFixtures.InsertRecipeAsync(factory);
        var recipeB = await ClientNutritionFixtures.InsertRecipeAsync(factory);
        var planA = await ClientNutritionFixtures.InsertPlanAsync(factory, client.UserId, NutritionPlanStatus.Active, Start, null, null,
            ClientNutritionFixtures.Week(1, WeekStatus.Published, ClientNutritionFixtures.MealWithRecipes(MealKind.Lunch, recipeA)));
        await ClientNutritionFixtures.InsertPlanAsync(factory, client.UserId, NutritionPlanStatus.Completed, Start.AddDays(-28), null, null,
            ClientNutritionFixtures.Week(1, WeekStatus.Published, ClientNutritionFixtures.MealWithRecipes(MealKind.Lunch, recipeB)));

        var response = await client.Http.GetAsync($"/client/nutrition/recipes?planId={planA.ExternalId}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("X-Total-Count").Single().Should().Be("1");
        (await ReadAsync(response)).Items.Select(i => i.RecipeId).Should().Equal(recipeA.ExternalId);
    }

    [Fact]
    public async Task Search_PlanIdOfAnotherClientsPlan_Returns404()
    {
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        var other = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        var recipe = await ClientNutritionFixtures.InsertRecipeAsync(factory);
        var otherPlan = await ClientNutritionFixtures.InsertPlanAsync(factory, other.UserId, NutritionPlanStatus.Active, Start, null, null,
            ClientNutritionFixtures.Week(1, WeekStatus.Published, ClientNutritionFixtures.MealWithRecipes(MealKind.Lunch, recipe)));

        var response = await client.Http.GetAsync($"/client/nutrition/recipes?planId={otherPlan.ExternalId}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Search_MealTypeFilter_ReturnsOnlyRecipesPlannedUnderThatKind()
    {
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        var lunch = await ClientNutritionFixtures.InsertRecipeAsync(factory);
        var dinner = await ClientNutritionFixtures.InsertRecipeAsync(factory);
        await ClientNutritionFixtures.InsertPlanAsync(factory, client.UserId, NutritionPlanStatus.Active, Start, null, null,
            ClientNutritionFixtures.Week(1, WeekStatus.Published,
                ClientNutritionFixtures.MealWithRecipes(MealKind.Lunch, lunch),
                ClientNutritionFixtures.MealWithRecipes(MealKind.Dinner, dinner)));

        var response = await client.Http.GetAsync("/client/nutrition/recipes?mealType=Dinner", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("X-Total-Count").Single().Should().Be("1");
        (await ReadAsync(response)).Items.Select(i => i.RecipeId).Should().Equal(dinner.ExternalId);
    }

    [Fact]
    public async Task Search_Paging_ReturnsRequestedPageAndFullTotal()
    {
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        var recipes = new List<Application.Domain.Documents.Recipe>();
        foreach (var letter in new[] { "A", "B", "C" })
        {
            recipes.Add(await ClientNutritionFixtures.InsertRecipeAsync(factory, $"Recipe {letter}"));
        }

        await ClientNutritionFixtures.InsertPlanAsync(factory, client.UserId, NutritionPlanStatus.Active, Start, null, null,
            ClientNutritionFixtures.Week(1, WeekStatus.Published, ClientNutritionFixtures.MealWithRecipes(MealKind.Lunch, recipes.ToArray())));

        var response = await client.Http.GetAsync("/client/nutrition/recipes?page=2&pageSize=2", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("X-Total-Count").Single().Should().Be("3");
        (await ReadAsync(response)).Items.Select(i => i.Name).Should().Equal("Recipe C");
    }

    [Fact]
    public async Task Search_InvalidPage_Returns400()
    {
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);

        var response = await client.Http.GetAsync("/client/nutrition/recipes?page=0", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
