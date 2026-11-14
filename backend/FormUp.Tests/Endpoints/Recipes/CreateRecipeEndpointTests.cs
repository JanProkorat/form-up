using System.Security.Claims;
using FastEndpoints;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.Recipes.CreateRecipe;
using FormUp.Application.Features.Recipes.Shared;
using FormUp.Tests.Endpoints;
using MongoDB.Driver;
using NSubstitute;

namespace FormUp.Tests.Endpoints.Recipes;

/// <summary>
/// Tests for <see cref="CreateRecipeEndpoint"/> focused on the visibility default/override behaviour.
/// </summary>
public class CreateRecipeEndpointTests
{
    private readonly Guid _nutritionistId = Guid.NewGuid();

    [Fact]
    public async Task HandleAsync_VisibilityOmitted_DefaultsToPrivate()
    {
        var foodId = Guid.NewGuid();
        var food = new Food
        {
            ExternalId = foodId,
            Name = "Chicken",
            NutrientValue = new NutrientValue { Kcal = 100, Protein = 20, Carbs = 0, Fat = 2 }
        };
        var mongo = RecipeTestHelpers.CreateMockMongo(foods: [food]);

        var ep = Factory.Create<CreateRecipeEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_nutritionistId, AppRoles.Nutritionist)))),
            mongo);

        var request = new CreateRecipeRequest
        {
            Name = "Default Private Recipe",
            Foods = [new RecipeFoodDto { FoodExternalId = foodId, AmountGrams = 150 }]
        };

        await ep.HandleAsync(request, TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(201);

        await mongo.Recipes.Received(1).InsertOneAsync(
            Arg.Is<Recipe>(r => r.Visibility == RecipeVisibility.Private && r.NutritionistId == _nutritionistId),
            Arg.Any<InsertOneOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_VisibilityPublic_IsPersisted()
    {
        var foodId = Guid.NewGuid();
        var food = new Food
        {
            ExternalId = foodId,
            Name = "Chicken",
            NutrientValue = new NutrientValue { Kcal = 100, Protein = 20, Carbs = 0, Fat = 2 }
        };
        var mongo = RecipeTestHelpers.CreateMockMongo(foods: [food]);

        var ep = Factory.Create<CreateRecipeEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_nutritionistId, AppRoles.Nutritionist)))),
            mongo);

        var request = new CreateRecipeRequest
        {
            Name = "Shared Recipe",
            Visibility = RecipeVisibility.Public,
            Foods = [new RecipeFoodDto { FoodExternalId = foodId, AmountGrams = 150 }]
        };

        await ep.HandleAsync(request, TestContext.Current.CancellationToken);

        await mongo.Recipes.Received(1).InsertOneAsync(
            Arg.Is<Recipe>(r => r.Visibility == RecipeVisibility.Public),
            Arg.Any<InsertOneOptions>(),
            Arg.Any<CancellationToken>());
    }

}
