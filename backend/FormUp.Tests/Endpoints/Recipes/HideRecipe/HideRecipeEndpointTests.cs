using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace FormUp.Tests.Endpoints.Recipes.HideRecipe;

/// <summary>
/// Real-Mongo tests for <c>PUT /recipes/{RecipeId}/hide|unhide</c> and the <c>hidden</c> search filter. The Mongo
/// mock ignores filters, so none of this can be proven there.
/// </summary>
[Collection(TestCollection.Name)]
public class HideRecipeEndpointTests(FitnessApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Recipe> InsertRecipeAsync(
        Guid ownerId,
        string token,
        RecipeVisibility visibility = RecipeVisibility.Public,
        string? mealType = null)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        var recipe = new Recipe
        {
            ExternalId = Guid.NewGuid(),
            NutritionistId = ownerId,
            Name = $"{token} {Guid.NewGuid():N}",
            Visibility = visibility,
            MealTypes = mealType is null ? null : [mealType],
            DateCreated = DateTime.UtcNow,
            Version = 3
        };
        await mongo.Recipes.InsertOneAsync(recipe, cancellationToken: Ct);

        return recipe;
    }

    private async Task<Recipe> LoadRecipeAsync(Guid recipeId)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        return await mongo.Recipes.Find(r => r.ExternalId == recipeId).FirstAsync(Ct);
    }

    private static string NewToken() => $"hide{Guid.NewGuid():N}";

    private static async Task<List<Guid>> SearchIdsAsync(HttpClient http, string query, string extra = "")
    {
        var response = await http.GetAsync($"/recipes?search={query}{extra}", Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: Ct);

        return body.GetProperty("recipes").EnumerateArray().Select(r => r.GetProperty("recipeId").GetGuid()).ToList();
    }

    [Fact]
    public async Task HideRecipe_ForeignPublicRecipe_Returns204RecordsOnlyTheCallerAndKeepsVersion()
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var recipe = await InsertRecipeAsync(Guid.NewGuid(), NewToken());

        var response = await coach.Http.PutAsJsonAsync($"/recipes/{recipe.ExternalId}/hide", new { }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var stored = await LoadRecipeAsync(recipe.ExternalId);
        stored.HiddenByNutritionistIds.Should().Equal(coach.UserId);
        stored.Version.Should().Be(3, "hiding is a view preference and must not invalidate the owner's edit");
    }

    [Fact]
    public async Task HideRecipe_Twice_StaysIdempotent()
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var recipe = await InsertRecipeAsync(Guid.NewGuid(), NewToken());

        var first = await coach.Http.PutAsJsonAsync($"/recipes/{recipe.ExternalId}/hide", new { }, Ct);
        var second = await coach.Http.PutAsJsonAsync($"/recipes/{recipe.ExternalId}/hide", new { }, Ct);

        first.StatusCode.Should().Be(HttpStatusCode.NoContent);
        second.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await LoadRecipeAsync(recipe.ExternalId)).HiddenByNutritionistIds.Should().Equal(coach.UserId);
    }

    [Fact]
    public async Task HideRecipe_OwnRecipe_Returns400HideOwnNotAllowed()
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var recipe = await InsertRecipeAsync(coach.UserId, NewToken());

        var response = await coach.Http.PutAsJsonAsync($"/recipes/{recipe.ExternalId}/hide", new { }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.RecipeHideOwnNotAllowed);
        (await LoadRecipeAsync(recipe.ExternalId)).HiddenByNutritionistIds.Should().BeEmpty();
    }

    [Theory]
    [InlineData("hide")]
    [InlineData("unhide")]
    public async Task HideOrUnhideRecipe_PrivateForeignRecipe_Returns404(string action)
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var recipe = await InsertRecipeAsync(Guid.NewGuid(), NewToken(), RecipeVisibility.Private);

        var response = await coach.Http.PutAsJsonAsync($"/recipes/{recipe.ExternalId}/{action}", new { }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("hide")]
    [InlineData("unhide")]
    public async Task HideOrUnhideRecipe_UnknownId_Returns404(string action)
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);

        var response = await coach.Http.PutAsJsonAsync($"/recipes/{Guid.NewGuid()}/{action}", new { }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UnhideRecipe_NotHidden_Returns204()
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var recipe = await InsertRecipeAsync(Guid.NewGuid(), NewToken());

        var response = await coach.Http.PutAsJsonAsync($"/recipes/{recipe.ExternalId}/unhide", new { }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Search_HiddenRecipe_IsExcludedForTheHiderOnlyAndRestoredByUnhide()
    {
        var hider = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var other = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var token = NewToken();
        var hiddenRecipe = await InsertRecipeAsync(Guid.NewGuid(), token);
        var keptRecipe = await InsertRecipeAsync(Guid.NewGuid(), token);
        (await hider.Http.PutAsJsonAsync($"/recipes/{hiddenRecipe.ExternalId}/hide", new { }, Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await SearchIdsAsync(hider.Http, token)).Should().BeEquivalentTo([keptRecipe.ExternalId]);
        (await SearchIdsAsync(hider.Http, token, "&hidden=true")).Should().BeEquivalentTo([hiddenRecipe.ExternalId]);
        (await SearchIdsAsync(other.Http, token)).Should().BeEquivalentTo([hiddenRecipe.ExternalId, keptRecipe.ExternalId]);
        (await SearchIdsAsync(other.Http, token, "&hidden=true")).Should().BeEmpty();

        (await hider.Http.PutAsJsonAsync($"/recipes/{hiddenRecipe.ExternalId}/unhide", new { }, Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await SearchIdsAsync(hider.Http, token)).Should().BeEquivalentTo([hiddenRecipe.ExternalId, keptRecipe.ExternalId]);
        (await SearchIdsAsync(hider.Http, token, "&hidden=true")).Should().BeEmpty();
    }

    [Fact]
    public async Task Search_HiddenTrue_ComposesWithMealTypeOwnerAndQueryFilters()
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var token = NewToken();
        var hiddenDinner = await InsertRecipeAsync(Guid.NewGuid(), token, mealType: "Dinner");
        var hiddenLunch = await InsertRecipeAsync(Guid.NewGuid(), token, mealType: "Lunch");
        await InsertRecipeAsync(Guid.NewGuid(), token, mealType: "Dinner");
        await coach.Http.PutAsJsonAsync($"/recipes/{hiddenDinner.ExternalId}/hide", new { }, Ct);
        await coach.Http.PutAsJsonAsync($"/recipes/{hiddenLunch.ExternalId}/hide", new { }, Ct);

        (await SearchIdsAsync(coach.Http, token, "&hidden=true")).Should().BeEquivalentTo([hiddenDinner.ExternalId, hiddenLunch.ExternalId]);
        (await SearchIdsAsync(coach.Http, token, "&hidden=true&mealType=Lunch")).Should().BeEquivalentTo([hiddenLunch.ExternalId]);
        (await SearchIdsAsync(coach.Http, token, "&hidden=true&owner=OtherCoaches")).Should().HaveCount(2);
        (await SearchIdsAsync(coach.Http, token, "&hidden=true&owner=Mine")).Should().BeEmpty();
        (await SearchIdsAsync(coach.Http, $"{token}-no-such-name", "&hidden=true")).Should().BeEmpty();
    }

    [Fact]
    public async Task Search_Response_ExposesOnlyTheCallersOwnHiddenFlag()
    {
        var hider = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var token = NewToken();
        var recipe = await InsertRecipeAsync(Guid.NewGuid(), token);
        await hider.Http.PutAsJsonAsync($"/recipes/{recipe.ExternalId}/hide", new { }, Ct);

        var response = await hider.Http.GetAsync($"/recipes?search={token}&hidden=true", Ct);
        var raw = await response.Content.ReadAsStringAsync(Ct);

        using var json = JsonDocument.Parse(raw);
        json.RootElement.GetProperty("recipes")[0].GetProperty("isHiddenByCurrentUser").GetBoolean().Should().BeTrue();
        raw.Should().NotContainEquivalentOf("hiddenByNutritionistIds");
        raw.Should().NotContain(hider.UserId.ToString());
    }
}
