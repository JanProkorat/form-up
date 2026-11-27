using System.Net;
using System.Text.Json;
using System.Net.Http.Json;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace FormUp.Tests.Endpoints.Foods.HideFood;

/// <summary>
/// Real-Mongo tests for <c>PUT /foods/{FoodId}/hide|unhide</c> and the <c>hidden</c> search filter. The Mongo mock
/// ignores filters, so none of this can be proven there.
/// </summary>
[Collection(TestCollection.Name)]
public class HideFoodEndpointTests(FitnessApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Food> InsertFoodAsync(
        Guid? ownerId,
        string token,
        FoodVisibility visibility = FoodVisibility.Public,
        FoodCategory category = FoodCategory.Dairy)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        var food = new Food
        {
            ExternalId = Guid.NewGuid(),
            Name = $"{token} {Guid.NewGuid():N}",
            NutritionistId = ownerId,
            Visibility = visibility,
            Category = category,
            NutrientValue = new NutrientValue { Kcal = 100, Protein = 5, Carbs = 10, Fat = 3 },
            DateCreated = DateTime.UtcNow
        };
        await mongo.Foods.InsertOneAsync(food, cancellationToken: Ct);

        return food;
    }

    private async Task<Food> LoadFoodAsync(Guid foodId)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        return await mongo.Foods.Find(f => f.ExternalId == foodId).FirstAsync(Ct);
    }

    private static string NewToken() => $"hide{Guid.NewGuid():N}";

    private static async Task<List<Guid>> SearchIdsAsync(HttpClient http, string query, string extra = "")
    {
        var response = await http.GetAsync($"/foods/search?q={query}{extra}", Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: Ct);

        return body.GetProperty("foods").EnumerateArray().Select(f => f.GetProperty("foodId").GetGuid()).ToList();
    }

    [Fact]
    public async Task HideFood_ForeignPublicFood_Returns204AndRecordsOnlyTheCaller()
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var food = await InsertFoodAsync(Guid.NewGuid(), NewToken());

        var response = await coach.Http.PutAsJsonAsync($"/foods/{food.ExternalId}/hide", new { }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await LoadFoodAsync(food.ExternalId)).HiddenByNutritionistIds.Should().Equal(coach.UserId);
    }

    [Fact]
    public async Task HideFood_Twice_StaysIdempotent()
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var food = await InsertFoodAsync(Guid.NewGuid(), NewToken());

        var first = await coach.Http.PutAsJsonAsync($"/foods/{food.ExternalId}/hide", new { }, Ct);
        var second = await coach.Http.PutAsJsonAsync($"/foods/{food.ExternalId}/hide", new { }, Ct);

        first.StatusCode.Should().Be(HttpStatusCode.NoContent);
        second.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await LoadFoodAsync(food.ExternalId)).HiddenByNutritionistIds.Should().Equal(coach.UserId);
    }

    [Fact]
    public async Task HideFood_OwnFood_Returns400HideOwnNotAllowed()
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var food = await InsertFoodAsync(coach.UserId, NewToken());

        var response = await coach.Http.PutAsJsonAsync($"/foods/{food.ExternalId}/hide", new { }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.FoodHideOwnNotAllowed);
        (await LoadFoodAsync(food.ExternalId)).HiddenByNutritionistIds.Should().BeEmpty();
    }

    [Theory]
    [InlineData("hide")]
    [InlineData("unhide")]
    public async Task HideOrUnhideFood_PrivateForeignFood_Returns404(string action)
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var food = await InsertFoodAsync(Guid.NewGuid(), NewToken(), FoodVisibility.Private);

        var response = await coach.Http.PutAsJsonAsync($"/foods/{food.ExternalId}/{action}", new { }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("hide")]
    [InlineData("unhide")]
    public async Task HideOrUnhideFood_UnknownId_Returns404(string action)
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);

        var response = await coach.Http.PutAsJsonAsync($"/foods/{Guid.NewGuid()}/{action}", new { }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UnhideFood_NotHidden_Returns204()
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var food = await InsertFoodAsync(Guid.NewGuid(), NewToken());

        var response = await coach.Http.PutAsJsonAsync($"/foods/{food.ExternalId}/unhide", new { }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Search_HiddenFood_IsExcludedForTheHiderOnlyAndRestoredByUnhide()
    {
        var hider = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var other = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var token = NewToken();
        var hiddenFood = await InsertFoodAsync(Guid.NewGuid(), token);
        var keptFood = await InsertFoodAsync(Guid.NewGuid(), token);
        (await hider.Http.PutAsJsonAsync($"/foods/{hiddenFood.ExternalId}/hide", new { }, Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var hiderDefault = await SearchIdsAsync(hider.Http, token);
        var hiderHiddenOnly = await SearchIdsAsync(hider.Http, token, "&hidden=true");
        var otherDefault = await SearchIdsAsync(other.Http, token);
        var otherHiddenOnly = await SearchIdsAsync(other.Http, token, "&hidden=true");

        hiderDefault.Should().BeEquivalentTo([keptFood.ExternalId]);
        hiderHiddenOnly.Should().BeEquivalentTo([hiddenFood.ExternalId]);
        otherDefault.Should().BeEquivalentTo([hiddenFood.ExternalId, keptFood.ExternalId]);
        otherHiddenOnly.Should().BeEmpty();

        (await hider.Http.PutAsJsonAsync($"/foods/{hiddenFood.ExternalId}/unhide", new { }, Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await SearchIdsAsync(hider.Http, token)).Should().BeEquivalentTo([hiddenFood.ExternalId, keptFood.ExternalId]);
        (await SearchIdsAsync(hider.Http, token, "&hidden=true")).Should().BeEmpty();
        (await LoadFoodAsync(hiddenFood.ExternalId)).HiddenByNutritionistIds.Should().BeEmpty();
    }

    [Fact]
    public async Task Search_HiddenTrue_ComposesWithCategoryOwnerAndQueryFilters()
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var token = NewToken();
        var hiddenDairy = await InsertFoodAsync(Guid.NewGuid(), token, category: FoodCategory.Dairy);
        var hiddenFruit = await InsertFoodAsync(Guid.NewGuid(), token, category: FoodCategory.Fruit);
        var visibleDairy = await InsertFoodAsync(Guid.NewGuid(), token, category: FoodCategory.Dairy);
        await coach.Http.PutAsJsonAsync($"/foods/{hiddenDairy.ExternalId}/hide", new { }, Ct);
        await coach.Http.PutAsJsonAsync($"/foods/{hiddenFruit.ExternalId}/hide", new { }, Ct);

        (await SearchIdsAsync(coach.Http, token, "&hidden=true")).Should().BeEquivalentTo([hiddenDairy.ExternalId, hiddenFruit.ExternalId]);
        (await SearchIdsAsync(coach.Http, token, "&hidden=true&category=Fruit")).Should().BeEquivalentTo([hiddenFruit.ExternalId]);
        (await SearchIdsAsync(coach.Http, token, "&hidden=true&owner=OtherCoaches")).Should().HaveCount(2);
        (await SearchIdsAsync(coach.Http, token, "&hidden=true&owner=Mine")).Should().BeEmpty();
        (await SearchIdsAsync(coach.Http, $"{token}-no-such-name", "&hidden=true")).Should().BeEmpty();
        (await SearchIdsAsync(coach.Http, token, "&category=Dairy")).Should().BeEquivalentTo([visibleDairy.ExternalId]);
    }

    [Fact]
    public async Task Search_Response_ExposesOnlyTheCallersOwnHiddenFlag()
    {
        var hider = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var token = NewToken();
        var food = await InsertFoodAsync(Guid.NewGuid(), token);
        await hider.Http.PutAsJsonAsync($"/foods/{food.ExternalId}/hide", new { }, Ct);

        var response = await hider.Http.GetAsync($"/foods/search?q={token}&hidden=true", Ct);
        var raw = await response.Content.ReadAsStringAsync(Ct);

        using var json = JsonDocument.Parse(raw);
        json.RootElement.GetProperty("foods")[0].GetProperty("isHiddenByCurrentUser").GetBoolean().Should().BeTrue();
        raw.Should().NotContainEquivalentOf("hiddenByNutritionistIds");
        raw.Should().NotContain(hider.UserId.ToString());
    }
}
