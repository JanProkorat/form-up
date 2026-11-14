using System.Security.Claims;
using FastEndpoints;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.Foods.Shared;
using FormUp.Application.Features.Foods.UpdateFood;
using FormUp.Tests.Endpoints;
using MongoDB.Driver;
using NSubstitute;

namespace FormUp.Tests.Endpoints.Foods;

/// <summary>
/// Tests for <see cref="UpdateFoodEndpoint"/>.
/// </summary>
public class UpdateFoodEndpointTests
{
    private readonly Guid _nutritionistId = Guid.NewGuid();

    [Fact]
    public async Task HandleAsync_OwnerUpdates_Succeeds()
    {
        var foodId = Guid.NewGuid();
        var food = FoodTestHelpers.CreateFood(
            externalId: foodId,
            name: "Old Name",
            nutritionistId: _nutritionistId);
        var mongo = FoodTestHelpers.CreateMockMongo(food);

        var ep = Factory.Create<UpdateFoodEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_nutritionistId, AppRoles.Nutritionist)))),
            mongo);

        var request = new UpdateFoodRequest
        {
            FoodId = foodId,
            Name = "New Name",
            NutrientValue = new NutrientValueDto { Kcal = 150, Protein = 15, Carbs = 15, Fat = 3 }
        };

        await ep.HandleAsync(request, TestContext.Current.CancellationToken);

        await mongo.Foods.Received(1).UpdateOneAsync(
            Arg.Any<FilterDefinition<Food>>(),
            Arg.Any<UpdateDefinition<Food>>(),
            Arg.Any<UpdateOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_NotOwner_ThrowsError()
    {
        var foodId = Guid.NewGuid();
        var food = FoodTestHelpers.CreateFood(
            externalId: foodId,
            nutritionistId: Guid.NewGuid()); // different owner
        var mongo = FoodTestHelpers.CreateMockMongo(food);

        var ep = Factory.Create<UpdateFoodEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_nutritionistId, AppRoles.Nutritionist)))),
            mongo);

        var request = new UpdateFoodRequest
        {
            FoodId = foodId,
            Name = "Hacked",
            NutrientValue = new NutrientValueDto { Kcal = 100, Protein = 10, Carbs = 10, Fat = 5 }
        };

        var act = () => ep.HandleAsync(request, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ValidationFailureException>();
    }

    [Fact]
    public async Task HandleAsync_FoodNotFound_Returns404()
    {
        var mongo = FoodTestHelpers.CreateMockMongo();

        var ep = Factory.Create<UpdateFoodEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_nutritionistId, AppRoles.Nutritionist)))),
            mongo);

        var request = new UpdateFoodRequest
        {
            FoodId = Guid.NewGuid(),
            Name = "Nope",
            NutrientValue = new NutrientValueDto { Kcal = 100, Protein = 10, Carbs = 10, Fat = 5 }
        };

        await ep.HandleAsync(request, TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task HandleAsync_NoVisibilityInRequest_UpdateStillSucceeds()
    {
        var foodId = Guid.NewGuid();
        var food = FoodTestHelpers.CreateFood(
            externalId: foodId,
            nutritionistId: _nutritionistId,
            visibility: FoodVisibility.Private);
        var mongo = FoodTestHelpers.CreateMockMongo(food);

        var ep = Factory.Create<UpdateFoodEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_nutritionistId, AppRoles.Nutritionist)))),
            mongo);

        var request = new UpdateFoodRequest
        {
            FoodId = foodId,
            Name = "Renamed",
            // Visibility omitted on purpose — handler must skip the Set(f => f.Visibility, ...)
            NutrientValue = new NutrientValueDto { Kcal = 100, Protein = 10, Carbs = 10, Fat = 5 }
        };

        await ep.HandleAsync(request, TestContext.Current.CancellationToken);

        await mongo.Foods.Received(1).UpdateOneAsync(
            Arg.Any<FilterDefinition<Food>>(),
            Arg.Any<UpdateDefinition<Food>>(),
            Arg.Any<UpdateOptions>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_OwnerFlipsVisibility_Succeeds()
    {
        var foodId = Guid.NewGuid();
        var food = FoodTestHelpers.CreateFood(
            externalId: foodId,
            nutritionistId: _nutritionistId,
            visibility: FoodVisibility.Public);
        var mongo = FoodTestHelpers.CreateMockMongo(food);

        var ep = Factory.Create<UpdateFoodEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_nutritionistId, AppRoles.Nutritionist)))),
            mongo);

        var request = new UpdateFoodRequest
        {
            FoodId = foodId,
            Name = "Still Same Name",
            Visibility = FoodVisibility.Private,
            NutrientValue = new NutrientValueDto { Kcal = 100, Protein = 10, Carbs = 10, Fat = 5 }
        };

        await ep.HandleAsync(request, TestContext.Current.CancellationToken);

        await mongo.Foods.Received(1).UpdateOneAsync(
            Arg.Any<FilterDefinition<Food>>(),
            Arg.Any<UpdateDefinition<Food>>(),
            Arg.Any<UpdateOptions>(),
            Arg.Any<CancellationToken>());
    }
}
