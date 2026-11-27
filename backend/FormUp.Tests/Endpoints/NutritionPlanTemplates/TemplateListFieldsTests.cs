using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace FormUp.Tests.Endpoints.NutritionPlanTemplates;

/// <summary>
/// Real-DB integration tests for the template list fields (<c>mealsPerDay</c>, <c>avgKcalPerDay</c>,
/// <c>usedBy</c>) and the <c>mealsPerDay</c> / <c>inUse</c> search filters. Every search narrows to
/// the test's own unique name, and asserts <c>TotalCount</c>, because the collection is shared.
/// </summary>
[Collection(TestCollection.Shard4)]
public class TemplateListFieldsTests(FitnessApiFactory factory)
{
    // Day shapes used throughout: three days with meals (3x200, 3x300, 2x100 kcal) and one empty
    // day. Most common meal count = 3; average = (600 + 900 + 200) / 3 = 566.67 -> 567.
    private static readonly (int Day, int Meals, decimal KcalPerMeal)[] DaySpecs =
    [
        (1, 3, 200m), (2, 3, 300m), (3, 2, 100m), (4, 0, 0m)
    ];

    private static string UniqueName() => $"tlf-{Guid.NewGuid():N}";

    private async Task<(HttpClient Client, Guid UserId)> RegisterNutritionistAsync(string tag)
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@template-list-{tag}.com";
        await TestHelpers.RegisterAsync(client, email, "TestPass1!", "Test", "Nutritionist", "Nutritionist");
        var (token, _) = await TestHelpers.LoginAsync(client, email, "TestPass1!");
        TestHelpers.SetBearerToken(client, token);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await db.Users.FirstAsync(u => u.Email == email, TestContext.Current.CancellationToken);

        return (client, user.Id);
    }

    private static List<PlanMeal> BuildMeals(int count, decimal kcalPerMeal) =>
        Enumerable.Range(1, count).Select(order => new PlanMeal
        {
            MealId = Guid.NewGuid(),
            Kind = (MealKind)Math.Min(order - 1, 2),
            Order = order,
            Foods =
            [
                new MealFood
                {
                    FoodExternalId = Guid.NewGuid(),
                    FoodName = "Test food",
                    AmountGrams = 100m,
                    NutrientValuePer100Grams = new NutrientValue { Kcal = kcalPerMeal }
                }
            ]
        }).ToList();

    private static List<PlanDay> BuildDays() =>
        DaySpecs.Select(spec => new PlanDay
        {
            DayOfWeek = spec.Day,
            Meals = BuildMeals(spec.Meals, spec.KcalPerMeal)
        }).ToList();

    private static object[] BuildRequestWeeks() =>
    [
        new
        {
            WeekNumber = 1,
            Days = DaySpecs.Select(spec => new
            {
                DayOfWeek = spec.Day,
                Meals = Enumerable.Range(1, spec.Meals).Select(order => new
                {
                    Kind = order == 1 ? "Breakfast" : order == 2 ? "Lunch" : "Dinner",
                    Order = order,
                    Foods = new[]
                    {
                        new
                        {
                            FoodExternalId = Guid.NewGuid(),
                            FoodName = "Test food",
                            AmountGrams = 100m,
                            NutrientValuePer100Grams = new { Kcal = spec.KcalPerMeal, Protein = 0m, Carbs = 0m, Fat = 0m }
                        }
                    }
                }).ToArray()
            }).ToArray()
        }
    ];

    private async Task<NutritionPlanTemplate> SeedTemplateAsync(
        Guid ownerId,
        string name,
        LibraryVisibility visibility = LibraryVisibility.Private,
        List<PlanDay>? days = null,
        int? mealsPerDay = null)
    {
        var template = new NutritionPlanTemplate
        {
            ExternalId = Guid.NewGuid(),
            OwnerId = ownerId,
            Name = name,
            Visibility = visibility,
            Version = 1,
            DateCreated = DateTime.UtcNow,
            Weeks = [new TemplateWeek { WeekNumber = 1, Days = days ?? BuildDays() }],
            WeekCount = 1,
            MealsPerDay = mealsPerDay
        };

        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        await mongo.NutritionPlanTemplates.InsertOneAsync(
            template, cancellationToken: TestContext.Current.CancellationToken);

        return template;
    }

    private async Task SeedPlanAsync(
        Guid nutritionistId, Guid? sourceTemplateId, NutritionPlanStatus status)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        await mongo.NutritionPlans.InsertOneAsync(
            new NutritionPlan
            {
                ExternalId = Guid.NewGuid(),
                ClientId = Guid.NewGuid(),
                NutritionistId = nutritionistId,
                Name = "Plan",
                Status = status,
                SourceTemplateId = sourceTemplateId,
                Version = 1,
                DateCreated = DateTime.UtcNow
            },
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private async Task<NutritionPlanTemplate> FetchTemplateAsync(Guid externalId)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        return await mongo.NutritionPlanTemplates
            .Find(t => t.ExternalId == externalId)
            .FirstAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<SearchDto> SearchAsync(HttpClient client, string name, string extraQuery = "")
    {
        var response = await client.GetAsync($"/nutrition/plan-templates?search={name}&pageSize=100{extraQuery}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<SearchDto>(
            cancellationToken: TestContext.Current.CancellationToken);
        return body!;
    }

    // ── write paths ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_WithMeals_ComputesMealsPerDayAndAvgKcal()
    {
        var (nutritionist, _) = await RegisterNutritionistAsync("create");

        var response = await nutritionist.PostAsJsonAsync(
            "/nutrition/plan-templates", new { Name = UniqueName(), Weeks = BuildRequestWeeks() });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<SummaryDto>(
            cancellationToken: TestContext.Current.CancellationToken);
        body!.MealsPerDay.Should().Be(3);
        body.AvgKcalPerDay.Should().Be(567m);

        var persisted = await FetchTemplateAsync(body.TemplateId);
        persisted.MealsPerDay.Should().Be(3);
        persisted.AvgKcalPerDay.Should().Be(567m);
    }

    [Fact]
    public async Task Update_ReplacesMeals_RecomputesStats()
    {
        var (nutritionist, nutritionistId) = await RegisterNutritionistAsync("update");
        var template = await SeedTemplateAsync(
            nutritionistId, UniqueName(), days: [new PlanDay { DayOfWeek = 1, Meals = BuildMeals(1, 50m) }], mealsPerDay: 1);

        var response = await nutritionist.PutAsJsonAsync(
            $"/nutrition/plan-templates/{template.ExternalId}",
            new { Name = template.Name, Version = template.Version, Weeks = BuildRequestWeeks() });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var persisted = await FetchTemplateAsync(template.ExternalId);
        persisted.MealsPerDay.Should().Be(3);
        persisted.AvgKcalPerDay.Should().Be(567m);
    }

    [Fact]
    public async Task Copy_SourceWithoutStoredStats_ComputesStatsOnTheCopy()
    {
        var (nutritionist, nutritionistId) = await RegisterNutritionistAsync("copy");
        var source = await SeedTemplateAsync(nutritionistId, UniqueName());

        var response = await nutritionist.PostAsync(
            $"/nutrition/plan-templates/{source.ExternalId}/copy", null, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<SummaryDto>(
            cancellationToken: TestContext.Current.CancellationToken);
        body!.MealsPerDay.Should().Be(3);
        body.AvgKcalPerDay.Should().Be(567m);
    }

    [Fact]
    public async Task FromPlan_StaleDayTotals_ComputesStatsFromMealsNotStoredTotals()
    {
        var (nutritionist, nutritionistId) = await RegisterNutritionistAsync("from-plan");
        var clientUserId = await TestHelpers.RegisterLinkedClientAsync(
            factory, nutritionistId, TestContext.Current.CancellationToken);

        var days = BuildDays();
        foreach (var day in days)
        {
            day.DayTotals = new NutrientTotals { Kcal = 9999m };
            foreach (var meal in day.Meals)
            {
                meal.MealTotals = new NutrientTotals { Kcal = 9999m };
            }
        }

        var plan = new NutritionPlan
        {
            ExternalId = Guid.NewGuid(),
            ClientId = clientUserId,
            NutritionistId = nutritionistId,
            Name = "Stale plan",
            Status = NutritionPlanStatus.Active,
            Version = 1,
            DateCreated = DateTime.UtcNow,
            Weeks = [new PlanWeek { WeekNumber = 1, Status = WeekStatus.Published, Days = days }]
        };

        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
            await mongo.NutritionPlans.InsertOneAsync(plan, cancellationToken: TestContext.Current.CancellationToken);
        }

        var response = await nutritionist.PostAsJsonAsync(
            "/nutrition/plan-templates/from-plan", new { PlanId = plan.ExternalId, Name = UniqueName() });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<SummaryDto>(
            cancellationToken: TestContext.Current.CancellationToken);
        body!.MealsPerDay.Should().Be(3);
        body.AvgKcalPerDay.Should().Be(567m, "kcal is recomputed from foods, not read from the stale stored totals");
    }

    // ── mealsPerDay filter + stats edge cases ────────────────────────────────

    [Fact]
    public async Task Search_MealsPerDayFilter_ReturnsOnlyMatchingAndCountsThem()
    {
        var (nutritionist, nutritionistId) = await RegisterNutritionistAsync("filter-meals");
        var name = UniqueName();
        var threeMeals = await SeedTemplateAsync(nutritionistId, name, mealsPerDay: 3);
        var twoMeals = await SeedTemplateAsync(
            nutritionistId, name, days: [new PlanDay { DayOfWeek = 1, Meals = BuildMeals(2, 100m) }], mealsPerDay: 2);

        var matching = await SearchAsync(nutritionist, name, "&mealsPerDay=3");
        matching.TotalCount.Should().Be(1);
        matching.Templates.Should().ContainSingle().Which.TemplateId.Should().Be(threeMeals.ExternalId);

        var unfiltered = await SearchAsync(nutritionist, name);
        unfiltered.TotalCount.Should().Be(2);
        unfiltered.Templates.Select(t => t.TemplateId).Should().BeEquivalentTo([threeMeals.ExternalId, twoMeals.ExternalId]);
    }

    [Fact]
    public async Task Search_TemplateWithNoNonEmptyDay_HasNullStatsAndMatchesNoMealsPerDayFilter()
    {
        var (nutritionist, nutritionistId) = await RegisterNutritionistAsync("empty-days");
        var name = UniqueName();
        var template = await SeedTemplateAsync(
            nutritionistId, name, days: [new PlanDay { DayOfWeek = 1, Meals = [] }]);

        var unfiltered = await SearchAsync(nutritionist, name);
        var row = unfiltered.Templates.Should().ContainSingle().Subject;
        row.TemplateId.Should().Be(template.ExternalId);
        row.MealsPerDay.Should().BeNull();
        row.AvgKcalPerDay.Should().BeNull();

        var filtered = await SearchAsync(nutritionist, name, "&mealsPerDay=1");
        filtered.TotalCount.Should().Be(0);
    }

    private static object[] BuildRequestWeeksWithEmptyMealRows(params (int Day, int Meals, bool Filled)[] daySpecs) =>
    [
        new
        {
            WeekNumber = 1,
            Days = daySpecs.Select(spec => new
            {
                DayOfWeek = spec.Day,
                Meals = Enumerable.Range(1, spec.Meals).Select(order => new
                {
                    Kind = order == 1 ? "Breakfast" : order == 2 ? "Lunch" : "Dinner",
                    Order = order,
                    Foods = spec.Filled
                        ? new[]
                        {
                            new
                            {
                                FoodExternalId = Guid.NewGuid(),
                                FoodName = "Test food",
                                AmountGrams = 100m,
                                NutrientValuePer100Grams = new { Kcal = 200m, Protein = 0m, Carbs = 0m, Fat = 0m }
                            }
                        }
                        : []
                }).ToArray()
            }).ToArray()
        }
    ];

    [Fact]
    public async Task Create_DaysWithOnlyEmptyMealRows_AreLeftOutOfBothStats()
    {
        var (nutritionist, _) = await RegisterNutritionistAsync("create-empty-rows");

        var response = await nutritionist.PostAsJsonAsync(
            "/nutrition/plan-templates",
            new
            {
                Name = UniqueName(),
                Weeks = BuildRequestWeeksWithEmptyMealRows((1, 2, true), (2, 3, false), (3, 3, false))
            });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<SummaryDto>(
            cancellationToken: TestContext.Current.CancellationToken);
        body!.AvgKcalPerDay.Should().Be(400m, "only the filled day counts");
        body.MealsPerDay.Should().Be(2, "empty meal rows do not make a day count");
    }

    [Fact]
    public async Task Create_EveryDayHasOnlyEmptyMealRows_PersistsNullStats()
    {
        var (nutritionist, _) = await RegisterNutritionistAsync("create-all-empty-rows");

        var response = await nutritionist.PostAsJsonAsync(
            "/nutrition/plan-templates",
            new
            {
                Name = UniqueName(),
                Weeks = BuildRequestWeeksWithEmptyMealRows((1, 3, false), (2, 2, false))
            });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<SummaryDto>(
            cancellationToken: TestContext.Current.CancellationToken);
        body!.MealsPerDay.Should().BeNull();
        body.AvgKcalPerDay.Should().BeNull();
    }

    [Fact]
    public async Task Create_WithNoMeals_PersistsNullStats()
    {
        var (nutritionist, _) = await RegisterNutritionistAsync("create-empty");

        var response = await nutritionist.PostAsJsonAsync(
            "/nutrition/plan-templates", new { Name = UniqueName(), WeekCount = 2 });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<SummaryDto>(
            cancellationToken: TestContext.Current.CancellationToken);
        body!.MealsPerDay.Should().BeNull();
        body.AvgKcalPerDay.Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Search_MealsPerDayNotPositive_Returns400(int mealsPerDay)
    {
        var (nutritionist, _) = await RegisterNutritionistAsync("bad-meals");

        var response = await nutritionist.GetAsync(
            $"/nutrition/plan-templates?mealsPerDay={mealsPerDay}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── usedBy + inUse ───────────────────────────────────────────────────────

    [Fact]
    public async Task Search_UsedByAndInUse_CountOnlyCallersActivePlans()
    {
        var (nutritionist, nutritionistId) = await RegisterNutritionistAsync("used-by");
        var name = UniqueName();
        var used = await SeedTemplateAsync(nutritionistId, name);
        var unused = await SeedTemplateAsync(nutritionistId, name);

        await SeedPlanAsync(nutritionistId, used.ExternalId, NutritionPlanStatus.Active);
        await SeedPlanAsync(nutritionistId, used.ExternalId, NutritionPlanStatus.Active);

        var all = await SearchAsync(nutritionist, name);
        all.TotalCount.Should().Be(2);
        all.Templates.Single(t => t.TemplateId == used.ExternalId).UsedBy.Should().Be(2);
        all.Templates.Single(t => t.TemplateId == unused.ExternalId).UsedBy.Should().Be(0);

        var inUse = await SearchAsync(nutritionist, name, "&inUse=true");
        inUse.TotalCount.Should().Be(1);
        inUse.Templates.Should().ContainSingle().Which.TemplateId.Should().Be(used.ExternalId);

        var notInUse = await SearchAsync(nutritionist, name, "&inUse=false");
        notInUse.TotalCount.Should().Be(1);
        notInUse.Templates.Should().ContainSingle().Which.TemplateId.Should().Be(unused.ExternalId);
    }

    [Fact]
    public async Task Search_PublicTemplateUsedByAnotherNutritionist_ShowsZeroToCallerAndIsExcludedByInUse()
    {
        var (_, ownerId) = await RegisterNutritionistAsync("used-owner");
        var (caller, _) = await RegisterNutritionistAsync("used-caller");
        var name = UniqueName();
        var template = await SeedTemplateAsync(ownerId, name, LibraryVisibility.Public);

        await SeedPlanAsync(ownerId, template.ExternalId, NutritionPlanStatus.Active);
        await SeedPlanAsync(ownerId, template.ExternalId, NutritionPlanStatus.Active);

        var all = await SearchAsync(caller, name);
        all.TotalCount.Should().Be(1);
        all.Templates.Should().ContainSingle().Which.UsedBy.Should().Be(0, "the owner's client count must not leak");

        var inUse = await SearchAsync(caller, name, "&inUse=true");
        inUse.TotalCount.Should().Be(0);

        var notInUse = await SearchAsync(caller, name, "&inUse=false");
        notInUse.TotalCount.Should().Be(1);
    }

    [Theory]
    [InlineData(NutritionPlanStatus.Draft)]
    [InlineData(NutritionPlanStatus.Completed)]
    [InlineData(NutritionPlanStatus.Archived)]
    public async Task Search_PlansNotActive_AreNotCountedAsUsage(NutritionPlanStatus status)
    {
        var (nutritionist, nutritionistId) = await RegisterNutritionistAsync("not-active");
        var name = UniqueName();
        var template = await SeedTemplateAsync(nutritionistId, name);

        await SeedPlanAsync(nutritionistId, template.ExternalId, status);

        var all = await SearchAsync(nutritionist, name);
        all.Templates.Should().ContainSingle().Which.UsedBy.Should().Be(0);

        var inUse = await SearchAsync(nutritionist, name, "&inUse=true");
        inUse.TotalCount.Should().Be(0);

        var notInUse = await SearchAsync(nutritionist, name, "&inUse=false");
        notInUse.TotalCount.Should().Be(1);
    }

    // ── detail usedBy ────────────────────────────────────────────────────────

    private static async Task<DetailDto> GetDetailAsync(HttpClient client, Guid templateId)
    {
        var response = await client.GetAsync(
            $"/nutrition/plan-templates/{templateId}", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<DetailDto>(
            cancellationToken: TestContext.Current.CancellationToken);
        return body!;
    }

    [Fact]
    public async Task GetDetail_OwnerWithTwoActivePlans_ReturnsUsedByTwo()
    {
        var (nutritionist, nutritionistId) = await RegisterNutritionistAsync("detail-used");
        var template = await SeedTemplateAsync(nutritionistId, UniqueName());
        var otherTemplate = await SeedTemplateAsync(nutritionistId, UniqueName());

        await SeedPlanAsync(nutritionistId, template.ExternalId, NutritionPlanStatus.Active);
        await SeedPlanAsync(nutritionistId, template.ExternalId, NutritionPlanStatus.Active);
        await SeedPlanAsync(nutritionistId, otherTemplate.ExternalId, NutritionPlanStatus.Active);

        (await GetDetailAsync(nutritionist, template.ExternalId)).UsedBy.Should().Be(2);
        (await GetDetailAsync(nutritionist, otherTemplate.ExternalId)).UsedBy.Should().Be(1);
    }

    [Fact]
    public async Task GetDetail_DraftAndArchivedPlans_AreNotCounted()
    {
        var (nutritionist, nutritionistId) = await RegisterNutritionistAsync("detail-inactive");
        var template = await SeedTemplateAsync(nutritionistId, UniqueName());

        await SeedPlanAsync(nutritionistId, template.ExternalId, NutritionPlanStatus.Active);
        await SeedPlanAsync(nutritionistId, template.ExternalId, NutritionPlanStatus.Draft);
        await SeedPlanAsync(nutritionistId, template.ExternalId, NutritionPlanStatus.Archived);

        (await GetDetailAsync(nutritionist, template.ExternalId)).UsedBy.Should().Be(1);
    }

    [Fact]
    public async Task GetDetail_PublicTemplateOfAnotherNutritionist_CountsOnlyCallersOwnPlans()
    {
        var (_, ownerId) = await RegisterNutritionistAsync("detail-owner");
        var (caller, callerId) = await RegisterNutritionistAsync("detail-caller");
        var template = await SeedTemplateAsync(ownerId, UniqueName(), LibraryVisibility.Public);

        await SeedPlanAsync(ownerId, template.ExternalId, NutritionPlanStatus.Active);
        await SeedPlanAsync(ownerId, template.ExternalId, NutritionPlanStatus.Active);

        (await GetDetailAsync(caller, template.ExternalId)).UsedBy.Should().Be(0, "the owner's count must not leak");

        await SeedPlanAsync(callerId, template.ExternalId, NutritionPlanStatus.Active);

        (await GetDetailAsync(caller, template.ExternalId)).UsedBy.Should().Be(1);
    }

    [Fact]
    public async Task Update_Response_ReturnsSameUsedByAsGet()
    {
        var (nutritionist, nutritionistId) = await RegisterNutritionistAsync("update-used");
        var template = await SeedTemplateAsync(nutritionistId, UniqueName());

        await SeedPlanAsync(nutritionistId, template.ExternalId, NutritionPlanStatus.Active);
        await SeedPlanAsync(nutritionistId, template.ExternalId, NutritionPlanStatus.Active);

        var response = await nutritionist.PutAsJsonAsync(
            $"/nutrition/plan-templates/{template.ExternalId}",
            new { Name = template.Name, Version = template.Version, Weeks = BuildRequestWeeks() });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<DetailDto>(
            cancellationToken: TestContext.Current.CancellationToken);
        body!.UsedBy.Should().Be(2);
        (await GetDetailAsync(nutritionist, template.ExternalId)).UsedBy.Should().Be(body.UsedBy);
    }

    private sealed class DetailDto
    {
        public int UsedBy { get; set; }
    }

    private sealed class SummaryDto
    {
        public Guid TemplateId { get; set; }
        public int? MealsPerDay { get; set; }
        public decimal? AvgKcalPerDay { get; set; }
        public int UsedBy { get; set; }
    }

    private sealed class SearchDto
    {
        public List<SummaryDto> Templates { get; set; } = [];
        public long TotalCount { get; set; }
    }
}
