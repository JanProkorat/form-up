using System.Security.Claims;
using FastEndpoints;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.Foods.CreateFood;
using FormUp.Application.Features.Foods.Shared;
using FormUp.Tests.Endpoints;
using MongoDB.Driver;
using NSubstitute;

namespace FormUp.Tests.Endpoints.Foods;

/// <summary>
/// Tests for <see cref="CreateFoodEndpoint"/>.
/// </summary>
public class CreateFoodEndpointTests
{
    private readonly Guid _nutritionistId = Guid.NewGuid();

    [Fact]
    public async Task HandleAsync_ValidRequest_CreatesFood()
    {
        var mongo = FoodTestHelpers.CreateMockMongo();

        var ep = Factory.Create<CreateFoodEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_nutritionistId, AppRoles.Nutritionist)))),
            mongo);

        var request = new CreateFoodRequest
        {
            Name = "Custom Protein Bar",
            NutrientValue = new NutrientValueDto
            {
                Kcal = 200,
                Protein = 20,
                Carbs = 20,
                Fat = 5
            },
            Allergens = [Allergen.Milk, Allergen.Soy],
            CommonServings = [new ServingSizeDto { Label = "1 bar", WeightGrams = 60 }]
        };

        await ep.HandleAsync(request, TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(201);

        await mongo.Foods.Received(1).InsertOneAsync(
            Arg.Is<Food>(f =>
                f.Name == "Custom Protein Bar" &&
                f.NutritionistId == _nutritionistId),
            Arg.Any<InsertOneOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_VisibilityOmitted_DefaultsToPrivate()
    {
        var mongo = FoodTestHelpers.CreateMockMongo();

        var ep = Factory.Create<CreateFoodEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_nutritionistId, AppRoles.Nutritionist)))),
            mongo);

        var request = new CreateFoodRequest
        {
            Name = "Private By Default",
            NutrientValue = new NutrientValueDto { Kcal = 100, Protein = 10, Carbs = 10, Fat = 5 }
        };

        await ep.HandleAsync(request, TestContext.Current.CancellationToken);

        await mongo.Foods.Received(1).InsertOneAsync(
            Arg.Is<Food>(f => f.Visibility == FoodVisibility.Private),
            Arg.Any<InsertOneOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_VisibilityPrivate_IsPersisted()
    {
        var mongo = FoodTestHelpers.CreateMockMongo();

        var ep = Factory.Create<CreateFoodEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_nutritionistId, AppRoles.Nutritionist)))),
            mongo);

        var request = new CreateFoodRequest
        {
            Name = "Secret Recipe Filler",
            Visibility = FoodVisibility.Private,
            NutrientValue = new NutrientValueDto { Kcal = 100, Protein = 10, Carbs = 10, Fat = 5 }
        };

        await ep.HandleAsync(request, TestContext.Current.CancellationToken);

        await mongo.Foods.Received(1).InsertOneAsync(
            Arg.Is<Food>(f =>
                f.Visibility == FoodVisibility.Private &&
                f.NutritionistId == _nutritionistId),
            Arg.Any<InsertOneOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_AllergensAndDietaryPreferences_StoredAsTrimmedStrings()
    {
        var mongo = FoodTestHelpers.CreateMockMongo();

        var ep = Factory.Create<CreateFoodEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_nutritionistId, AppRoles.Nutritionist)))),
            mongo);

        var request = new CreateFoodRequest
        {
            Name = "Tagged Food",
            NutrientValue = new NutrientValueDto { Kcal = 100, Protein = 10, Carbs = 10, Fat = 5 },
            Allergens = [Allergen.Milk, Allergen.TreeNuts],
            DietaryPreferences = [DietaryPreference.Vegan],
            CommonServings = [new ServingSizeDto { Label = "1 bar", WeightGrams = 60 }]
        };

        await ep.HandleAsync(request, TestContext.Current.CancellationToken);

        await mongo.Foods.Received(1).InsertOneAsync(
            Arg.Is<Food>(f =>
                f.Allergens.SequenceEqual(new[] { "Milk", "TreeNuts" }) &&
                f.DietaryPreferences.SequenceEqual(new[] { "Vegan" })),
            Arg.Any<InsertOneOptions>(),
            Arg.Any<CancellationToken>());
    }
}
