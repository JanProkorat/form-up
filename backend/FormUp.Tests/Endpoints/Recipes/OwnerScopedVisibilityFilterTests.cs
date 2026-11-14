using System.Security.Claims;
using FastEndpoints;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.Foods.GetFood;
using FormUp.Application.Features.Foods.GetFoodTags;
using FormUp.Application.Features.Foods.SearchFoods;
using FormUp.Application.Features.Foods.Shared;
using FormUp.Application.Features.Recipes.GetRecipe;
using FormUp.Application.Features.Recipes.SearchRecipes;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Infrastructure;
using MongoDB.Driver;
using NSubstitute;

namespace FormUp.Tests.Endpoints.Recipes;

/// <summary>
/// Thin per-collection handle onto the shared Mongo container (#1104 Phase B — see
/// <see cref="SharedTestContainers"/>). Each collection using this fixture gets its own
/// database name inside that one shared server instead of its own container.
/// </summary>
public class OwnerScopedVisibilityMongoContainerFixture(SharedTestContainers sharedContainers)
{
    public string ConnectionString => sharedContainers.MongoConnectionString;

    public string DatabaseName { get; } = SharedTestContainers.CreateMongoDatabaseName("ownerscopedvisibility");
}

[CollectionDefinition("OwnerScopedVisibilityFilter")]
public class OwnerScopedVisibilityFilterCollection : ICollectionFixture<OwnerScopedVisibilityMongoContainerFixture>;

/// <summary>
/// Testcontainers integration tests for the own-or-public visibility filter guard added in #992
/// to <see cref="SearchRecipesEndpoint"/>, <see cref="GetRecipeEndpoint"/>, and
/// <see cref="SearchFoodsEndpoint"/>. Boots a real MongoDB container because
/// <see cref="RecipeTestHelpers.CreateMockMongo"/> stubs <c>FindAsync</c> to return every seeded
/// document regardless of the <c>FilterDefinition</c> passed in — the filter is never evaluated,
/// so a test written against that harness cannot fail no matter what the endpoint's filter does
/// and proves nothing about the <see cref="Guid.Empty"/>-caller guard. Mirrors
/// <see cref="FormUp.Tests.Services.LibrarySearchHelperTests"/>'s approach: an
/// <see cref="IMongoContext"/> substitute whose Recipes/Foods properties return real,
/// containerised collections.
/// </summary>
/// <remarks>
/// A unique <c>Guid.NewGuid()</c> external id per fact is NOT sufficient isolation on its own:
/// <see cref="SearchRecipesEndpoint"/> and <see cref="SearchFoodsEndpoint"/> match on
/// visibility, not on identity, so a Public row seeded by one fact stays a permanent match for
/// every later fact's search once the Mongo collection is shared across the whole class (the
/// fixture is <c>ICollectionFixture</c>-scoped, one database per class run, not per fact). CI's
/// first run of #1104 failed exactly this way — two facts each insert a recipe/food named
/// "Others Public" and a later <c>ContainSingle</c>/<c>TotalCount</c> assertion saw both. Each
/// fact clears both collections in its own <see cref="IAsyncLifetime.InitializeAsync"/>, mirroring
/// <see cref="FormUp.Tests.Services.LibrarySearchHelperTests"/>.
/// </remarks>
[Collection("OwnerScopedVisibilityFilter")]
public class OwnerScopedVisibilityFilterTests : IAsyncLifetime
{
    private readonly IMongoCollection<Recipe> _recipes;
    private readonly IMongoCollection<Food> _foods;
    private readonly IMongoCollection<FoodTag> _foodTags;
    private readonly IMongoCollection<FoodTagAssignment> _foodTagAssignments;
    private readonly IMongoContext _mongoContext;

    public OwnerScopedVisibilityFilterTests(OwnerScopedVisibilityMongoContainerFixture containerFixture)
    {
        var mongoClient = new MongoClient(containerFixture.ConnectionString);
        var mongoDb = mongoClient.GetDatabase(containerFixture.DatabaseName);
        _recipes = mongoDb.GetCollection<Recipe>("recipes");
        _foods = mongoDb.GetCollection<Food>("foods");
        _foodTags = mongoDb.GetCollection<FoodTag>("foodTags");
        _foodTagAssignments = mongoDb.GetCollection<FoodTagAssignment>("foodTagAssignments");

        var mongoContext = Substitute.For<IMongoContext>();
        mongoContext.Recipes.Returns(_recipes);
        mongoContext.Foods.Returns(_foods);
        mongoContext.FoodTags.Returns(_foodTags);
        mongoContext.FoodTagAssignments.Returns(_foodTagAssignments);
        _mongoContext = mongoContext;
    }

    public async ValueTask InitializeAsync()
    {
        await _recipes.DeleteManyAsync(FilterDefinition<Recipe>.Empty);
        await _foods.DeleteManyAsync(FilterDefinition<Food>.Empty);
        await _foodTags.DeleteManyAsync(FilterDefinition<FoodTag>.Empty);
        await _foodTagAssignments.DeleteManyAsync(FilterDefinition<FoodTagAssignment>.Empty);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ── helpers ──────────────────────────────────────────────────────────────

    private static Recipe MakeRecipe(Guid nutritionistId, string name, RecipeVisibility visibility) =>
        new()
        {
            ExternalId = Guid.NewGuid(),
            NutritionistId = nutritionistId,
            Name = name,
            Visibility = visibility,
            TotalNutrients = new NutrientTotals(),
            DateCreated = DateTime.UtcNow,
        };

    // NutritionistId is explicitly Guid.Empty (never null) below — an absent/null field would
    // pass against the UNFIXED code too and prove nothing about the guard under test.
    private static Food MakeFood(Guid nutritionistId, string name, FoodVisibility visibility) =>
        new()
        {
            ExternalId = Guid.NewGuid(),
            Name = name,
            NutritionistId = nutritionistId,
            Visibility = visibility,
            IsDeleted = false,
            DateCreated = DateTime.UtcNow,
        };

    // NutritionistId is explicitly null — a system/seed food with no owning nutritionist.
    // Food.NutritionistId carries no [BsonIgnoreIfNull], so this stores an explicit BSON null
    // rather than omitting the element.
    private static Food MakeOwnerlessFood(string name, FoodVisibility visibility) =>
        new()
        {
            ExternalId = Guid.NewGuid(),
            Name = name,
            NutritionistId = null,
            Visibility = visibility,
            IsDeleted = false,
            DateCreated = DateTime.UtcNow,
        };

    // A nutritionist whose user-id claim cannot be parsed as a Guid, so GetFoodEndpoint's
    // Guid.TryParse leaves currentUserId null. Built inline rather than via
    // EndpointTestHelpers.FakeUserClaims, which only takes a well-formed Guid.
    private static Claim[] UnparsableCallerClaims() =>
    [
        new Claim(AppClaims.UserId, "not-a-guid"),
        new Claim(AppClaims.Email, "test@test.com"),
        new Claim(ClaimTypes.Role, AppRoles.Nutritionist)
    ];

    // ── tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// #992 error path: a Private recipe whose <c>nutritionistId</c> is an explicit zero uuid must
    /// be excluded for an empty caller, while an unrelated Public recipe is still returned.
    /// </summary>
    [Fact]
    public async Task SearchRecipes_EmptyCallerId_ExcludesPrivateRecipeWithZeroUuidOwner_ButIncludesPublic()
    {
        var ct = TestContext.Current.CancellationToken;

        var zeroOwnerPrivate = MakeRecipe(Guid.Empty, "Zero Owner Private", RecipeVisibility.Private);
        var othersPublic = MakeRecipe(Guid.NewGuid(), "Others Public", RecipeVisibility.Public);

        await _recipes.InsertManyAsync([zeroOwnerPrivate, othersPublic], cancellationToken: ct);

        var ep = Factory.Create<SearchRecipesEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(Guid.Empty, AppRoles.Nutritionist))),
            _mongoContext);

        await ep.HandleAsync(new SearchRecipesRequest(), ct);

        ep.Response.Recipes.Should().ContainSingle(r => r.Name == "Others Public");
        ep.Response.TotalCount.Should().Be(1);
    }

    /// <summary>
    /// #992 error path: a Private food whose <c>nutritionistId</c> is an explicit zero uuid (not
    /// null — <see cref="Food.NutritionistId"/> is <c>Guid?</c>) must be excluded for an empty
    /// caller, while an unrelated Public non-deleted food is still returned.
    /// </summary>
    [Fact]
    public async Task SearchFoods_EmptyCallerId_ExcludesPrivateFoodWithZeroUuidOwner_ButIncludesPublic()
    {
        var ct = TestContext.Current.CancellationToken;

        var zeroOwnerPrivate = MakeFood(Guid.Empty, "Zero Owner Private", FoodVisibility.Private);
        var othersPublic = MakeFood(Guid.NewGuid(), "Others Public", FoodVisibility.Public);

        await _foods.InsertManyAsync([zeroOwnerPrivate, othersPublic], cancellationToken: ct);

        var ep = Factory.Create<SearchFoodsEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(Guid.Empty, AppRoles.Nutritionist))),
            _mongoContext);

        await ep.HandleAsync(new SearchFoodsRequest(), ct);

        ep.Response.Foods.Should().ContainSingle(f => f.Name == "Others Public");
        ep.Response.TotalCount.Should().Be(1);
    }

    /// <summary>
    /// #992 error path — the third own-or-public site found beyond the AC by shape-grep, and the
    /// highest-consequence: a single-document read gated solely by the disjunct. A Private recipe
    /// whose <c>nutritionistId</c> is an explicit zero uuid must 404 for an empty caller, while a
    /// Public recipe still resolves.
    /// </summary>
    [Fact]
    public async Task GetRecipe_EmptyCallerId_PrivateRecipeWithZeroUuidOwner_Returns404_PublicRecipe_Returns200()
    {
        var ct = TestContext.Current.CancellationToken;

        var zeroOwnerPrivate = MakeRecipe(Guid.Empty, "Zero Owner Private", RecipeVisibility.Private);
        var othersPublic = MakeRecipe(Guid.NewGuid(), "Others Public", RecipeVisibility.Public);

        await _recipes.InsertManyAsync([zeroOwnerPrivate, othersPublic], cancellationToken: ct);

        var privateEp = Factory.Create<GetRecipeEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(Guid.Empty, AppRoles.Nutritionist))),
            _mongoContext);

        await privateEp.HandleAsync(
            new GetRecipeRequest { RecipeId = zeroOwnerPrivate.ExternalId }, ct);

        privateEp.HttpContext.Response.StatusCode.Should().Be(404);

        var publicEp = Factory.Create<GetRecipeEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(Guid.Empty, AppRoles.Nutritionist))),
            _mongoContext);

        await publicEp.HandleAsync(
            new GetRecipeRequest { RecipeId = othersPublic.ExternalId }, ct);

        publicEp.HttpContext.Response.StatusCode.Should().Be(200);
    }

    /// <summary>
    /// #1010 error path: the fourth member of the own-or-public family. A Private food with NO
    /// owner must 404 for a nutritionist whose id claim could not be parsed. Before the fix the
    /// gate ran in memory as <c>food.NutritionistId != currentUserId</c> with both sides
    /// <see langword="null"/>, which evaluated false and returned the food as "owned by the
    /// caller".
    /// </summary>
    [Fact]
    public async Task GetFood_UnparsableCallerId_PrivateOwnerlessFood_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;

        var ownerlessPrivate = MakeOwnerlessFood("Ownerless Private", FoodVisibility.Private);
        await _foods.InsertOneAsync(ownerlessPrivate, cancellationToken: ct);

        var ep = Factory.Create<GetFoodEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(UnparsableCallerClaims())),
            _mongoContext);

        await ep.HandleAsync(new GetFoodRequest { FoodId = ownerlessPrivate.ExternalId }, ct);

        ep.HttpContext.Response.StatusCode.Should().Be(404);
    }

    /// <summary>
    /// #1010 success path, so the test above cannot pass by blanket-denying an unparsable caller:
    /// a Public ownerless food still resolves for the same caller.
    /// </summary>
    [Fact]
    public async Task GetFood_UnparsableCallerId_PublicOwnerlessFood_Returns200()
    {
        var ct = TestContext.Current.CancellationToken;

        var ownerlessPublic = MakeOwnerlessFood("Ownerless Public", FoodVisibility.Public);
        await _foods.InsertOneAsync(ownerlessPublic, cancellationToken: ct);

        var ep = Factory.Create<GetFoodEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(UnparsableCallerClaims())),
            _mongoContext);

        await ep.HandleAsync(new GetFoodRequest { FoodId = ownerlessPublic.ExternalId }, ct);

        ep.HttpContext.Response.StatusCode.Should().Be(200);
    }

    /// <summary>
    /// #1010 regression guard: a nutritionist still reads their OWN Private food. Pins that the
    /// move from an in-memory post-filter to a Mongo-level Or clause did not narrow the reachable
    /// own-or-public behaviour.
    /// </summary>
    [Fact]
    public async Task GetFood_OwningNutritionist_PrivateOwnFood_Returns200()
    {
        var ct = TestContext.Current.CancellationToken;

        var ownerId = Guid.NewGuid();
        var ownPrivate = MakeFood(ownerId, "Own Private", FoodVisibility.Private);
        await _foods.InsertOneAsync(ownPrivate, cancellationToken: ct);

        var ep = Factory.Create<GetFoodEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(ownerId, AppRoles.Nutritionist))),
            _mongoContext);

        await ep.HandleAsync(new GetFoodRequest { FoodId = ownPrivate.ExternalId }, ct);

        ep.HttpContext.Response.StatusCode.Should().Be(200);
    }

    /// <summary>
    /// Moved here from <c>GetFoodEndpointTests.HandleAsync_PrivateFood_OtherNutritionistGets404</c>
    /// by #1010. That test ran against <c>FoodTestHelpers.CreateMockMongo</c>, whose
    /// <c>FindAsync</c> stub returns every seeded food without evaluating the
    /// <c>FilterDefinition</c>, so it could only observe a gate applied AFTER the read. Once the
    /// gate moved into the query it became structurally blind — it belongs on a real collection.
    /// </summary>
    [Fact]
    public async Task GetFood_OtherNutritionist_OthersPrivateFood_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;

        var othersPrivate = MakeFood(Guid.NewGuid(), "Others Private 404", FoodVisibility.Private);
        await _foods.InsertOneAsync(othersPrivate, cancellationToken: ct);

        var ep = Factory.Create<GetFoodEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(Guid.NewGuid(), AppRoles.Nutritionist))),
            _mongoContext);

        await ep.HandleAsync(new GetFoodRequest { FoodId = othersPrivate.ExternalId }, ct);

        ep.HttpContext.Response.StatusCode.Should().Be(404);
    }

    /// <summary>
    /// #1010 regression guard: a Client reading a Private food referenced by their plan is still
    /// exempt from the visibility gate — the behaviour the endpoint's summary promises.
    /// </summary>
    [Fact]
    public async Task GetFood_ClientCaller_OthersPrivateFood_Returns200()
    {
        var ct = TestContext.Current.CancellationToken;

        var othersPrivate = MakeFood(Guid.NewGuid(), "Others Private", FoodVisibility.Private);
        await _foods.InsertOneAsync(othersPrivate, cancellationToken: ct);

        var ep = Factory.Create<GetFoodEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(Guid.NewGuid(), AppRoles.Client))),
            _mongoContext);

        await ep.HandleAsync(new GetFoodRequest { FoodId = othersPrivate.ExternalId }, ct);

        ep.HttpContext.Response.StatusCode.Should().Be(200);
    }

    /// <summary>
    /// #1120 success path: the food-tags filter matches ANY of the supplied tag ids, scoped to the
    /// caller's own <c>FoodTagAssignment</c> — a match on the caller's own Private food is still
    /// returned.
    /// </summary>
    [Fact]
    public async Task SearchFoods_ByTagId_MatchesOwnedAssignment_ReturnsTaggedFood()
    {
        var ct = TestContext.Current.CancellationToken;
        var ownerId = Guid.NewGuid();

        var ownPrivate = MakeFood(ownerId, "Own Private Tagged", FoodVisibility.Private);
        await _foods.InsertOneAsync(ownPrivate, cancellationToken: ct);

        var tag = new FoodTag
        {
            ExternalId = Guid.NewGuid(),
            OwnerUserId = ownerId,
            Name = "High Protein",
            NormalizedName = "high protein",
            ColorHex = "#3b82f6",
            Version = 1,
            DateCreated = DateTime.UtcNow,
        };
        await _foodTags.InsertOneAsync(tag, cancellationToken: ct);
        await _foodTagAssignments.InsertOneAsync(new FoodTagAssignment
        {
            OwnerUserId = ownerId,
            FoodExternalId = ownPrivate.ExternalId,
            TagIds = [tag.ExternalId],
            DateCreated = DateTime.UtcNow,
        }, cancellationToken: ct);

        var ep = Factory.Create<SearchFoodsEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(ownerId, AppRoles.Nutritionist))),
            _mongoContext);

        await ep.HandleAsync(new SearchFoodsRequest { TagIds = [tag.ExternalId, Guid.NewGuid()] }, ct);

        ep.Response.Foods.Should().ContainSingle(f => f.Name == "Own Private Tagged");
        ep.Response.Foods[0].Tags.Should().ContainSingle(t => t.TagId == tag.ExternalId);
    }

    /// <summary>
    /// #1120 error path: a tag assignment owned by a DIFFERENT coach must not surface a food in
    /// the caller's search — assignments are scoped by OwnerUserId, so the caller's own search
    /// with the other coach's tag id matches zero of the caller's own <c>FoodTagAssignment</c>
    /// rows regardless of whether the underlying food is visible to the caller.
    /// </summary>
    [Fact]
    public async Task SearchFoods_TagOwnedByAnotherCoach_ReturnsZeroRows()
    {
        var ct = TestContext.Current.CancellationToken;
        var otherCoachId = Guid.NewGuid();

        var publicFood = MakeFood(otherCoachId, "Shared Public Food", FoodVisibility.Public);
        await _foods.InsertOneAsync(publicFood, cancellationToken: ct);

        var othersTag = new FoodTag
        {
            ExternalId = Guid.NewGuid(),
            OwnerUserId = otherCoachId,
            Name = "Meal Prep",
            NormalizedName = "meal prep",
            ColorHex = "#3b82f6",
            Version = 1,
            DateCreated = DateTime.UtcNow,
        };
        await _foodTags.InsertOneAsync(othersTag, cancellationToken: ct);
        await _foodTagAssignments.InsertOneAsync(new FoodTagAssignment
        {
            OwnerUserId = otherCoachId,
            FoodExternalId = publicFood.ExternalId,
            TagIds = [othersTag.ExternalId],
            DateCreated = DateTime.UtcNow,
        }, cancellationToken: ct);

        var ep = Factory.Create<SearchFoodsEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(Guid.NewGuid(), AppRoles.Nutritionist))),
            _mongoContext);

        await ep.HandleAsync(new SearchFoodsRequest { TagIds = [othersTag.ExternalId] }, ct);

        ep.Response.Foods.Should().BeEmpty();
        ep.Response.TotalCount.Should().Be(0);
    }

    /// <summary>
    /// #1115 success path: the category filter is multi-value, match-any. Foods span three
    /// categories; searching with two of them returns only those two, filtered to this test's own
    /// uniquely-named foods since the collection is shared across facts.
    /// </summary>
    [Fact]
    public async Task SearchFoods_TwoCategoriesSupplied_ReturnsOnlyThoseCategories()
    {
        var ct = TestContext.Current.CancellationToken;
        var ownerId = Guid.NewGuid();

        var fruitFood = MakeFood(ownerId, "Category Filter Fruit", FoodVisibility.Public);
        fruitFood.Category = FoodCategory.Fruit;
        var dairyFood = MakeFood(ownerId, "Category Filter Dairy", FoodVisibility.Public);
        dairyFood.Category = FoodCategory.Dairy;
        var meatFood = MakeFood(ownerId, "Category Filter Meat", FoodVisibility.Public);
        meatFood.Category = FoodCategory.Meat;
        await _foods.InsertManyAsync([fruitFood, dairyFood, meatFood], cancellationToken: ct);

        var ep = Factory.Create<SearchFoodsEndpoint>(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(ownerId, AppRoles.Nutritionist))),
            _mongoContext);

        await ep.HandleAsync(
            new SearchFoodsRequest { Categories = [FoodCategory.Fruit, FoodCategory.Dairy] }, ct);

        ep.Response.Foods.Should().HaveCount(2);
        ep.Response.Foods.Select(f => f.Name).Should().BeEquivalentTo(
            ["Category Filter Fruit", "Category Filter Dairy"]);
    }

    /// <summary>
    /// #1120 owner-scoping: <c>GET /trainer/food-tags</c> must never list a tag owned by a
    /// different nutritionist — each coach's food-tag catalog is entirely private to them.
    /// </summary>
    [Fact]
    public async Task GetFoodTags_SecondNutritionist_SeesNoneOfFirstsTags()
    {
        var ct = TestContext.Current.CancellationToken;
        var firstCoachId = Guid.NewGuid();
        var secondCoachId = Guid.NewGuid();

        await _foodTags.InsertOneAsync(new FoodTag
        {
            ExternalId = Guid.NewGuid(),
            OwnerUserId = firstCoachId,
            Name = "First Coach Only",
            NormalizedName = "first coach only",
            ColorHex = "#3b82f6",
            Version = 1,
            DateCreated = DateTime.UtcNow,
        }, cancellationToken: ct);

        var ep = Factory.Create<GetFoodTagsEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(secondCoachId, AppRoles.Nutritionist)))),
            _mongoContext);

        await ep.HandleAsync(ct);

        ep.Response.Tags.Should().BeEmpty();
    }

    /// <summary>
    /// #1120 success path: a caller's own food tags still appear in their own tags listing,
    /// sorted by name.
    /// </summary>
    [Fact]
    public async Task GetFoodTags_OwnTags_AreListedSortedByName()
    {
        var ct = TestContext.Current.CancellationToken;
        var ownerId = Guid.NewGuid();

        await _foodTags.InsertManyAsync(
        [
            new FoodTag
            {
                ExternalId = Guid.NewGuid(),
                OwnerUserId = ownerId,
                Name = "Zucchini",
                NormalizedName = "zucchini",
                ColorHex = "#3b82f6",
                Version = 1,
                DateCreated = DateTime.UtcNow,
            },
            new FoodTag
            {
                ExternalId = Guid.NewGuid(),
                OwnerUserId = ownerId,
                Name = "Almond",
                NormalizedName = "almond",
                ColorHex = "#f59e0b",
                Version = 1,
                DateCreated = DateTime.UtcNow,
            },
        ], cancellationToken: ct);

        var ep = Factory.Create<GetFoodTagsEndpoint>(EndpointTestHelpers.WithEntitlements(
            ctx => ctx.Request.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity(EndpointTestHelpers.FakeUserClaims(ownerId, AppRoles.Nutritionist)))),
            _mongoContext);

        await ep.HandleAsync(ct);

        ep.Response.Tags.Select(t => t.Name).Should().Equal("Almond", "Zucchini");
    }
}
