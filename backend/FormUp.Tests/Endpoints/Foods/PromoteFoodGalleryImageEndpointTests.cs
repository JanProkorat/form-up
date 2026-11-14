using System.Security.Claims;
using FastEndpoints;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Features.Foods.PromoteFoodGalleryImage;
using MongoDB.Driver;
using NSubstitute;

namespace FormUp.Tests.Endpoints.Foods;

/// <summary>
/// Unit tests for <see cref="PromoteFoodGalleryImageEndpoint"/> branches the integration host cannot reach.
/// </summary>
public class PromoteFoodGalleryImageEndpointTests
{
    private readonly Guid _nutritionistId = Guid.NewGuid();

    [Fact]
    public async Task HandleAsync_CompareAndSwapMissesOnConcurrentChange_Returns409()
    {
        var foodId = Guid.NewGuid();
        var galleryUrl = $"foods/{foodId}/gallery-{Guid.NewGuid():N}.jpg";
        var food = FoodTestHelpers.CreateFood(externalId: foodId, nutritionistId: _nutritionistId);
        food.GalleryImageUrls.Add(galleryUrl);
        var mongo = FoodTestHelpers.CreateMockMongo(food);

        var missResult = Substitute.For<UpdateResult>();
        missResult.ModifiedCount.Returns(0L);
        mongo.Foods.UpdateOneAsync(
                Arg.Any<FilterDefinition<Food>>(),
                Arg.Any<UpdateDefinition<Food>>(),
                Arg.Any<UpdateOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(missResult);

        var ep = Factory.Create<PromoteFoodGalleryImageEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(_nutritionistId, AppRoles.Nutritionist)))),
            mongo);

        await ep.HandleAsync(new PromoteFoodGalleryImageRequest { FoodId = foodId, ImageUrl = galleryUrl },
            CancellationToken.None);

        ep.HttpContext.Response.StatusCode.Should().Be(409);
        await mongo.Foods.Received(1).UpdateOneAsync(
            Arg.Any<FilterDefinition<Food>>(),
            Arg.Any<UpdateDefinition<Food>>(),
            Arg.Any<UpdateOptions>(),
            Arg.Any<CancellationToken>());
    }
}
