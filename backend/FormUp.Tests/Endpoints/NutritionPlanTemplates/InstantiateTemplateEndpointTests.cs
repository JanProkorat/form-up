using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Entities;
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
/// Testcontainers integration tests for <c>POST /nutrition/plan-templates/{templateId}/instantiate</c>
/// (#861) — the risk-centre endpoint of this issue: fresh <c>MealId</c> minting, the
/// coach-client-link 404 (never 403), replication of the plan-creation start-date/overlap rules,
/// and the Goal/DietaryStyle/TargetWeightKg field-mapping contract.
/// </summary>
[Collection(TestCollection.Name)]
public class InstantiateTemplateEndpointTests(FitnessApiFactory factory)
{
    private static string UniqueEmail(string tag) => $"{Guid.NewGuid():N}@instantiate-{tag}.com";

    private async Task<(HttpClient Client, Guid UserId)> RegisterNutritionistAsync(string tag)
    {
        var client = factory.CreateClient();
        var email = UniqueEmail(tag);
        await TestHelpers.RegisterAsync(client, email, "TestPass1!", "Test", "Nutritionist", "Nutritionist");
        var (token, _) = await TestHelpers.LoginAsync(client, email, "TestPass1!");
        TestHelpers.SetBearerToken(client, token);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await db.Users.FirstAsync(u => u.Email == email, TestContext.Current.CancellationToken);

        return (client, user.Id);
    }

    private async Task<(Guid ClientPublicId, long ClientProfileId, Guid ClientUserId)> RegisterClientAsync(string tag)
    {
        var client = factory.CreateClient();
        var email = UniqueEmail(tag);
        await TestHelpers.RegisterAsync(client, email, "TestPass1!", "Test", "Client", "Client");
        await TestHelpers.LoginAsync(client, email, "TestPass1!");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await db.Users.FirstAsync(u => u.Email == email, TestContext.Current.CancellationToken);
        var profile = await db.ClientProfiles.FirstAsync(
            cp => cp.UserId == user.Id, TestContext.Current.CancellationToken);

        return (profile.PublicId, profile.Id, user.Id);
    }

    private async Task<long> GetProfessionalProfileIdAsync(Guid nutritionistUserId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var profile = await db.ProfessionalProfiles.FirstAsync(
            p => p.UserId == nutritionistUserId, TestContext.Current.CancellationToken);
        return profile.Id;
    }

    private Task LinkAsync(long nutritionistProfileId, long clientProfileId) =>
        LinkAsync(nutritionistProfileId, clientProfileId, canViewTrainingPlans: true, canViewNutritionPlans: true);

    private async Task LinkAsync(
        long nutritionistProfileId, long clientProfileId, bool canViewTrainingPlans, bool canViewNutritionPlans)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.ClientProfessionalLinks.Add(new ClientProfessionalLink
        {
            PublicId = Guid.NewGuid(),
            ProfessionalProfileId = nutritionistProfileId,
            ClientProfileId = clientProfileId,
            ProfessionalRole = UserRole.Nutritionist,
            ConnectionState = ConnectionState.Connected,
            CanViewTrainingPlans = canViewTrainingPlans,
            CanViewNutritionPlans = canViewNutritionPlans,
            DateCreated = DateTime.UtcNow
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task SeedTemplateAsync(NutritionPlanTemplate template)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        await mongo.NutritionPlanTemplates.InsertOneAsync(
            template, cancellationToken: TestContext.Current.CancellationToken);
    }

    private async Task<NutritionPlan> FetchPlanAsync(Guid externalId)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        return await mongo.NutritionPlans
            .Find(p => p.ExternalId == externalId)
            .FirstAsync(TestContext.Current.CancellationToken);
    }

    private static NutritionPlanTemplate BuildTemplateWithMeals(
        Guid ownerId, int weekCount = 1, PrimaryGoal? goal = null, DietaryStyle? dietaryStyle = null)
    {
        var template = new NutritionPlanTemplate
        {
            ExternalId = Guid.NewGuid(),
            OwnerId = ownerId,
            Name = "Source Template",
            Goal = goal,
            DietaryStyle = dietaryStyle,
            Visibility = LibraryVisibility.Private,
            Version = 1,
            DateCreated = DateTime.UtcNow,
            Supplements = [new Supplement { ExternalId = Guid.NewGuid(), Name = "Vitamin D3" }],
            Weeks = Enumerable.Range(1, weekCount).Select(weekNumber => new TemplateWeek
            {
                WeekNumber = weekNumber,
                Days = Enumerable.Range(1, 7).Select(dayOfWeek => new PlanDay
                {
                    DayOfWeek = dayOfWeek,
                    Meals = dayOfWeek == 1
                        ? [new PlanMeal { MealId = Guid.NewGuid(), Kind = MealKind.Breakfast, Order = 1 }]
                        : []
                }).ToList()
            }).ToList(),
            WeekCount = weekCount
        };

        return template;
    }

    // ── the risk-centre AC: fresh MealId minting ─────────────────────────────

    [Fact]
    public async Task Instantiate_ValidRequest_CreatesDraftPlanWithFreshMealIdsAndSupplementIds()
    {
        var (nutritionist, nutritionistId) = await RegisterNutritionistAsync("meal-id");
        var (clientPublicId, clientProfileId, clientUserId) = await RegisterClientAsync("meal-id");
        var professionalProfileId = await GetProfessionalProfileIdAsync(nutritionistId);
        await LinkAsync(professionalProfileId, clientProfileId);

        var template = BuildTemplateWithMeals(nutritionistId, weekCount: 2);
        await SeedTemplateAsync(template);

        var templateMealIds = template.Weeks
            .SelectMany(w => w.Days)
            .SelectMany(d => d.Meals)
            .Select(m => m.MealId)
            .ToHashSet();
        var templateSupplementIds = template.Supplements.Select(s => s.ExternalId).ToHashSet();

        var response = await nutritionist.PostAsJsonAsync(
            $"/nutrition/plan-templates/{template.ExternalId}/instantiate",
            new { ClientId = clientPublicId, Name = "Instantiated Plan" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<InstantiateResponseDto>(
            cancellationToken: TestContext.Current.CancellationToken);
        body.Should().NotBeNull();
        body!.Status.Should().Be("Draft");

        var plan = await FetchPlanAsync(body.PlanId);
        plan.ClientId.Should().Be(clientUserId);
        plan.Status.Should().Be(NutritionPlanStatus.Draft);
        plan.SourceTemplateId.Should().Be(template.ExternalId);
        plan.Weeks.Should().OnlyContain(w => w.Status == WeekStatus.Draft);

        var planMealIds = plan.Weeks.SelectMany(w => w.Days).SelectMany(d => d.Meals).Select(m => m.MealId).ToList();
        planMealIds.Should().NotBeEmpty();
        planMealIds.Should().OnlyContain(id => !templateMealIds.Contains(id),
            "no MealId in the instantiated plan may appear anywhere in the source template");

        var planSupplementIds = plan.Supplements.Select(s => s.ExternalId).ToList();
        planSupplementIds.Should().OnlyContain(id => !templateSupplementIds.Contains(id),
            "instantiate mints a fresh Supplement.ExternalId for every supplement it copies");
    }

    [Fact]
    public async Task Instantiate_MealItems_GetFreshItemIds()
    {
        var (nutritionist, nutritionistId) = await RegisterNutritionistAsync("item-id");
        var (clientPublicId, clientProfileId, _) = await RegisterClientAsync("item-id");
        await LinkAsync(await GetProfessionalProfileIdAsync(nutritionistId), clientProfileId);

        var template = BuildTemplateWithMeals(nutritionistId);
        var templateMeal = template.Weeks[0].Days[0].Meals[0];
        templateMeal.Foods = [new MealFood { ItemId = Guid.NewGuid(), FoodExternalId = Guid.NewGuid(), FoodName = "Rice" }];
        templateMeal.Recipes = [new MealRecipe { ItemId = Guid.NewGuid(), RecipeId = Guid.NewGuid(), RecipeName = "Bowl" }];
        await SeedTemplateAsync(template);

        var response = await nutritionist.PostAsJsonAsync(
            $"/nutrition/plan-templates/{template.ExternalId}/instantiate",
            new { ClientId = clientPublicId, Name = "Item Ids Plan" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<InstantiateResponseDto>(
            cancellationToken: TestContext.Current.CancellationToken);
        var planMeal = (await FetchPlanAsync(body!.PlanId)).Weeks[0].Days[0].Meals[0];
        planMeal.Foods[0].ItemId.Should().NotBe(Guid.Empty).And.NotBe(templateMeal.Foods[0].ItemId);
        planMeal.Recipes[0].ItemId.Should().NotBe(Guid.Empty).And.NotBe(templateMeal.Recipes[0].ItemId);
    }

    [Fact]
    public async Task Instantiate_SameTemplateForDifferentClients_ProducesIndependentPlansWithDistinctMealIds()
    {
        var (nutritionist, nutritionistId) = await RegisterNutritionistAsync("independent");
        var (clientAPublicId, clientAProfileId, _) = await RegisterClientAsync("independent-a");
        var (clientBPublicId, clientBProfileId, _) = await RegisterClientAsync("independent-b");
        var professionalProfileId = await GetProfessionalProfileIdAsync(nutritionistId);
        await LinkAsync(professionalProfileId, clientAProfileId);
        await LinkAsync(professionalProfileId, clientBProfileId);

        var template = BuildTemplateWithMeals(nutritionistId);
        await SeedTemplateAsync(template);

        var responseA = await nutritionist.PostAsJsonAsync(
            $"/nutrition/plan-templates/{template.ExternalId}/instantiate",
            new { ClientId = clientAPublicId, Name = "Plan A" });
        var responseB = await nutritionist.PostAsJsonAsync(
            $"/nutrition/plan-templates/{template.ExternalId}/instantiate",
            new { ClientId = clientBPublicId, Name = "Plan B" });

        responseA.StatusCode.Should().Be(HttpStatusCode.Created);
        responseB.StatusCode.Should().Be(HttpStatusCode.Created);

        var bodyA = await responseA.Content.ReadFromJsonAsync<InstantiateResponseDto>(
            cancellationToken: TestContext.Current.CancellationToken);
        var bodyB = await responseB.Content.ReadFromJsonAsync<InstantiateResponseDto>(
            cancellationToken: TestContext.Current.CancellationToken);

        var planA = await FetchPlanAsync(bodyA!.PlanId);
        var planB = await FetchPlanAsync(bodyB!.PlanId);

        var mealIdsA = planA.Weeks.SelectMany(w => w.Days).SelectMany(d => d.Meals).Select(m => m.MealId).ToHashSet();
        var mealIdsB = planB.Weeks.SelectMany(w => w.Days).SelectMany(d => d.Meals).Select(m => m.MealId).ToHashSet();

        mealIdsA.Should().NotIntersectWith(mealIdsB, "two independent instantiations must never share a MealId");
    }

    // ── coach-client link ─────────────────────────────────────────────────────

    [Fact]
    public async Task Instantiate_UnlinkedClient_Returns404()
    {
        var (nutritionist, nutritionistId) = await RegisterNutritionistAsync("unlinked");
        var (clientPublicId, _, _) = await RegisterClientAsync("unlinked");
        // Deliberately no link created.

        var template = BuildTemplateWithMeals(nutritionistId);
        await SeedTemplateAsync(template);

        var response = await nutritionist.PostAsJsonAsync(
            $"/nutrition/plan-templates/{template.ExternalId}/instantiate",
            new { ClientId = clientPublicId, Name = "Plan" });

        response.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "an unlinked client must 404, never 403 — a 403 would confirm the client exists to an unlinked coach");
    }

    /// <summary>
    /// Mirror-site regression guard: this is a nutrition route and must require
    /// <c>CanViewNutritionPlans</c> specifically. An active link that grants only the training
    /// domain must still be denied — if the guard were ever widened to a bare "link exists"
    /// check, this test would regress to 201.
    /// </summary>
    [Fact]
    public async Task Instantiate_LinkGrantsOnlyTraining_Returns404()
    {
        var (nutritionist, nutritionistId) = await RegisterNutritionistAsync("training-only");
        var (clientPublicId, clientProfileId, _) = await RegisterClientAsync("training-only");
        var professionalProfileId = await GetProfessionalProfileIdAsync(nutritionistId);
        await LinkAsync(professionalProfileId, clientProfileId, canViewTrainingPlans: true, canViewNutritionPlans: false);

        var template = BuildTemplateWithMeals(nutritionistId);
        await SeedTemplateAsync(template);

        var response = await nutritionist.PostAsJsonAsync(
            $"/nutrition/plan-templates/{template.ExternalId}/instantiate",
            new { ClientId = clientPublicId, Name = "Plan" });

        response.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "a link that grants only the training domain must not admit a nutrition-plan instantiation");
    }

    // ── template guard: read-guarded, not write-guarded ──────────────────────

    [Fact]
    public async Task Instantiate_OtherOwnersPublicTemplate_Returns201()
    {
        var otherOwnerId = Guid.NewGuid();
        var (nutritionist, nutritionistId) = await RegisterNutritionistAsync("public-instantiate");
        var (clientPublicId, clientProfileId, _) = await RegisterClientAsync("public-instantiate");
        var professionalProfileId = await GetProfessionalProfileIdAsync(nutritionistId);
        await LinkAsync(professionalProfileId, clientProfileId);

        var template = BuildTemplateWithMeals(otherOwnerId);
        template.Visibility = LibraryVisibility.Public;
        await SeedTemplateAsync(template);

        var response = await nutritionist.PostAsJsonAsync(
            $"/nutrition/plan-templates/{template.ExternalId}/instantiate",
            new { ClientId = clientPublicId, Name = "Plan From Public Template" });

        response.StatusCode.Should().Be(
            HttpStatusCode.Created,
            "instantiate is read-guarded — another owner's Public template must stay instantiable");
    }

    [Fact]
    public async Task Instantiate_OtherOwnersPrivateTemplate_Returns404()
    {
        var otherOwnerId = Guid.NewGuid();
        var (nutritionist, nutritionistId) = await RegisterNutritionistAsync("private-instantiate");
        var (clientPublicId, clientProfileId, _) = await RegisterClientAsync("private-instantiate");
        var professionalProfileId = await GetProfessionalProfileIdAsync(nutritionistId);
        await LinkAsync(professionalProfileId, clientProfileId);

        var template = BuildTemplateWithMeals(otherOwnerId);
        await SeedTemplateAsync(template);

        var response = await nutritionist.PostAsJsonAsync(
            $"/nutrition/plan-templates/{template.ExternalId}/instantiate",
            new { ClientId = clientPublicId, Name = "Plan" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── overlap + field mapping ───────────────────────────────────────────────

    /// <summary>
    /// The start date must be the next Monday, not <c>DateTime.UtcNow.Date</c>. The instantiate
    /// validator enforces <c>START_DATE_NOT_MONDAY</c> (mirroring <c>CreatePlanValidator</c>), so
    /// a non-Monday start is rejected with a 400 *before* the endpoint's overlap check runs —
    /// which made this assertion pass only when the suite happened to execute on a Monday and
    /// fail on the other six days. A date-dependent test that goes green once a week is worse
    /// than one that fails consistently.
    /// </summary>
    [Fact]
    public async Task Instantiate_OverlappingWindow_Returns409PlanOverlap()
    {
        var today = DateTime.UtcNow.Date;
        var daysUntilMonday = ((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7;
        var nextMonday = today.AddDays(daysUntilMonday == 0 ? 7 : daysUntilMonday);

        var (nutritionist, nutritionistId) = await RegisterNutritionistAsync("overlap");
        var (clientPublicId, clientProfileId, clientUserId) = await RegisterClientAsync("overlap");
        var professionalProfileId = await GetProfessionalProfileIdAsync(nutritionistId);
        await LinkAsync(professionalProfileId, clientProfileId);

        var template = BuildTemplateWithMeals(nutritionistId, weekCount: 4);
        await SeedTemplateAsync(template);

        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
            await mongo.NutritionPlans.InsertOneAsync(new NutritionPlan
            {
                ExternalId = Guid.NewGuid(),
                ClientId = clientUserId,
                NutritionistId = nutritionistId,
                Name = "Existing Plan",
                Status = NutritionPlanStatus.Active,
                StartDate = nextMonday,
                Version = 1,
                DateCreated = DateTime.UtcNow,
                Weeks = Enumerable.Range(1, 4).Select(w => new PlanWeek { WeekNumber = w }).ToList()
            }, cancellationToken: TestContext.Current.CancellationToken);
        }

        // Same start date and week count as the seeded plan, so the windows overlap exactly —
        // the overlap is the property under test, not an incidental near-miss.
        var response = await nutritionist.PostAsJsonAsync(
            $"/nutrition/plan-templates/{template.ExternalId}/instantiate",
            new { ClientId = clientPublicId, Name = "Overlapping Plan", StartDate = nextMonday });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var responseBody = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        responseBody.Should().Contain("PLAN_OVERLAP");
    }

    [Fact]
    public async Task Instantiate_FieldMapping_CopiesGoal_NoDietaryStyleOrTargetWeightOnPlan()
    {
        var (nutritionist, nutritionistId) = await RegisterNutritionistAsync("field-mapping");
        var (clientPublicId, clientProfileId, _) = await RegisterClientAsync("field-mapping");
        var professionalProfileId = await GetProfessionalProfileIdAsync(nutritionistId);
        await LinkAsync(professionalProfileId, clientProfileId);

        var template = BuildTemplateWithMeals(
            nutritionistId, goal: PrimaryGoal.GainMuscle, dietaryStyle: DietaryStyle.Vegan);
        await SeedTemplateAsync(template);

        var response = await nutritionist.PostAsJsonAsync(
            $"/nutrition/plan-templates/{template.ExternalId}/instantiate",
            new { ClientId = clientPublicId, Name = "Mapped Plan" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<InstantiateResponseDto>(
            cancellationToken: TestContext.Current.CancellationToken);

        var plan = await FetchPlanAsync(body!.PlanId);
        plan.Goal.Should().Be(PrimaryGoal.GainMuscle, "Goal copies through from the template");
        plan.TargetWeightKg.Should().BeNull("TargetWeightKg is client-only and not set by instantiate");
    }

    private sealed class InstantiateResponseDto
    {
        public Guid PlanId { get; set; }
        public string Status { get; set; } = string.Empty;
    }
}
