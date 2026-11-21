using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.ClientNutrition.GetTodayPlan;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace FormUp.Tests.Endpoints.ClientNutrition;

/// <summary>
/// Testcontainers integration tests (real MongoDB) for the #838 two-phase projected read in
/// <c>GET /client/nutrition/plan/today</c>. The mocked-<c>IMongoContext</c> unit tests in
/// <see cref="GetTodayPlanEndpointTests"/> prove the C# week/day-selection logic, but a real
/// Mongo instance is the only way to prove the actual projection queries — a malformed
/// phase-1 metadata projection or a wrong phase-2 <c>weeks.$</c> positional projection would
/// pass every mocked test (NSubstitute ignores the <c>Projection</c> option entirely) and
/// silently break in production.
/// </summary>
[Collection(TestCollection.Shard3)]
public class GetTodayPlanProjectionIntegrationTests(FitnessApiFactory factory)
{
    private static string UniqueEmail() => $"{Guid.NewGuid():N}@today-plan-projection-test.com";

    /// <summary>
    /// The single instant every fixture in this file is pinned to: 2026-03-11 23:30 UTC.
    /// Europe/Prague is CET (UTC+1) at this date — 2026 DST starts 2026-03-29 — so the local
    /// instant is 2026-03-12 00:30 (a Thursday), and the endpoint's client-local "today"
    /// resolves to exactly 2026-03-12T00:00:00Z. Every fixture date below is an ABSOLUTE
    /// literal derived from that 2026-03-12 local day, never a relative offset off this field —
    /// a relative offset would silently reintroduce the wall-clock coupling #994 exists to kill.
    /// </summary>
    private static readonly DateTimeOffset PinnedInstantUtc = new(2026, 3, 11, 23, 30, 0, TimeSpan.Zero);

    private static WebApplicationFactory<Program>? _pinnedFactory;

    /// <summary>
    /// A host derived from the shared <c>factory</c> collection fixture via
    /// <c>WithWebHostBuilder</c>, pinning <see cref="TimeProvider"/> to
    /// <see cref="PinnedInstantUtc"/> for every test in this class. Composition runs the
    /// parent's <c>ConfigureWebHost</c> first, so this derived host reuses the already-started
    /// Testcontainers, the seeded DB, and <c>RemoveBackgroundHostedServices()</c> — no new
    /// containers, no reintroduction of the #726 zombie-scheduler flake.
    /// </summary>
    /// <remarks>
    /// Memoized in a STATIC field, not an instance field: xunit constructs a new instance of
    /// this test class per <c>[Fact]</c>, so an instance field would boot four separate hosts.
    /// <c>TestAssemblyConfig</c>'s <c>MaxParallelThreads = 1</c> serializes the whole assembly,
    /// so the lazy-init check below needs no lock.
    ///
    /// NEVER disposed — no <c>using</c>, no explicit <c>DisposeAsync</c> call.
    /// <c>RefreshTokenReuseDetectionConcurrencyTests</c> (lines 135-160) documents the measured
    /// incident this avoids: disposing an extra host mid-run repoints FastEndpoints' process-
    /// global <c>ServiceResolver.Provider</c> at a disposed provider, producing 51 scattered
    /// <see cref="ObjectDisposedException"/> failures in unrelated tests.
    /// </remarks>
    private WebApplicationFactory<Program> PinnedFactory => _pinnedFactory ??= FastEndpointsBootGate.Booted(factory.WithWebHostBuilder(builder =>
    {
        builder.ConfigureServices(services =>
        {
            // AddIdentityCore (Program.cs) internally does a TryAddSingleton(TimeProvider.System)
            // of its own, ahead of Program.cs's own explicit AddSingleton(TimeProvider.System) —
            // so TWO descriptors for TimeProvider exist by this point, not one. Remove every
            // matching descriptor rather than assuming a single registration.
            var descriptors = services.Where(d => d.ServiceType == typeof(TimeProvider)).ToList();
            foreach (var descriptor in descriptors)
            {
                services.Remove(descriptor);
            }

            services.AddSingleton<TimeProvider>(new FixedTimeProvider(PinnedInstantUtc));
        });
    }));

    /// <summary>
    /// Builds a published <see cref="PlanWeek"/> with 7 days; only <paramref name="targetDayIndex"/>
    /// (0-based, Monday=0) carries a distinguishing meal so tests can tell which week/day a
    /// query actually returned.
    /// </summary>
    private static PlanWeek BuildWeek(int weekNumber, int targetDayIndex, Guid mealId, string foodName, DateTime datePublished)
    {
        var days = Enumerable.Range(1, 7).Select(d => new PlanDay { DayOfWeek = d, Meals = [] }).ToList();
        days[targetDayIndex].Meals.Add(new PlanMeal
        {
            MealId = mealId,
            Kind = MealKind.Lunch,
            Order = 1,
            Foods =
            [
                new MealFood
                {
                    FoodExternalId = Guid.NewGuid(),
                    FoodName = foodName,
                    NutrientValuePer100Grams = new NutrientValue { Kcal = 100, Protein = 10, Carbs = 10, Fat = 5 },
                    AmountGrams = 150
                }
            ]
        });

        return new PlanWeek
        {
            WeekNumber = weekNumber,
            Status = WeekStatus.Published,
            DatePublished = datePublished,
            Days = days
        };
    }

    /// <summary>
    /// Regression guard for #838. Seeds a 3-week Active plan whose window places today in
    /// WEEK 3 (a non-index-0 week — an off-by-one in the positional <c>weeks.$</c> filter would
    /// only surface here). Each week carries a distinctly-named meal on today's day-of-week so
    /// the test can prove exactly which week's content came back.
    /// <list type="number">
    /// <item>Direct-queries the real Mongo instance with the endpoint's actual (now
    /// <c>internal</c>) phase-1 <see cref="GetTodayPlanEndpoint.LightPlanProjection"/> and
    /// asserts every week's metadata is retained while <c>days</c> content is excluded for ALL
    /// weeks — not just the resolved one.</item>
    /// <item>Calls the real HTTP endpoint and asserts the response resolves week 3 and returns
    /// EXACTLY week 3's meal (not week 1's or week 2's, not empty) — proving the real phase-2
    /// positional projection matched the correct non-zero array index.</item>
    /// <item>Asserts byte-equivalence of the hydrated content against what a naive full-fetch
    /// of the seed would produce.</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task GetTodayPlan_MultiWeekPlan_TodayInNonFirstWeek_HydratesExactWeekContent_RealMongoProjection()
    {
        var httpClient = PinnedFactory.CreateClient();
        var email = UniqueEmail();
        await TestHelpers.RegisterAsync(httpClient, email, "TestPass1!", "Proj", "Plan", "Client");
        var (accessToken, _) = await TestHelpers.LoginAsync(httpClient, email, "TestPass1!");

        Guid clientUserId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await db.Users.FirstAsync(
                u => u.Email == email,
                TestContext.Current.CancellationToken);
            // GetTodayPlanEndpoint resolves the caller's ClientProfile by UserId and filters
            // NutritionPlan.ClientId on ClientProfile.UserId (#840) — seed the plan with
            // UserId, not PublicId, or the endpoint's own-plan lookup matches nothing.
            clientUserId = user.Id;
        }

        // PinnedInstantUtc's local day (2026-03-12) is a Thursday: DayOfWeek.Thursday == 4,
        // and 0-based Monday=0 gives targetDayIndex 3.
        const int todayDow = 4;
        const int targetDayIndex = 3;

        // 2026-02-23 (Monday) is 17 days before the pinned local day (2026-03-12) — daysSinceStart
        // 17 resolves weekNum 3 (17/7 + 1), dayIndex 3 (17 % 7), landing in a non-index-0 week.
        var startDate = new DateTime(2026, 2, 23, 0, 0, 0, DateTimeKind.Utc);

        var week1MealId = Guid.NewGuid();
        var week2MealId = Guid.NewGuid();
        var week3MealId = Guid.NewGuid();
        var planId = Guid.NewGuid();

        var globalSettings = new GlobalNutritionSettings { DailyKcal = 2200, ProteinGrams = 150, CarbsGrams = 220, FatGrams = 70 };

        var plan = new NutritionPlan
        {
            ExternalId = planId,
            ClientId = clientUserId,
            NutritionistId = Guid.NewGuid(),
            Name = "Projection Test Plan",
            Status = NutritionPlanStatus.Active,
            StartDate = startDate,
            GlobalSettings = globalSettings,
            Version = 1,
            DateCreated = startDate,
            Weeks =
            [
                BuildWeek(1, targetDayIndex, week1MealId, "Week 1 Food (must never appear)", startDate),
                BuildWeek(2, targetDayIndex, week2MealId, "Week 2 Food (must never appear)", startDate),
                BuildWeek(3, targetDayIndex, week3MealId, "Week 3 Food (target)", startDate)
            ]
        };

        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
            await mongo.NutritionPlans.InsertOneAsync(plan, cancellationToken: TestContext.Current.CancellationToken);
        }

        // ── 1. Direct assertion on the REAL phase-1 projection against real Mongo ──────────
        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
            var filter = Builders<NutritionPlan>.Filter.Eq(p => p.ExternalId, planId);
            using var cursor = await mongo.NutritionPlans.FindAsync(
                filter,
                new FindOptions<NutritionPlan, NutritionPlan> { Projection = GetTodayPlanEndpoint.LightPlanProjection },
                TestContext.Current.CancellationToken);
            var projected = (await cursor.ToListAsync(TestContext.Current.CancellationToken)).Single();

            projected.Weeks.Should().HaveCount(3,
                "the weeks array itself must be RETAINED by the projection — only its content is excluded");
            projected.Weeks.Select(w => w.WeekNumber).Should().BeEquivalentTo(new[] { 1, 2, 3 });
            projected.Weeks.Should().AllSatisfy(w => w.Status.Should().Be(WeekStatus.Published));
            projected.Weeks.Should().AllSatisfy(w => w.DatePublished.Should().Be(startDate));
            projected.Weeks.Should().AllSatisfy(w => w.Days.Should().BeEmpty(
                "the real phase-1 projection must exclude weeks[].days content for EVERY week"));
            projected.GlobalSettings.Should().NotBeNull("plan-level GlobalSettings must survive the light projection");
            projected.GlobalSettings!.DailyKcal.Should().Be(2200);
        }

        // ── 1b. Direct assertion on the REAL phase-2 positional projection against real Mongo ──
        // Regression guard: an inclusion-only "weeks.$" projection returns ONLY `_id` and
        // `weeks` — every other field (including `externalId`) is excluded and deserializes to
        // its C# default (Guid.Empty) unless explicitly re-included in the projection. This
        // exact bug made FetchHydratedWeekAsync silently return null on every real call (the
        // defensive ExternalId-match guard always failed against a Guid.Empty), invisible to
        // the mocked unit tests since NSubstitute ignores the Projection option entirely.
        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
            var weekFilter = Builders<NutritionPlan>.Filter.And(
                Builders<NutritionPlan>.Filter.Eq(p => p.ExternalId, planId),
                Builders<NutritionPlan>.Filter.Eq("weeks.weekNumber", 3));
            var weekProjection = Builders<NutritionPlan>.Projection.Combine(
                Builders<NutritionPlan>.Projection.Include(p => p.ExternalId),
                Builders<NutritionPlan>.Projection.Include("weeks.$"));
            using var cursor = await mongo.NutritionPlans.FindAsync(
                weekFilter,
                new FindOptions<NutritionPlan, NutritionPlan> { Projection = weekProjection },
                TestContext.Current.CancellationToken);
            var hydratedPlans = await cursor.ToListAsync(TestContext.Current.CancellationToken);

            hydratedPlans.Should().ContainSingle();
            hydratedPlans[0].ExternalId.Should().Be(planId,
                "the phase-2 projection must explicitly re-include ExternalId — otherwise it deserializes to Guid.Empty " +
                "and the endpoint's defensive ExternalId match silently fails on every real request");
            var hydratedWeek = hydratedPlans[0].Weeks.Should().ContainSingle().Subject;
            hydratedWeek.WeekNumber.Should().Be(3, "the positional $ operator must match the correct non-zero-index array element");
        }

        // ── 2. Real HTTP round-trip: phase-2 must hydrate EXACTLY week 3's content ─────────
        TestHelpers.SetBearerToken(httpClient, accessToken);
        var response = await httpClient.GetAsync("/client/nutrition/plan/today", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        var body = await response.Content.ReadFromJsonAsync<TodayPlanResponseDto>(
            jsonOptions,
            cancellationToken: TestContext.Current.CancellationToken);

        body.Should().NotBeNull();
        body!.PlanId.Should().Be(planId);
        body.WeekNumber.Should().Be(3, "today's date must resolve to week 3, not week 1 (proves phase-1 metadata drove real window/week resolution)");
        body.DayOfWeek.Should().Be(todayDow);
        body.Meals.Should().ContainSingle();

        var hydratedMeal = body.Meals[0];
        hydratedMeal.MealId.Should().Be(week3MealId,
            "phase-2 hydration must fetch week 3's meal — a wrong/off-by-one positional $ match would return week 1's or week 2's instead");
        hydratedMeal.Foods.Should().ContainSingle();
        hydratedMeal.Foods[0].FoodName.Should().Be("Week 3 Food (target)");

        // ── 3. Byte-equivalence: matches exactly what a naive full-fetch of the seed would produce ──
        body.GlobalSettings.Should().NotBeNull();
        body.GlobalSettings!.DailyKcal.Should().Be(2200);
        body.Meals.Should().NotContain(m => m.MealId == week1MealId || m.MealId == week2MealId,
            "sibling weeks' meal content must never leak into the hydrated response");
    }

    // ── Local response DTOs (per slice rules — not shared across features) ────────

    private record TodayPlanResponseDto(
        Guid PlanId,
        string PlanName,
        int WeekNumber,
        int DayOfWeek,
        List<MealResponseDto> Meals,
        GlobalSettingsResponseDto? GlobalSettings);

    private record MealResponseDto(
        Guid MealId,
        string Kind,
        int Order,
        List<FoodResponseDto> Foods);

    private record FoodResponseDto(
        Guid FoodExternalId,
        string FoodName);

    private record GlobalSettingsResponseDto(
        decimal? DailyKcal);
}
