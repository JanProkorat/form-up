using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace FormUp.Tests.Endpoints.NutritionPlans;

/// <summary>
/// Testcontainers integration tests for <c>POST /nutrition/plans/{planId}/copy</c>: the
/// source-plan gate, the target-client link gate (always 404, never 403), the overlap check,
/// and the independence of the copy from its source.
/// </summary>
[Collection(TestCollection.Shard2)]
public class CopyNutritionPlanEndpointTests(FitnessApiFactory factory)
{
    private static DateTime NextMonday()
    {
        var today = DateTime.UtcNow.Date;
        var daysUntilMonday = ((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7;
        return today.AddDays(daysUntilMonday == 0 ? 7 : daysUntilMonday);
    }

    private async Task<Actor> LinkedClientAsync(Actor nutritionist, bool canViewNutritionPlans = true)
    {
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        await TestActors.Link(factory, nutritionist, client)
            .AsRole(UserRole.Nutritionist)
            .CanViewNutritionPlans(canViewNutritionPlans)
            .CreateAsync(TestContext.Current.CancellationToken);
        return client;
    }

    private async Task<NutritionPlan> SeedPlanAsync(
        Guid nutritionistId, Guid clientUserId, int weekCount = 2, DateTime? startDate = null)
    {
        var plan = new NutritionPlan
        {
            ExternalId = Guid.NewGuid(),
            ClientId = clientUserId,
            NutritionistId = nutritionistId,
            Name = "Source Plan",
            Status = NutritionPlanStatus.Active,
            Goal = PrimaryGoal.GainMuscle,
            TargetWeightKg = 72.5m,
            StartDate = startDate,
            Version = 4,
            DateCreated = DateTime.UtcNow.AddDays(-10),
            QuestionnaireResponseId = Guid.NewGuid(),
            Supplements = [new Supplement { ExternalId = Guid.NewGuid(), Name = "Vitamin D3" }],
            Weeks = Enumerable.Range(1, weekCount).Select(weekNumber => new PlanWeek
            {
                WeekNumber = weekNumber,
                Status = WeekStatus.Published,
                DatePublished = DateTime.UtcNow.AddDays(-5),
                Days = Enumerable.Range(1, 7).Select(dayOfWeek => new PlanDay
                {
                    DayOfWeek = dayOfWeek,
                    Meals = dayOfWeek == 1
                        ? [new PlanMeal { MealId = Guid.NewGuid(), Kind = MealKind.Breakfast, Order = 1 }]
                        : []
                }).ToList()
            }).ToList()
        };

        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        await mongo.NutritionPlans.InsertOneAsync(plan, cancellationToken: TestContext.Current.CancellationToken);
        return plan;
    }

    private async Task<NutritionPlan> FetchPlanAsync(Guid externalId)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        return await mongo.NutritionPlans
            .Find(p => p.ExternalId == externalId)
            .FirstAsync(TestContext.Current.CancellationToken);
    }

    private static List<Guid> MealIds(NutritionPlan plan) =>
        plan.Weeks.SelectMany(w => w.Days).SelectMany(d => d.Meals).Select(m => m.MealId).ToList();

    // ── success ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Copy_ToSameClient_CreatesDraftPlanWithResetWeeksAndFreshIds()
    {
        var nutritionist = await TestActors.Nutritionist(factory).CreateAsync(TestContext.Current.CancellationToken);
        var client = await LinkedClientAsync(nutritionist);
        var source = await SeedPlanAsync(nutritionist.UserId, client.UserId);

        var response = await nutritionist.Http.PostAsJsonAsync(
            $"/nutrition/plans/{source.ExternalId}/copy",
            new { ClientId = client.PublicId },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<CopyResponseDto>(
            cancellationToken: TestContext.Current.CancellationToken);
        body!.Status.Should().Be("Draft");
        body.ClientId.Should().Be(client.PublicId);
        body.Name.Should().Be("Source Plan", "the name defaults to the source plan's name");

        var copy = await FetchPlanAsync(body.PlanId);
        copy.ExternalId.Should().NotBe(source.ExternalId);
        copy.ClientId.Should().Be(client.UserId);
        copy.NutritionistId.Should().Be(nutritionist.UserId);
        copy.Status.Should().Be(NutritionPlanStatus.Draft);
        copy.Version.Should().Be(1);
        copy.StartDate.Should().BeNull();
        copy.QuestionnaireResponseId.Should().BeNull();
        copy.Goal.Should().Be(PrimaryGoal.GainMuscle);
        copy.TargetWeightKg.Should().Be(72.5m, "a same-client copy keeps the client's target weight");
        copy.Weeks.Should().HaveCount(2);
        copy.Weeks.Should().OnlyContain(w => w.Status == WeekStatus.Draft && w.DatePublished == null);

        var copyMealIds = MealIds(copy);
        copyMealIds.Should().NotBeEmpty();
        copyMealIds.Should().NotIntersectWith(MealIds(source), "the copy mints a fresh MealId for every meal");
        copy.Supplements.Select(s => s.ExternalId).Should()
            .NotIntersectWith(source.Supplements.Select(s => s.ExternalId));
    }

    [Fact]
    public async Task Copy_ToOtherLinkedClientWithNameAndStartDate_UsesGivenValuesAndLeavesSourceUnchanged()
    {
        var nutritionist = await TestActors.Nutritionist(factory).CreateAsync(TestContext.Current.CancellationToken);
        var sourceClient = await LinkedClientAsync(nutritionist);
        var targetClient = await LinkedClientAsync(nutritionist);
        var source = await SeedPlanAsync(nutritionist.UserId, sourceClient.UserId);
        var startDate = NextMonday();

        var response = await nutritionist.Http.PostAsJsonAsync(
            $"/nutrition/plans/{source.ExternalId}/copy",
            new { ClientId = targetClient.PublicId, Name = "Copied For Target", StartDate = startDate },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<CopyResponseDto>(
            cancellationToken: TestContext.Current.CancellationToken);

        var copy = await FetchPlanAsync(body!.PlanId);
        copy.ClientId.Should().Be(targetClient.UserId);
        copy.Name.Should().Be("Copied For Target");
        copy.StartDate.Should().Be(startDate);
        copy.TargetWeightKg.Should().BeNull("target weight is client-specific and is not carried to another client");

        var sourceAfter = await FetchPlanAsync(source.ExternalId);
        sourceAfter.ClientId.Should().Be(sourceClient.UserId);
        sourceAfter.Status.Should().Be(NutritionPlanStatus.Active);
        sourceAfter.Version.Should().Be(4);
        sourceAfter.Weeks.Should().OnlyContain(w => w.Status == WeekStatus.Published && w.DatePublished != null);
        MealIds(sourceAfter).Should().BeEquivalentTo(MealIds(source));
    }

    // ── source gate ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Copy_ForeignPlan_Returns404()
    {
        var nutritionist = await TestActors.Nutritionist(factory).CreateAsync(TestContext.Current.CancellationToken);
        var client = await LinkedClientAsync(nutritionist);
        var source = await SeedPlanAsync(Guid.NewGuid(), client.UserId);

        var response = await nutritionist.Http.PostAsJsonAsync(
            $"/nutrition/plans/{source.ExternalId}/copy",
            new { ClientId = client.PublicId },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Copy_SourceClientLinkLacksNutritionCapability_Returns404()
    {
        var nutritionist = await TestActors.Nutritionist(factory).CreateAsync(TestContext.Current.CancellationToken);
        var sourceClient = await LinkedClientAsync(nutritionist, canViewNutritionPlans: false);
        var targetClient = await LinkedClientAsync(nutritionist);
        var source = await SeedPlanAsync(nutritionist.UserId, sourceClient.UserId);

        var response = await nutritionist.Http.PostAsJsonAsync(
            $"/nutrition/plans/{source.ExternalId}/copy",
            new { ClientId = targetClient.PublicId },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── target gate ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Copy_ToUnlinkedClient_Returns404()
    {
        var nutritionist = await TestActors.Nutritionist(factory).CreateAsync(TestContext.Current.CancellationToken);
        var sourceClient = await LinkedClientAsync(nutritionist);
        var unlinkedClient = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        var source = await SeedPlanAsync(nutritionist.UserId, sourceClient.UserId);

        var response = await nutritionist.Http.PostAsJsonAsync(
            $"/nutrition/plans/{source.ExternalId}/copy",
            new { ClientId = unlinkedClient.PublicId },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "an unlinked client must 404, never 403 — a 403 would confirm the client exists");
    }

    [Fact]
    public async Task Copy_ToClientWhoseLinkLacksNutritionCapability_Returns404()
    {
        var nutritionist = await TestActors.Nutritionist(factory).CreateAsync(TestContext.Current.CancellationToken);
        var sourceClient = await LinkedClientAsync(nutritionist);
        var capabilityLessClient = await LinkedClientAsync(nutritionist, canViewNutritionPlans: false);
        var source = await SeedPlanAsync(nutritionist.UserId, sourceClient.UserId);

        var response = await nutritionist.Http.PostAsJsonAsync(
            $"/nutrition/plans/{source.ExternalId}/copy",
            new { ClientId = capabilityLessClient.PublicId },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── overlap + validation wiring ──────────────────────────────────────────

    [Fact]
    public async Task Copy_StartDateOverlapsTargetClientsPlan_Returns409PlanOverlap()
    {
        var nutritionist = await TestActors.Nutritionist(factory).CreateAsync(TestContext.Current.CancellationToken);
        var client = await LinkedClientAsync(nutritionist);
        var startDate = NextMonday();
        var source = await SeedPlanAsync(nutritionist.UserId, client.UserId, weekCount: 2);
        await SeedPlanAsync(nutritionist.UserId, client.UserId, weekCount: 2, startDate: startDate);

        var response = await nutritionist.Http.PostAsJsonAsync(
            $"/nutrition/plans/{source.ExternalId}/copy",
            new { ClientId = client.PublicId, StartDate = startDate },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var responseBody = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        responseBody.Should().Contain("PLAN_OVERLAP");
    }

    [Fact]
    public async Task Copy_StartDateNotMonday_Returns400()
    {
        var nutritionist = await TestActors.Nutritionist(factory).CreateAsync(TestContext.Current.CancellationToken);
        var client = await LinkedClientAsync(nutritionist);
        var source = await SeedPlanAsync(nutritionist.UserId, client.UserId);

        var response = await nutritionist.Http.PostAsJsonAsync(
            $"/nutrition/plans/{source.ExternalId}/copy",
            new { ClientId = client.PublicId, StartDate = NextMonday().AddDays(1) },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private sealed class CopyResponseDto
    {
        public Guid PlanId { get; set; }
        public Guid ClientId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
    }
}
