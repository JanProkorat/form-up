using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using FormUp.Tests.Builders;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace FormUp.Tests.Endpoints.Recipes;

/// <summary>
/// Drives <see cref="FormUp.Application.Features.Recipes.UpdateRecipe.UpdateRecipeEndpoint"/>
/// itself (via the real HTTP + Testcontainers stack) against a raw, field-absent legacy recipe
/// document — unlike <see cref="RecipeVersionLegacyIntegrationTests"/>, which only proves the
/// standalone filter shape behaves correctly and never instantiates the endpoint. That gap means
/// the four tests in that file cannot detect the <c>Not(Exists(Version))</c> clause being removed
/// from the endpoint itself; this test closes it.
///
/// <c>UpdateRecipe</c> returns 200/409 (never 201), so it does not need the <c>LinkGenerator</c>
/// that the lightweight <c>Factory.Create&lt;TEndpoint&gt;()</c> host lacks — <c>FitnessApiFactory</c>
/// is used anyway here specifically to exercise the real Mongo driver's CAS filter behavior.
/// </summary>
[Collection(TestCollection.Name)]
public class UpdateRecipeEndpointIntegrationTests(FitnessApiFactory factory)
{
    /// <summary>
    /// Seeds a raw legacy recipe (no <c>version</c> BSON element — the state of every recipe
    /// stored before #1032) directly into the collection the running app reads from, then drives
    /// <c>PUT /recipes/{id}</c> with <c>Version = 1</c> through real HTTP. RED before the endpoint
    /// fix (a bare <c>Eq(version, 1)</c> filter matches zero documents), GREEN after.
    /// </summary>
    [Fact]
    public async Task HandleAsync_LegacyRecipeMissingVersionField_Version1_Succeeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = factory.CreateClient();

        var email = $"{Guid.NewGuid():N}@recipe-legacy-endpoint.test";
        await TestHelpers.RegisterAsync(client, email, "TestPass1!", "Test", "Nutritionist", "Nutritionist");
        var (accessToken, _) = await TestHelpers.LoginAsync(client, email, "TestPass1!");

        Guid nutritionistId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await db.Users.FirstAsync(u => u.Email == email, ct);
            nutritionistId = user.Id;
        }

        var recipeId = Guid.NewGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
            var rawRecipes = mongo.Recipes.Database.GetCollection<BsonDocument>(
                mongo.Recipes.CollectionNamespace.CollectionName);

            await rawRecipes.InsertOneAsync(new BsonDocument
            {
                { "externalId", new BsonBinaryData(recipeId, GuidRepresentation.Standard) },
                { "nutritionistId", new BsonBinaryData(nutritionistId, GuidRepresentation.Standard) },
                { "name", "Legacy Recipe" },
                { "visibility", RecipeVisibility.Public.ToString() },
                { "dateCreated", DateTime.UtcNow }
                // NOTE: NO version element — simulates every recipe stored before #1032.
            }, cancellationToken: ct);
        }

        var foodId = await TestHelpers.CreateFoodAsync(client, accessToken, ct, kcal: 100m, protein: 20m, carbs: 0m, fat: 2m);

        var response = await client.PutAsJsonAsync(
            $"/recipes/{recipeId}",
            new
            {
                Version = 1,
                Name = "Updated Legacy Recipe",
                MealTypes = new[] { "Lunch" },
                Foods = new[]
                {
                    new { FoodExternalId = foodId, AmountGrams = 100m }
                }
            },
            ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "a legacy recipe with no stored version element must be updatable on its first " +
            "write when the client echoes back Version = 1 — the Not(Exists(Version)) clause " +
            "in UpdateRecipeEndpoint's CAS filter is what makes this succeed");

        var body = await response.Content.ReadFromJsonAsync<RecipeVersionRef>(cancellationToken: ct);
        body!.Version.Should().Be(2,
            "the version field is stored for the first time on this write and bumped to 2");
    }

    [Fact]
    public async Task HandleAsync_StepsWithMinutes_RoundTripsAndOutOfRangeMinutesReturns400()
    {
        var ct = TestContext.Current.CancellationToken;
        var coach = await TestActors.Nutritionist(factory).CreateAsync(ct);
        var foodId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
            await mongo.Foods.InsertOneAsync(new Food
            {
                ExternalId = foodId,
                Name = $"Steps Test Food {foodId:N}",
                NutritionistId = coach.UserId,
                Visibility = FoodVisibility.Private,
                NutrientValue = new NutrientValue { Kcal = 100, Protein = 20, Carbs = 0, Fat = 2 },
                DateCreated = DateTime.UtcNow
            }, cancellationToken: ct);
        }

        var created = await coach.Http.PostAsJsonAsync("/recipes", new
        {
            Name = "Steps Update Recipe",
            MealTypes = new[] { "Lunch" },
            Foods = new[] { new { FoodExternalId = foodId, AmountGrams = 100m } }
        }, ct);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var createdBody = await created.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        var recipeId = createdBody.GetProperty("recipeId").GetGuid();
        var version = createdBody.GetProperty("version").GetInt32();

        var updated = await coach.Http.PutAsJsonAsync($"/recipes/{recipeId}", new
        {
            Version = version,
            Name = "Steps Update Recipe",
            MealTypes = new[] { "Lunch" },
            Steps = new[]
            {
                new { Title = (string?)" Boil ", Description = " Boil the pasta ", Minutes = (int?)12 },
                new { Title = (string?)null, Description = "Serve", Minutes = (int?)null }
            },
            Foods = new[] { new { FoodExternalId = foodId, AmountGrams = 100m } }
        }, ct);

        updated.StatusCode.Should().Be(HttpStatusCode.OK);
        var steps = (await updated.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct))
            .GetProperty("steps").EnumerateArray().ToList();
        steps.Should().HaveCount(2);
        steps[0].GetProperty("title").GetString().Should().Be("Boil");
        steps[0].GetProperty("description").GetString().Should().Be("Boil the pasta");
        steps[0].GetProperty("minutes").GetInt32().Should().Be(12);
        steps[1].GetProperty("description").GetString().Should().Be("Serve");

        var invalid = await coach.Http.PutAsJsonAsync($"/recipes/{recipeId}", new
        {
            Version = version + 1,
            Name = "Steps Update Recipe",
            MealTypes = new[] { "Lunch" },
            Steps = new[] { new { Description = "Boil", Minutes = (int?)181 } },
            Foods = new[] { new { FoodExternalId = foodId, AmountGrams = 100m } }
        }, ct);

        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private record RecipeVersionRef(int Version);
}
