using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FormUp.Application.Domain.Enums;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;

namespace FormUp.Tests.Endpoints.Admin;

/// <summary>
/// Real-pipeline canary for #2088: an Admin who holds no coach role is refused on the coach, client, tag and
/// custom-list surfaces, while an Admin who also holds a coach role keeps that role's access.
/// </summary>
[Collection(TestCollection.Name)]
public class AdminOnlyRoleGateIntegrationTests(FitnessApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/trainer/pending-invites")]
    [InlineData("/client/profile/summary")]
    [InlineData("/trainer/food-tags")]
    [InlineData("/foods/custom")]
    [InlineData("/exercises/custom")]
    public async Task AdminOnly_OnCoachClientTagAndCustomListEndpoints_Returns403(string route)
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);

        var response = await admin.Http.GetAsync(route, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AdminOnly_OnTagWrite_Returns403()
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);

        var response = await admin.Http.PostAsJsonAsync(
            "/trainer/food-tags", new { Name = "Admin tag", ColorHex = "#aabbcc" }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AdminPlusTrainer_KeepsCoachAccessAndOwnLibraryItems()
    {
        var coach = await TestActors.Professional(factory, UserRole.Admin, UserRole.Trainer).CreateAsync(Ct);
        var name = $"Mixed Trainer Exercise {Guid.NewGuid():N}";

        (await coach.Http.GetAsync("/trainer/pending-invites", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await coach.Http.GetAsync("/exercises/custom", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var created = await coach.Http.PostAsJsonAsync("/exercises", new
        {
            Name = name,
            MuscleGroups = new[] { "Chest" },
            Equipment = "Barbell",
            Category = "Strength",
            Difficulty = "Beginner",
        }, Ct);
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        (await created.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(Ct))
            .GetProperty("isCustom").GetBoolean().Should().BeTrue();

        var search = await coach.Http.GetFromJsonAsync<System.Text.Json.JsonElement>(
            $"/exercises/search?Query={name}", Ct);
        search.GetProperty("exercises").EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()).Should().Contain(name);
    }

    [Fact]
    public async Task AdminPlusNutritionist_KeepsCoachAccessAndOwnLibraryItems()
    {
        var coach = await TestActors.Professional(factory, UserRole.Admin, UserRole.Nutritionist).CreateAsync(Ct);
        var name = $"Mixed Nutritionist Food {Guid.NewGuid():N}";

        (await coach.Http.GetAsync("/trainer/pending-invites", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await coach.Http.GetAsync("/foods/custom", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var created = await coach.Http.PostAsJsonAsync("/foods", new
        {
            Name = name,
            NutrientValue = new { Kcal = 89, Protein = 10, Carbs = 10, Fat = 1 },
            CommonServings = new[] { new { Label = "1 piece", WeightGrams = 50 } },
        }, Ct);
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        var body = await created.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(Ct);
        body.GetProperty("isSystem").GetBoolean().Should().BeFalse();
        body.GetProperty("isOwnedByCurrentUser").GetBoolean().Should().BeTrue();

        var recipe = await coach.Http.PostAsJsonAsync("/recipes", new
        {
            Name = $"Mixed Recipe {Guid.NewGuid():N}",
            MealTypes = new[] { "Dinner" },
            Foods = new[] { new { FoodExternalId = body.GetProperty("foodId").GetGuid(), AmountGrams = 100m } },
        }, Ct);
        recipe.StatusCode.Should().Be(HttpStatusCode.Created, await recipe.Content.ReadAsStringAsync(Ct));
        (await recipe.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(Ct))
            .GetProperty("isSystem").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task AdminPlusTrainer_OnRecipeAndFoodEndpoints_StaysRefused()
    {
        var coach = await TestActors.Professional(factory, UserRole.Admin, UserRole.Trainer).CreateAsync(Ct);

        (await coach.Http.GetAsync($"/recipes/{Guid.NewGuid()}", Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await coach.Http.PostAsJsonAsync("/recipes", new { }, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await coach.Http.DeleteAsync($"/foods/{Guid.NewGuid()}", Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AdminPlusNutritionist_OnExerciseWrites_StaysRefused()
    {
        var coach = await TestActors.Professional(factory, UserRole.Admin, UserRole.Nutritionist).CreateAsync(Ct);

        (await coach.Http.PostAsJsonAsync("/exercises", new { Name = "Refused" }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await coach.Http.DeleteAsync($"/exercises/{Guid.NewGuid()}?Version=1", Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
