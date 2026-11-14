using System.Security.Claims;
using System.Text.Json;
using FastEndpoints;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.Recipes.Shared;
using FormUp.Application.Features.Recipes.UpdateRecipe;
using FormUp.Tests.Endpoints;
using MongoDB.Driver;
using NSubstitute;

namespace FormUp.Tests.Endpoints.Recipes;

/// <summary>
/// Tests for <see cref="UpdateRecipeEndpoint"/>.
/// </summary>
public class UpdateRecipeEndpointTests
{
    private readonly Guid _nutritionistId = Guid.NewGuid();

    [Fact]
    public async Task HandleAsync_OwnerFlipsVisibility_Succeeds()
    {
        var recipeId = Guid.NewGuid();
        var foodId = Guid.NewGuid();
        var recipe = RecipeTestHelpers.CreateRecipe(
            externalId: recipeId,
            nutritionistId: _nutritionistId,
            visibility: RecipeVisibility.Public);
        var food = new Food
        {
            ExternalId = foodId,
            Name = "Chicken",
            NutrientValue = new NutrientValue { Kcal = 100, Protein = 20, Carbs = 0, Fat = 2 }
        };
        var mongo = RecipeTestHelpers.CreateMockMongo(recipes: [recipe], foods: [food]);

        var ep = Factory.Create<UpdateRecipeEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_nutritionistId, AppRoles.Nutritionist)))),
            mongo);

        var request = new UpdateRecipeRequest
        {
            RecipeId = recipeId,
            Version = 1,
            Name = "Same Name",
            Visibility = RecipeVisibility.Private,
            Foods = [new RecipeFoodDto { FoodExternalId = foodId, AmountGrams = 100 }]
        };

        await ep.HandleAsync(request, TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(200,
            "an unstubbed ReplaceOneAsync auto-substitutes ModifiedCount = 0, which would " +
            "silently take the 409 branch instead of Send.OkAsync — this assertion is what " +
            "makes that regression visible");
        await mongo.Recipes.Received(1).ReplaceOneAsync(
            Arg.Any<FilterDefinition<Recipe>>(),
            Arg.Is<Recipe>(r => r.Visibility == RecipeVisibility.Private),
            Arg.Any<ReplaceOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_NoVisibilityInRequest_PreservesExisting()
    {
        var recipeId = Guid.NewGuid();
        var foodId = Guid.NewGuid();
        var recipe = RecipeTestHelpers.CreateRecipe(
            externalId: recipeId,
            nutritionistId: _nutritionistId,
            visibility: RecipeVisibility.Private);
        var food = new Food
        {
            ExternalId = foodId,
            Name = "Chicken",
            NutrientValue = new NutrientValue { Kcal = 100, Protein = 20, Carbs = 0, Fat = 2 }
        };
        var mongo = RecipeTestHelpers.CreateMockMongo(recipes: [recipe], foods: [food]);

        var ep = Factory.Create<UpdateRecipeEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_nutritionistId, AppRoles.Nutritionist)))),
            mongo);

        var request = new UpdateRecipeRequest
        {
            RecipeId = recipeId,
            Version = 1,
            Name = "Same Name",
            // Visibility intentionally omitted — should preserve the existing Private value.
            Foods = [new RecipeFoodDto { FoodExternalId = foodId, AmountGrams = 100 }]
        };

        await ep.HandleAsync(request, TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(200,
            "an unstubbed ReplaceOneAsync auto-substitutes ModifiedCount = 0, which would " +
            "silently take the 409 branch instead of Send.OkAsync — this assertion is what " +
            "makes that regression visible");
        await mongo.Recipes.Received(1).ReplaceOneAsync(
            Arg.Any<FilterDefinition<Recipe>>(),
            Arg.Is<Recipe>(r => r.Visibility == RecipeVisibility.Private),
            Arg.Any<ReplaceOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_StaleVersion_Returns409()
    {
        var recipeId = Guid.NewGuid();
        var foodId = Guid.NewGuid();
        // Recipe is currently at version 3; the caller still holds a stale version 1 copy.
        var recipe = RecipeTestHelpers.CreateRecipe(
            externalId: recipeId,
            nutritionistId: _nutritionistId,
            visibility: RecipeVisibility.Public,
            version: 3);
        var food = new Food
        {
            ExternalId = foodId,
            Name = "Chicken",
            NutrientValue = new NutrientValue { Kcal = 100, Protein = 20, Carbs = 0, Fat = 2 }
        };
        var mongo = RecipeTestHelpers.CreateMockMongo(recipes: [recipe], foods: [food]);

        using var responseBody = new MemoryStream();
        var ep = Factory.Create<UpdateRecipeEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx =>
            {
                ctx.Request.HttpContext.User = new ClaimsPrincipal(
                    new ClaimsIdentity(
                        EndpointTestHelpers.FakeUserClaims(_nutritionistId, AppRoles.Nutritionist)));
                ctx.Request.HttpContext.Response.Body = responseBody;
            }),
            mongo);

        var request = new UpdateRecipeRequest
        {
            RecipeId = recipeId,
            Version = 1,
            Name = "Stale Update",
            Foods = [new RecipeFoodDto { FoodExternalId = foodId, AmountGrams = 100 }]
        };

        await ep.HandleAsync(request, TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(409);

        responseBody.Seek(0, SeekOrigin.Begin);
        using var doc = await JsonDocument.ParseAsync(responseBody, cancellationToken: TestContext.Current.CancellationToken);
        doc.RootElement.GetProperty("errorCode").GetString()
            .Should().Be(ErrorCodes.RecipeVersionConflict);

        await mongo.Recipes.DidNotReceive().ReplaceOneAsync(
            Arg.Any<FilterDefinition<Recipe>>(),
            Arg.Any<Recipe>(),
            Arg.Any<ReplaceOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ConcurrentWrite_DoubleGuard_Returns409()
    {
        // Recipe is at version 1, request carries version 1 (matches in memory, so the early
        // pre-check passes and control reaches the write) — but ReplaceOneAsync returns
        // ModifiedCount = 0 (a concurrent write beat us between the fetch and this write).
        var recipeId = Guid.NewGuid();
        var foodId = Guid.NewGuid();
        var recipe = RecipeTestHelpers.CreateRecipe(
            externalId: recipeId,
            nutritionistId: _nutritionistId,
            visibility: RecipeVisibility.Public,
            version: 1);
        var food = new Food
        {
            ExternalId = foodId,
            Name = "Chicken",
            NutrientValue = new NutrientValue { Kcal = 100, Protein = 20, Carbs = 0, Fat = 2 }
        };
        var mongo = RecipeTestHelpers.CreateMockMongo(
            recipes: [recipe], foods: [food], modifiedCount: 0);

        using var responseBody = new MemoryStream();
        var ep = Factory.Create<UpdateRecipeEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx =>
            {
                ctx.Request.HttpContext.User = new ClaimsPrincipal(
                    new ClaimsIdentity(
                        EndpointTestHelpers.FakeUserClaims(_nutritionistId, AppRoles.Nutritionist)));
                ctx.Request.HttpContext.Response.Body = responseBody;
            }),
            mongo);

        var request = new UpdateRecipeRequest
        {
            RecipeId = recipeId,
            Version = 1,
            Name = "Concurrent Update",
            Foods = [new RecipeFoodDto { FoodExternalId = foodId, AmountGrams = 100 }]
        };

        await ep.HandleAsync(request, TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(409);

        responseBody.Seek(0, SeekOrigin.Begin);
        using var doc = await JsonDocument.ParseAsync(responseBody, cancellationToken: TestContext.Current.CancellationToken);
        doc.RootElement.GetProperty("errorCode").GetString()
            .Should().Be(ErrorCodes.RecipeVersionConflict);
    }

    [Fact]
    public async Task HandleAsync_NotOwner_Returns404()
    {
        var recipeId = Guid.NewGuid();
        // Recipe belongs to a different nutritionist — owner filter returns nothing.
        var mongo = RecipeTestHelpers.CreateMockMongo();

        var ep = Factory.Create<UpdateRecipeEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_nutritionistId, AppRoles.Nutritionist)))),
            mongo);

        var request = new UpdateRecipeRequest
        {
            RecipeId = recipeId,
            Name = "Hacked",
            Foods = []
        };

        await ep.HandleAsync(request, TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(404);
    }
}
