using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace FormUp.Tests.Seeding;

// ---------------------------------------------------------------------------
// Factory — mirrors QaSeedRunnerFactory's shape (Seed/QaSeedRunnerTests.cs) but
// runs in its own collection so the large catalog seed (184 foods / 124 recipes /
// 130 exercises / 10 workout templates) doesn't contend with the shared
// "Integration" collection's Testcontainers.
// ---------------------------------------------------------------------------

/// <summary>
/// WebApplicationFactory that wires up real Postgres + Mongo via Testcontainers for
/// public-catalog-seeding tests (issue #809).
/// </summary>
public class CatalogSeedingFactory(SharedTestContainers sharedContainers) : WebApplicationFactory<Program>, IAsyncLifetime
{
    private string _postgresConnectionString = string.Empty;
    private string _mongoDatabaseName = string.Empty;
    private bool _catalogSeeded;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("POSTGRES_PASSWORD", "test");
        builder.UseSetting("MONGO_PASSWORD", "test");
        builder.UseSetting("MINIO_ACCESS_KEY", "test");
        builder.UseSetting("MINIO_SECRET_KEY", "test");
        builder.UseSetting("JWT_SECRET", new string('x', 64));
        builder.UseSetting("RateLimiting:Disabled", "true");

        builder.UseSetting("ConnectionStrings:PostgreSQl",
            "Host=localhost;Database=placeholder;Username=postgres");
        builder.UseSetting("ConnectionStrings:MongoDB",
            "mongodb://localhost:27017");

        builder.ConfigureServices(services =>
        {
            var pgDesc = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<ApplicationDbContext>));
            if (pgDesc is not null) services.Remove(pgDesc);

            services.AddDbContext<ApplicationDbContext>(options =>
                options.UseNpgsql(_postgresConnectionString)
                    .ConfigureWarnings(w => w.Ignore(
                        Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));

            var mongoDbDesc = services.SingleOrDefault(d => d.ServiceType == typeof(IMongoDatabase));
            if (mongoDbDesc is not null) services.Remove(mongoDbDesc);

            var mongoCtxDesc = services.SingleOrDefault(d => d.ServiceType == typeof(IMongoContext));
            if (mongoCtxDesc is not null) services.Remove(mongoCtxDesc);

            services.AddSingleton<IMongoDatabase>(_ =>
            {
                var client = new MongoClient(sharedContainers.MongoConnectionString);
                return client.GetDatabase(_mongoDatabaseName);
            });
            services.AddSingleton<IMongoContext, MongoContext>();

            var emailDesc = services.SingleOrDefault(d => d.ServiceType == typeof(IEmailService));
            if (emailDesc is not null) services.Remove(emailDesc);
            services.AddSingleton<FakeEmailService>();
            services.AddSingleton<IEmailService>(sp => sp.GetRequiredService<FakeEmailService>());

            var notifierDesc = services.SingleOrDefault(d => d.ServiceType == typeof(IRealtimeNotifier));
            if (notifierDesc is not null) services.Remove(notifierDesc);
            services.AddSingleton<FakeRealtimeNotifier>();
            services.AddSingleton<IRealtimeNotifier>(sp => sp.GetRequiredService<FakeRealtimeNotifier>());

            var blobDesc = services.SingleOrDefault(d => d.ServiceType == typeof(IBlobStorageService));
            if (blobDesc is not null) services.Remove(blobDesc);
            services.AddSingleton<IBlobStorageService, FakeBlobStorageService>();

            var pushDesc = services.SingleOrDefault(d => d.ServiceType == typeof(IPushNotificationService));
            if (pushDesc is not null) services.Remove(pushDesc);
            services.AddSingleton<FakePushNotificationService>();
            services.AddSingleton<IPushNotificationService>(sp => sp.GetRequiredService<FakePushNotificationService>());

            // #726: prevent the background schedulers/worker from starting in this test host.
            services.RemoveBackgroundHostedServices();
        });

        builder.UseEnvironment("Development");
    }

    public async ValueTask InitializeAsync()
    {
        _postgresConnectionString = await sharedContainers.CreatePostgresDatabaseAsync("catalog_seeding");
        _mongoDatabaseName = SharedTestContainers.CreateMongoDatabaseName("catalog_seeding");

        FastEndpointsBootGate.Start(this);

        // Applies migrations, seeds roles, and — per #809 — the system admin user.
        await ApplicationDbContextSeed.SeedAsync(Services);
    }

    /// <summary>
    /// Wipes Postgres and Mongo and marks the shared catalog seed as gone.
    /// </summary>
    public async Task ResetAsync()
    {
        _catalogSeeded = false;
        await Services.ResetPostgresAndMongoAsync();
    }

    /// <summary>
    /// Makes sure the database holds exactly one catalog seed, seeding it only when a previous
    /// fact wiped or replaced it. Read-only facts share that one seed.
    /// </summary>
    public async Task EnsureCatalogSeededAsync()
    {
        if (_catalogSeeded)
        {
            return;
        }

        await ResetAsync();
        await MongoSeeder.SeedAsync(Services);
        _catalogSeeded = true;
    }

    // Skip base.DisposeAsync() — see QaSeedRunnerFactory / FitnessApiFactory for the
    // ObjectDisposedException rationale (FastEndpoints' process-global ServiceResolver). No
    // Testcontainer to dispose here anymore (#1104 Phase B); SharedTestContainers owns that.
    public new ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Defines a separate collection for catalog seeding tests so they run serially and don't
/// contend with the shared Integration collection's Testcontainers. Boots
/// <see cref="CatalogSeedingFactory"/> ONCE for the collection rather than per test — see
/// <see cref="CatalogSeedingFactory.EnsureCatalogSeededAsync"/> for how facts share one seed (#1104).
/// </summary>
[CollectionDefinition("CatalogSeedingTests")]
public class CatalogSeedingTestsCollection : ICollectionFixture<CatalogSeedingFactory>;

/// <summary>
/// Integration tests for the public-catalog seeding pipeline (#809): system admin user,
/// foods/recipes/exercises/workout templates loaded from the embedded JSON seed data.
/// </summary>
/// <remarks>
/// Read-only catalog facts share one seeded database (<see cref="CatalogSeedingFactory.EnsureCatalogSeededAsync"/>).
/// Facts that assert exact counts after a re-seed, pre-insert legacy rows or test the reset helper
/// itself wipe first with <see cref="CatalogSeedingFactory.ResetAsync"/>.
/// </remarks>
[Collection("CatalogSeedingTests")]
[TestCaseOrderer(typeof(WipingTestsLastOrderer))]
public class CatalogSeedingTests(CatalogSeedingFactory factory)
{
    private readonly CatalogSeedingFactory _factory = factory;

    /// <summary>
    /// The reset helper recreates the Mongo indexes it drops (#1129).
    /// </summary>
    [Fact]
    [Trait(WipingTestsLastOrderer.TraitName, WipingTestsLastOrderer.TraitValue)]
    public async Task InitializeAsync_RecreatesFoodTagIndexes_UniqueOwnerNormalizedNameIndexExists()
    {
        var ct = TestContext.Current.CancellationToken;

        await _factory.ResetAsync();

        using var scope = _factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        var indexes =await (await mongo.FoodTags.Indexes.ListAsync(ct)).ToListAsync(ct);

        var ownerNameIndex = indexes.SingleOrDefault(
            i => i["name"].AsString == "idx_foodtag_ownerUserId_normalizedName");
        ownerNameIndex.Should().NotBeNull(
            because: "ResetPostgresAndMongoAsync must recreate the foodTags indexes after dropping the collection");
        ownerNameIndex!["unique"].AsBoolean.Should().BeTrue(
            because: "the per-owner normalized-name constraint must stay unique after the reset");
    }

    /// <summary>
    /// Running MongoSeeder.SeedAsync twice must be idempotent: document counts stay at the
    /// exact number of JSON seed entries — no duplicates on re-seed.
    /// </summary>
    [Fact]
    [Trait(WipingTestsLastOrderer.TraitName, WipingTestsLastOrderer.TraitValue)]
    public async Task SeedAsync_RunTwice_IsIdempotent()
    {
        var ct = TestContext.Current.CancellationToken;

        await _factory.ResetAsync();
        await MongoSeeder.SeedAsync(_factory.Services);
        await MongoSeeder.SeedAsync(_factory.Services);

        using var scope = _factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        var foodCount = await mongo.Foods.CountDocumentsAsync(FilterDefinition<Food>.Empty, cancellationToken: ct);
        var recipeCount = await mongo.Recipes.CountDocumentsAsync(FilterDefinition<Recipe>.Empty, cancellationToken: ct);
        var exerciseCount = await mongo.Exercises.CountDocumentsAsync(FilterDefinition<Exercise>.Empty, cancellationToken: ct);
        var templateCount = await mongo.SessionTemplates.CountDocumentsAsync(FilterDefinition<SessionTemplate>.Empty, cancellationToken: ct);

        foodCount.Should().Be(FoodSeedData.LoadEntries().Count, "re-seeding foods must not create duplicates");
        recipeCount.Should().Be(RecipeSeedData.LoadEntries().Count, "re-seeding recipes must not create duplicates");
        exerciseCount.Should().Be(ExerciseSeedData.LoadEntries().Count, "re-seeding exercises must not create duplicates");
        templateCount.Should().Be(SessionTemplateSeedData.LoadEntries().Count, "re-seeding workout templates must not create duplicates");
    }

    /// <summary>
    /// The system admin user (fixed GUID, non-loginable) must be created by
    /// ApplicationDbContextSeed.SeedAsync — already run during factory InitializeAsync — with
    /// EmailConfirmed=true, IsActive=true, and Admin role membership.
    /// </summary>
    [Fact]
    public async Task SeedAsync_SystemAdminUser_ExistsWithAdminRoleAndEmailConfirmed()
    {
        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var admin = await userManager.FindByIdAsync(SystemUsers.AdminId.ToString());

        admin.Should().NotBeNull("the system admin user must be seeded by ApplicationDbContextSeed.SeedAsync");
        admin!.Email.Should().Be(SystemUsers.AdminEmail);
        admin.EmailConfirmed.Should().BeTrue();
        admin.IsActive.Should().BeTrue();

        (await userManager.IsInRoleAsync(admin, UserRole.Admin.ToString()))
            .Should().BeTrue("the system admin must be a member of the Admin role");
    }

    /// <summary>
    /// Seeding twice must not create a second system admin user or duplicate its role membership.
    /// </summary>
    [Fact]
    public async Task SeedAsync_SystemAdminUser_SeedingTwiceIsIdempotent()
    {
        // ApplicationDbContextSeed.SeedAsync already ran once in InitializeAsync(); run again.
        await ApplicationDbContextSeed.SeedAsync(_factory.Services);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var adminCount = await db.Users.CountAsync(u => u.Id == SystemUsers.AdminId);
        adminCount.Should().Be(1, "the system admin user must not be duplicated across seed runs");
    }

    /// <summary>
    /// Seeded foods are public catalog entries — deliberately owner-less (NutritionistId=null)
    /// per the design spec §2 so /foods/custom doesn't misclassify them as a nutritionist's own.
    /// The SearchFoods visibility filter (IsDeleted=false AND (Public OR owned-by-caller)) must
    /// surface all of them to an arbitrary authenticated user.
    /// </summary>
    [Fact]
    public async Task SeedAsync_Foods_AreOwnerlessPublicAndSearchableByArbitraryUser()
    {
        var ct = TestContext.Current.CancellationToken;

        await _factory.EnsureCatalogSeededAsync();

        using var scope = _factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        var foods = await mongo.Foods.Find(FilterDefinition<Food>.Empty).ToListAsync(ct);
        foods.Should().NotBeEmpty();
        foods.Should().AllSatisfy(f =>
        {
            f.NutritionistId.Should().BeNull($"seeded catalog food '{f.Name}' must not be stamped with an owner");
            f.Visibility.Should().Be(FoodVisibility.Public);
        });

        // Mirrors SearchFoodsEndpoint's visibility filter.
        var arbitraryUserId = Guid.NewGuid();
        var filterBuilder = Builders<Food>.Filter;
        var filter = filterBuilder.Eq(f => f.IsDeleted, false)
            & filterBuilder.Or(
                filterBuilder.Eq(f => f.Visibility, FoodVisibility.Public),
                filterBuilder.Eq(f => f.NutritionistId, arbitraryUserId));

        var count = await mongo.Foods.CountDocumentsAsync(filter, cancellationToken: ct);
        count.Should().Be(foods.Count,
            "SearchFoods' visibility filter must surface every seeded public food to any authenticated user");
    }

    /// <summary>
    /// Seeded recipes are owned by the system admin, public, have positive computed totals, and
    /// every denormalized MealFood snapshot resolves to a seeded Food ExternalId. The
    /// SearchRecipes visibility filter (owned-by-caller OR Public) must surface all of them to an
    /// arbitrary authenticated nutritionist.
    /// </summary>
    [Fact]
    public async Task SeedAsync_Recipes_HaveValidTotalsResolveIngredientsAndAreSearchableByArbitraryUser()
    {
        var ct = TestContext.Current.CancellationToken;

        await _factory.EnsureCatalogSeededAsync();

        using var scope = _factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        var foodExternalIds = (await mongo.Foods
                .Find(FilterDefinition<Food>.Empty)
                .Project(f => f.ExternalId)
                .ToListAsync(ct))
            .ToHashSet();

        var recipes = await mongo.Recipes.Find(FilterDefinition<Recipe>.Empty).ToListAsync(ct);
        recipes.Should().NotBeEmpty();

        recipes.Should().AllSatisfy(r =>
        {
            r.TotalNutrients.Kcal.Should().BeGreaterThan(0, $"recipe '{r.Name}' must have positive total kcal");
            r.Visibility.Should().Be(RecipeVisibility.Public);
            r.NutritionistId.Should().Be(SystemUsers.AdminId);
            r.Foods.Should().NotBeEmpty($"recipe '{r.Name}' must have ingredients");

            foreach (var mealFood in r.Foods)
            {
                foodExternalIds.Should().Contain(mealFood.FoodExternalId,
                    $"recipe '{r.Name}' references FoodExternalId {mealFood.FoodExternalId} which must resolve to a seeded Food");
            }
        });

        // Mirrors SearchRecipesEndpoint's visibility filter.
        var arbitraryNutritionistId = Guid.NewGuid();
        var filterBuilder = Builders<Recipe>.Filter;
        var filter = filterBuilder.Or(
            filterBuilder.Eq(r => r.NutritionistId, arbitraryNutritionistId),
            filterBuilder.Eq(r => r.Visibility, RecipeVisibility.Public));

        var count = await mongo.Recipes.CountDocumentsAsync(filter, cancellationToken: ct);
        count.Should().Be(recipes.Count,
            "SearchRecipes' visibility filter must surface every seeded public recipe to any authenticated nutritionist");
    }

    /// <summary>
    /// Seeded recipes carry the JSON servings count (default 1) in the dedicated field instead of a
    /// note prefix, and store meal types as canonical enum names rather than lowercase spellings.
    /// </summary>
    [Fact]
    public async Task SeedAsync_Recipes_StoreServingsFieldAndCanonicalMealTypes()
    {
        var ct = TestContext.Current.CancellationToken;

        await _factory.EnsureCatalogSeededAsync();

        using var scope = _factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        var recipes = await mongo.Recipes.Find(FilterDefinition<Recipe>.Empty).ToListAsync(ct);
        var entriesByName = RecipeSeedData.LoadEntries().ToDictionary(e => e.Name);

        recipes.Should().Contain(r => entriesByName.ContainsKey(r.Name) && entriesByName[r.Name].Servings > 1,
            "the seed JSON has multi-serving recipes, so the test is not vacuous");

        recipes.Where(r => entriesByName.ContainsKey(r.Name)).Should().AllSatisfy(r =>
        {
            r.Servings.Should().Be(entriesByName[r.Name].Servings ?? 1, $"recipe '{r.Name}'");
            (r.Note ?? string.Empty).Should().NotStartWith("Recept na", $"recipe '{r.Name}'");
            (r.MealTypes ?? []).Should().AllSatisfy(mealType =>
                Enum.GetNames<RecipeMealType>().Should().Contain(mealType, $"recipe '{r.Name}'"));
        });
    }

    /// <summary>
    /// Seeded exercises are system catalog entries — deliberately owner-less (TrainerId=null,
    /// IsCustom=false, Source="system") per the design spec §2 so /exercises/custom doesn't
    /// misclassify them as a trainer's own.
    /// </summary>
    [Fact]
    public async Task SeedAsync_Exercises_AreOwnerlessNonCustomAndSystemSourced()
    {
        var ct = TestContext.Current.CancellationToken;

        await _factory.EnsureCatalogSeededAsync();

        using var scope = _factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        var exercises = await mongo.Exercises.Find(FilterDefinition<Exercise>.Empty).ToListAsync(ct);
        exercises.Should().NotBeEmpty();

        exercises.Should().AllSatisfy(e =>
        {
            e.TrainerId.Should().BeNull($"seeded catalog exercise '{e.Name}' must not be stamped with an owner");
            e.IsCustom.Should().BeFalse();
            e.Source.Should().Be("system");
            e.IsActive.Should().BeTrue();
        });
    }

    /// <summary>
    /// Every workout template exercise reference resolves to a seeded Exercise, and there are at
    /// least two templates per <see cref="WorkoutFormat"/> value (design spec §5).
    /// </summary>
    [Fact]
    public async Task SeedAsync_SessionTemplates_ResolveExercisesAndCoverEveryFormat()
    {
        var ct = TestContext.Current.CancellationToken;

        await _factory.EnsureCatalogSeededAsync();

        using var scope = _factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        var exerciseExternalIds = (await mongo.Exercises
                .Find(FilterDefinition<Exercise>.Empty)
                .Project(e => e.ExternalId)
                .ToListAsync(ct))
            .ToHashSet();

        var templates = await mongo.SessionTemplates.Find(FilterDefinition<SessionTemplate>.Empty).ToListAsync(ct);
        templates.Should().NotBeEmpty();

        templates.Should().AllSatisfy(t =>
        {
            t.OwnerId.Should().Be(SystemUsers.AdminId);
            t.Visibility.Should().Be(LibraryVisibility.Public);
            t.Version.Should().Be(1);
            t.Workouts.Should().NotBeEmpty($"template '{t.Name}' must have sections");

            foreach (var section in t.Workouts)
            {
                foreach (var exercise in section.Exercises)
                {
                    exerciseExternalIds.Should().Contain(exercise.ExerciseExternalId,
                        $"template '{t.Name}' section '{section.Name}' references ExerciseExternalId " +
                        $"{exercise.ExerciseExternalId} which must resolve to a seeded Exercise");
                }
            }
        });

        foreach (var format in Enum.GetValues<WorkoutFormat>())
        {
            templates.Count(t => t.Format == format).Should().BeGreaterThanOrEqualTo(2,
                $"at least 2 seeded workout templates must use format {format}");
        }
    }

    /// <summary>
    /// #810 review finding B1: on a DB seeded before this PR, a food/exercise whose Name already
    /// exists (with an old, random ExternalId) is skipped by the per-document name-dedupe — but
    /// recipes/workout templates must still resolve their ingredient/exercise references to that
    /// PRE-EXISTING document's actual ExternalId, not the in-memory deterministic one. Otherwise
    /// the reference dangles (points at an ExternalId no document carries).
    /// Simulates this by pre-inserting a "Whole Egg" food and a "Barbell Bench Press" exercise —
    /// both referenced by real seed recipes/templates — under random legacy ExternalIds before
    /// running the seeder.
    /// </summary>
    [Fact]
    [Trait(WipingTestsLastOrderer.TraitName, WipingTestsLastOrderer.TraitValue)]
    public async Task SeedAsync_LegacyNamedDocsWithRandomExternalIds_RecipeAndTemplateRefsBindToPreExistingDoc()
    {
        var ct = TestContext.Current.CancellationToken;

        await _factory.ResetAsync();

        using var scope = _factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        var legacyFoodExternalId = Guid.NewGuid();
        var legacyFood = new Food
        {
            ExternalId = legacyFoodExternalId,
            Name = "Whole Egg",
            LocalizedNames = new LocalizedNames { En = "Whole Egg", Cs = "Legacy vejce", De = "Legacy Ei" },
            Category = FoodCategory.Dairy,
            NutrientValue = new NutrientValue { Kcal = 999, Protein = 1, Carbs = 1, Fat = 1 },
            Visibility = FoodVisibility.Public,
            DateCreated = DateTime.UtcNow,
        };
        await mongo.Foods.InsertOneAsync(legacyFood, cancellationToken: ct);

        var legacyExerciseExternalId = Guid.NewGuid();
        var legacyExercise = new Exercise
        {
            ExternalId = legacyExerciseExternalId,
            Name = "Barbell Bench Press",
            LocalizedNames = new LocalizedNames { En = "Barbell Bench Press", Cs = "Legacy bench press", De = "Legacy Bankdrücken" },
            MuscleGroups = [MuscleGroup.Chest],
            Equipment = ExerciseEquipment.Barbell,
            Category = ExerciseCategory.Strength,
            Difficulty = ExerciseDifficulty.Intermediate,
            IsCustom = false,
            IsActive = true,
            Source = "system",
            DateCreated = DateTime.UtcNow,
        };
        await mongo.Exercises.InsertOneAsync(legacyExercise, cancellationToken: ct);

        await MongoSeeder.SeedAsync(_factory.Services);

        // No name duplicates — the seeder must have skipped inserting a second "Whole Egg" /
        // "Barbell Bench Press" and kept the pre-existing legacy document as-is.
        var wholeEggCount = await mongo.Foods.CountDocumentsAsync(
            Builders<Food>.Filter.Regex(f => f.Name, new MongoDB.Bson.BsonRegularExpression("^Whole Egg$", "i")),
            cancellationToken: ct);
        wholeEggCount.Should().Be(1, "the legacy 'Whole Egg' food must not be duplicated by the seeder");

        var benchPressCount = await mongo.Exercises.CountDocumentsAsync(
            Builders<Exercise>.Filter.Regex(e => e.Name, new MongoDB.Bson.BsonRegularExpression("^Barbell Bench Press$", "i")),
            cancellationToken: ct);
        benchPressCount.Should().Be(1, "the legacy 'Barbell Bench Press' exercise must not be duplicated by the seeder");

        var persistedWholeEgg = await mongo.Foods
            .Find(f => f.Name == "Whole Egg").FirstOrDefaultAsync(ct);
        persistedWholeEgg.Should().NotBeNull();
        persistedWholeEgg!.ExternalId.Should().Be(legacyFoodExternalId,
            "the pre-existing legacy food's ExternalId must survive — it was not re-inserted");

        var persistedBenchPress = await mongo.Exercises
            .Find(e => e.Name == "Barbell Bench Press").FirstOrDefaultAsync(ct);
        persistedBenchPress.Should().NotBeNull();
        persistedBenchPress!.ExternalId.Should().Be(legacyExerciseExternalId,
            "the pre-existing legacy exercise's ExternalId must survive — it was not re-inserted");

        // Every recipe referencing "Whole Egg" must bind to the legacy ExternalId, not the
        // in-memory deterministic one — this is the crux of B1.
        var recipes = await mongo.Recipes.Find(FilterDefinition<Recipe>.Empty).ToListAsync(ct);
        var recipesReferencingWholeEgg = recipes
            .Where(r => r.Foods.Any(mf => mf.FoodName == "Whole Egg"))
            .ToList();
        recipesReferencingWholeEgg.Should().NotBeEmpty("at least one seed recipe references Whole Egg — test fixture assumption");

        recipesReferencingWholeEgg.Should().AllSatisfy(r =>
        {
            var wholeEggRefs = r.Foods.Where(mf => mf.FoodName == "Whole Egg");
            wholeEggRefs.Should().AllSatisfy(mf =>
                mf.FoodExternalId.Should().Be(legacyFoodExternalId,
                    $"recipe '{r.Name}' must bind its Whole Egg reference to the pre-existing document's ExternalId, not a dangling deterministic one"));
        });

        // Every workout template referencing "Barbell Bench Press" must bind to the legacy
        // ExternalId, not the in-memory deterministic one.
        var templates = await mongo.SessionTemplates.Find(FilterDefinition<SessionTemplate>.Empty).ToListAsync(ct);
        var benchPressRefs = templates
            .SelectMany(t => t.Workouts)
            .SelectMany(s => s.Exercises)
            .Where(e => e.ExerciseName == "Barbell Bench Press")
            .ToList();
        benchPressRefs.Should().NotBeEmpty("at least one seed template references Barbell Bench Press — test fixture assumption");

        benchPressRefs.Should().AllSatisfy(e =>
            e.ExerciseExternalId.Should().Be(legacyExerciseExternalId,
                "workout template exercise refs must bind to the pre-existing document's ExternalId, not a dangling deterministic one"));

        // Full cross-reference integrity check, same as the other tests — no dangling refs anywhere.
        var allFoodExternalIds = (await mongo.Foods
                .Find(FilterDefinition<Food>.Empty)
                .Project(f => f.ExternalId)
                .ToListAsync(ct))
            .ToHashSet();
        recipes.SelectMany(r => r.Foods).Should().AllSatisfy(mf =>
            allFoodExternalIds.Should().Contain(mf.FoodExternalId));

        var allExerciseExternalIds = (await mongo.Exercises
                .Find(FilterDefinition<Exercise>.Empty)
                .Project(e => e.ExternalId)
                .ToListAsync(ct))
            .ToHashSet();
        templates.SelectMany(t => t.Workouts).SelectMany(s => s.Exercises).Should().AllSatisfy(e =>
            allExerciseExternalIds.Should().Contain(e.ExerciseExternalId));
    }
}
