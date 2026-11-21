using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using FastEndpoints;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.Foods.SearchFoods;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Builders;
using FormUp.Tests.Endpoints;
using FormUp.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using NSubstitute;

namespace FormUp.Tests.Endpoints.Foods;

/// <summary>
/// Thin per-collection handle onto the shared Mongo container (#1104 Phase B — see
/// <see cref="SharedTestContainers"/>). Each collection using this fixture gets its own
/// database name inside that one shared server instead of its own container.
/// </summary>
public class SearchFoodsMongoContainerFixture(SharedTestContainers sharedContainers)
{
    /// <summary>The shared Mongo container's connection string.</summary>
    public string ConnectionString => sharedContainers.MongoConnectionString;

    /// <summary>A database name unique to this fixture, inside the shared container.</summary>
    public string DatabaseName { get; } = SharedTestContainers.CreateMongoDatabaseName("searchfoodsendpoint");
}

[CollectionDefinition("SearchFoodsEndpoint")]
public class SearchFoodsEndpointCollection : ICollectionFixture<SearchFoodsMongoContainerFixture>;

/// <summary>
/// Tests for <see cref="SearchFoodsEndpoint"/>. Backed by a real Mongo container, not
/// <see cref="FoodTestHelpers.CreateMockMongo(Food[])"/> — #1139 moved the endpoint from
/// <c>FindAsync</c> to an aggregation pipeline (computed sort keys, optional collation), and that
/// mock only stubs <c>FindAsync</c>/<c>CountDocumentsAsync</c>. Stubbing a full aggregation
/// pipeline with NSubstitute is infeasible, so these tests mirror
/// <see cref="FormUp.Tests.Endpoints.Recipes.OwnerScopedVisibilityFilterTests"/>'s
/// real-Mongo approach instead. Owner-filter and sort-specific coverage lives in
/// <see cref="SearchFoodsOwnerAndSortTests"/>.
/// </summary>
[Collection("SearchFoodsEndpoint")]
public class SearchFoodsEndpointTests : IAsyncLifetime
{
    private readonly IMongoCollection<Food> _foods;
    private readonly IMongoCollection<FoodTag> _foodTags;
    private readonly IMongoCollection<FoodTagAssignment> _foodTagAssignments;
    private readonly IMongoContext _mongoContext;

    public SearchFoodsEndpointTests(SearchFoodsMongoContainerFixture containerFixture)
    {
        var mongoClient = new MongoClient(containerFixture.ConnectionString);
        var mongoDb = mongoClient.GetDatabase(containerFixture.DatabaseName);
        _foods = mongoDb.GetCollection<Food>("foods");
        _foodTags = mongoDb.GetCollection<FoodTag>("foodTags");
        _foodTagAssignments = mongoDb.GetCollection<FoodTagAssignment>("foodTagAssignments");

        var mongoContext = Substitute.For<IMongoContext>();
        mongoContext.Foods.Returns(_foods);
        mongoContext.FoodTags.Returns(_foodTags);
        mongoContext.FoodTagAssignments.Returns(_foodTagAssignments);
        _mongoContext = mongoContext;
    }

    public async ValueTask InitializeAsync()
    {
        await _foods.DeleteManyAsync(FilterDefinition<Food>.Empty);
        await _foodTags.DeleteManyAsync(FilterDefinition<FoodTag>.Empty);
        await _foodTagAssignments.DeleteManyAsync(FilterDefinition<FoodTagAssignment>.Empty);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private SearchFoodsEndpoint CreateEndpoint(Guid callerId)
        => Factory.Create<SearchFoodsEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    EndpointTestHelpers.FakeUserClaims(callerId, AppRoles.Nutritionist))),
            _mongoContext);

    [Fact]
    public async Task HandleAsync_LocalResults_ReturnsFoods()
    {
        var ct = TestContext.Current.CancellationToken;
        var callerId = Guid.NewGuid();
        var food = FoodTestHelpers.CreateFood(name: "Chicken Breast", nutritionistId: callerId);
        await _foods.InsertOneAsync(food, cancellationToken: ct);

        var ep = CreateEndpoint(callerId);

        await ep.HandleAsync(new SearchFoodsRequest { Query = "chicken" }, ct);

        ep.Response.Foods.Should().HaveCount(1);
        ep.Response.Foods[0].Name.Should().Be("Chicken Breast");
    }

    [Fact]
    public async Task HandleAsync_NoLocalResults_ReturnsEmpty()
    {
        var ct = TestContext.Current.CancellationToken;

        var ep = CreateEndpoint(Guid.NewGuid());

        await ep.HandleAsync(new SearchFoodsRequest { Query = "quinoa", PageSize = 20 }, ct);

        ep.Response.Foods.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAsync_NoQuery_ReturnsAll()
    {
        var ct = TestContext.Current.CancellationToken;
        var callerId = Guid.NewGuid();
        var food1 = FoodTestHelpers.CreateFood(name: "Apple", nutritionistId: callerId);
        var food2 = FoodTestHelpers.CreateFood(name: "Banana", nutritionistId: callerId);
        await _foods.InsertManyAsync([food1, food2], cancellationToken: ct);

        var ep = CreateEndpoint(callerId);

        await ep.HandleAsync(new SearchFoodsRequest(), ct);

        ep.Response.Foods.Should().HaveCount(2);
    }

    [Fact]
    public async Task HandleAsync_WithAcceptLanguageCzech_ReturnsCzechName()
    {
        var ct = TestContext.Current.CancellationToken;
        var callerId = Guid.NewGuid();
        var food = FoodTestHelpers.CreateFood(name: "Chicken Breast", nutritionistId: callerId);
        food.LocalizedNames = new LocalizedNames
        {
            En = "Chicken Breast",
            Cs = "Kuřecí prsa",
        };
        await _foods.InsertOneAsync(food, cancellationToken: ct);

        var ep = CreateEndpoint(callerId);
        ep.HttpContext.Request.Headers.AcceptLanguage = "cs";

        await ep.HandleAsync(new SearchFoodsRequest { Query = "chicken" }, ct);

        ep.Response.Foods.Should().HaveCount(1);
        ep.Response.Foods[0].Name.Should().Be("Kuřecí prsa");
    }

    [Fact]
    public async Task HandleAsync_MissingUserIdClaim_Returns401()
    {
        var ct = TestContext.Current.CancellationToken;
        var food = FoodTestHelpers.CreateFood(name: "Anything");
        await _foods.InsertOneAsync(food, cancellationToken: ct);

        var ep = Factory.Create<SearchFoodsEndpoint>(_mongoContext);

        await ep.HandleAsync(new SearchFoodsRequest(), ct);

        ep.HttpContext.Response.StatusCode.Should().Be(401);
    }

    [Fact]
    public async Task HandleAsync_AuthenticatedOwner_IsOwnedFlagIsTrue()
    {
        var ct = TestContext.Current.CancellationToken;
        var ownerId = Guid.NewGuid();
        var food = FoodTestHelpers.CreateFood(
            name: "My Private Food",
            nutritionistId: ownerId,
            visibility: FoodVisibility.Private);
        await _foods.InsertOneAsync(food, cancellationToken: ct);

        var ep = CreateEndpoint(ownerId);

        await ep.HandleAsync(new SearchFoodsRequest(), ct);

        ep.Response.Foods.Should().HaveCount(1);
        ep.Response.Foods[0].IsOwnedByCurrentUser.Should().BeTrue();
        ep.Response.Foods[0].Visibility.Should().Be(FoodVisibility.Private);
    }

    [Fact]
    public async Task HandleAsync_AuthenticatedNonOwner_IsOwnedFlagIsFalse()
    {
        var ct = TestContext.Current.CancellationToken;
        var ownerId = Guid.NewGuid();
        var otherNutritionistId = Guid.NewGuid();
        var food = FoodTestHelpers.CreateFood(
            name: "Public Food",
            nutritionistId: ownerId,
            visibility: FoodVisibility.Public);
        await _foods.InsertOneAsync(food, cancellationToken: ct);

        var ep = CreateEndpoint(otherNutritionistId);

        await ep.HandleAsync(new SearchFoodsRequest(), ct);

        ep.Response.Foods.Should().HaveCount(1);
        ep.Response.Foods[0].IsOwnedByCurrentUser.Should().BeFalse();
    }
}

/// <summary>
/// HTTP-level test for the <c>category</c> query param binding on <c>GET /foods/search</c>. A
/// direct <see cref="SearchFoodsEndpoint.HandleAsync"/> unit call skips FastEndpoints' own query
/// binding, so it cannot prove that a single <c>?category=Dairy</c> (the old link shape) still
/// binds into <see cref="SearchFoodsRequest.Categories"/> as a one-item list. Real HTTP through
/// <see cref="FitnessApiFactory"/> is required.
/// </summary>
[Collection(TestCollection.Shard4)]
public class SearchFoodsCategoryQueryBindingTests(FitnessApiFactory factory)
{
    [Fact]
    public async Task SearchFoods_SingleCategoryQueryParam_BindsAsOneItemList_AndFiltersResults()
    {
        var ct = TestContext.Current.CancellationToken;
        var nutritionist = await TestActors.Nutritionist(factory).CreateAsync(ct);

        var matchingFoodId = Guid.NewGuid();
        var otherFoodId = Guid.NewGuid();

        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

            await mongo.Foods.InsertManyAsync(
            [
                new Food
                {
                    ExternalId = matchingFoodId,
                    Name = "Single Category Binding Dairy",
                    Category = FoodCategory.Dairy,
                    NutritionistId = nutritionist.UserId,
                    Visibility = FoodVisibility.Public,
                    IsDeleted = false,
                    DateCreated = DateTime.UtcNow,
                },
                new Food
                {
                    ExternalId = otherFoodId,
                    Name = "Single Category Binding Meat",
                    Category = FoodCategory.Meat,
                    NutritionistId = nutritionist.UserId,
                    Visibility = FoodVisibility.Public,
                    IsDeleted = false,
                    DateCreated = DateTime.UtcNow,
                },
            ], cancellationToken: ct);
        }

        var response = await nutritionist.Http.GetAsync("/foods/search?category=Dairy", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // A local minimal shape, not SearchFoodsResponse — FoodSummary.Category/Visibility
        // serialize as strings via the app's globally-configured JsonStringEnumConverter
        // (rules/api-design.md#json-serialization), but HttpContent.ReadFromJsonAsync's default
        // JsonSerializerOptions has no such converter, so deserializing the real response type
        // throws. Only FoodId is needed for this assertion anyway.
        var body = await response.Content.ReadFromJsonAsync<SearchResultShim>(cancellationToken: ct);

        body.Should().NotBeNull();
        body!.Foods.Should().ContainSingle(f => f.FoodId == matchingFoodId);
        body.Foods.Should().NotContain(f => f.FoodId == otherFoodId);
    }

    private record SearchResultShim(List<FoodIdShim> Foods);

    private record FoodIdShim(Guid FoodId);
}

/// <summary>
/// HTTP-level tests for the <c>owner</c>/<c>sortBy</c>/<c>sortDir</c> query param binding on
/// <c>GET /foods/search</c> (#1139). A direct <see cref="SearchFoodsEndpoint.HandleAsync"/> unit
/// call skips FastEndpoints' own query binding, so it cannot prove that repeated
/// <c>?owner=Mine&amp;owner=System</c> binds into <see cref="SearchFoodsRequest.Owners"/> as a
/// two-item list, that <c>sortBy</c>/<c>sortDir</c> bind by enum member name, or that an
/// unparsable enum value 400s before the handler runs. Real HTTP through
/// <see cref="FitnessApiFactory"/> is required — mirrors
/// <see cref="SearchFoodsCategoryQueryBindingTests"/>.
/// </summary>
[Collection(TestCollection.Shard4)]
public class SearchFoodsOwnerAndSortQueryBindingTests(FitnessApiFactory factory)
{
    [Fact]
    public async Task SearchFoods_RepeatedOwnerQueryParam_BindsAsTwoItemList_AndSortParamsBindByName()
    {
        var ct = TestContext.Current.CancellationToken;
        var nutritionist = await TestActors.Nutritionist(factory).CreateAsync(ct);

        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

            await mongo.Foods.InsertOneAsync(new Food
            {
                ExternalId = Guid.NewGuid(),
                Name = "Owner Sort Binding Food",
                Category = FoodCategory.Other,
                NutritionistId = nutritionist.UserId,
                Visibility = FoodVisibility.Public,
                IsDeleted = false,
                DateCreated = DateTime.UtcNow,
            }, cancellationToken: ct);
        }

        var response = await nutritionist.Http.GetAsync(
            "/foods/search?owner=Mine&owner=System&sortBy=Name&sortDir=Descending", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("owner=NotAMember")]
    [InlineData("sortBy=NotAField")]
    [InlineData("sortDir=NotADirection")]
    public async Task SearchFoods_InvalidEnumQueryValue_Returns400(string invalidQuery)
    {
        var ct = TestContext.Current.CancellationToken;
        var nutritionist = await TestActors.Nutritionist(factory).CreateAsync(ct);

        var response = await nutritionist.Http.GetAsync($"/foods/search?{invalidQuery}", ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
