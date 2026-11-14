using System.Security.Claims;
using FastEndpoints;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Features.Foods.DeleteFood;
using FormUp.Tests.Endpoints;
using MongoDB.Driver;
using NSubstitute;

namespace FormUp.Tests.Endpoints.Foods;

/// <summary>
/// Tests for <see cref="DeleteFoodEndpoint"/>.
/// </summary>
public class DeleteFoodEndpointTests
{
    private readonly Guid _nutritionistId = Guid.NewGuid();

    [Fact]
    public async Task HandleAsync_OwnerDeletes_SoftDeletes()
    {
        var foodId = Guid.NewGuid();
        var food = FoodTestHelpers.CreateFood(
            externalId: foodId,
            nutritionistId: _nutritionistId);
        var mongo = FoodTestHelpers.CreateMockMongo(food);

        var ep = Factory.Create<DeleteFoodEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_nutritionistId, AppRoles.Nutritionist)))),
            mongo);

        await ep.HandleAsync(new DeleteFoodRequest { FoodId = foodId }, TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(204);

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

        var ep = Factory.Create<DeleteFoodEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_nutritionistId, AppRoles.Nutritionist)))),
            mongo);

        var act = () => ep.HandleAsync(new DeleteFoodRequest { FoodId = foodId }, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ValidationFailureException>();
    }

    [Fact]
    public async Task HandleAsync_FoodNotFound_Returns404()
    {
        var mongo = FoodTestHelpers.CreateMockMongo();

        var ep = Factory.Create<DeleteFoodEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(_nutritionistId, AppRoles.Nutritionist)))),
            mongo);

        await ep.HandleAsync(new DeleteFoodRequest { FoodId = Guid.NewGuid() }, TestContext.Current.CancellationToken);

        ep.HttpContext.Response.StatusCode.Should().Be(404);
    }

}
