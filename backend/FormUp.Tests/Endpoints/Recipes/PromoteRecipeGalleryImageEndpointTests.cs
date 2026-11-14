using System.Security.Claims;
using FastEndpoints;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Features.Recipes.PromoteRecipeGalleryImage;
using MongoDB.Driver;
using NSubstitute;

namespace FormUp.Tests.Endpoints.Recipes;

/// <summary>
/// Unit tests for <see cref="PromoteRecipeGalleryImageEndpoint"/> branches the integration host cannot reach.
/// </summary>
public class PromoteRecipeGalleryImageEndpointTests
{
    private readonly Guid _nutritionistId = Guid.NewGuid();

    [Fact]
    public async Task HandleAsync_CompareAndSwapMissesOnConcurrentChange_Returns409()
    {
        var recipeId = Guid.NewGuid();
        var galleryUrl = $"recipes/{recipeId}/gallery-{Guid.NewGuid():N}.jpg";
        var recipe = RecipeTestHelpers.CreateRecipe(externalId: recipeId, nutritionistId: _nutritionistId);
        recipe.GalleryImageUrls.Add(galleryUrl);
        var mongo = RecipeTestHelpers.CreateMockMongo(recipes: [recipe], modifiedCount: 0);

        var ep = Factory.Create<PromoteRecipeGalleryImageEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(_nutritionistId, AppRoles.Nutritionist)))),
            mongo);

        await ep.HandleAsync(new PromoteRecipeGalleryImageRequest { RecipeId = recipeId, ImageUrl = galleryUrl },
            CancellationToken.None);

        ep.HttpContext.Response.StatusCode.Should().Be(409);
        await mongo.Recipes.Received(1).UpdateOneAsync(
            Arg.Any<FilterDefinition<Application.Domain.Documents.Recipe>>(),
            Arg.Any<UpdateDefinition<Application.Domain.Documents.Recipe>>(),
            Arg.Any<UpdateOptions>(),
            Arg.Any<CancellationToken>());
    }
}
