using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.ClientNutrition.GetFullPlan;
using FormUp.Tests.Builders;
using FormUp.Tests.Endpoints.NutritionPlans;
using FormUp.Tests.Infrastructure;

namespace FormUp.Tests.Endpoints.ClientNutrition;

/// <summary>
/// Integration tests (real MongoDB, which unit mocks cannot prove) for GET /client/nutrition/plans/{PlanId}.
/// </summary>
[Collection(TestCollection.Shard4)]
public class GetClientNutritionPlanIntegrationTests(FitnessApiFactory factory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private Task<Application.Domain.Documents.NutritionPlan> InsertPlanAsync(
        Guid clientId, NutritionPlanStatus status, DateTime start, params Application.Domain.Documents.PlanWeek[] weeks) =>
        ClientNutritionFixtures.InsertPlanAsync(factory, clientId, status, start, null, null, weeks);

    private static Application.Domain.Documents.PlanWeek PublishedWeek(int number) =>
        ClientNutritionFixtures.Week(number, WeekStatus.Published,
            ClientNutritionFixtures.MealWithFoods(MealKind.Lunch, PlanTestHelpers.CreateMealFood()));

    private static Task<HttpResponseMessage> GetAsync(Actor client, Guid planId) =>
        client.Http.GetAsync($"/client/nutrition/plans/{planId}", TestContext.Current.CancellationToken);

    [Fact]
    public async Task GetPlan_OwnerActivePlanInWindow_Returns200WithCurrentWeek()
    {
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        var plan = await InsertPlanAsync(client.UserId, NutritionPlanStatus.Active,
            ClientNutritionFixtures.CurrentWeekMondayUtc(), PublishedWeek(1));

        var response = await GetAsync(client, plan.ExternalId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<GetFullPlanResponse>(Json, TestContext.Current.CancellationToken))!;
        body.PlanId.Should().Be(plan.ExternalId);
        body.Status.Should().Be("Active");
        body.CurrentWeek.Should().Be(1);
        body.CurrentDayOfWeek.Should().NotBeNull();
        body.Weeks.Should().ContainSingle();
    }

    [Fact]
    public async Task GetPlan_OwnerCompletedPlan_Returns200WithNullCurrentWeek()
    {
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        var plan = await InsertPlanAsync(client.UserId, NutritionPlanStatus.Completed,
            ClientNutritionFixtures.CurrentWeekMondayUtc(), PublishedWeek(1));

        var response = await GetAsync(client, plan.ExternalId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<GetFullPlanResponse>(Json, TestContext.Current.CancellationToken))!;
        body.Status.Should().Be("Completed");
        body.CurrentWeek.Should().BeNull();
        body.CurrentDayOfWeek.Should().BeNull();
        body.Weeks.Should().ContainSingle();
    }

    [Fact]
    public async Task GetPlan_OwnerActivePlanPastItsWindow_Returns200WithNullCurrentWeek()
    {
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        var plan = await InsertPlanAsync(client.UserId, NutritionPlanStatus.Active,
            ClientNutritionFixtures.CurrentWeekMondayUtc().AddDays(-21), PublishedWeek(1));

        var response = await GetAsync(client, plan.ExternalId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<GetFullPlanResponse>(Json, TestContext.Current.CancellationToken))!;
        body.CurrentWeek.Should().BeNull();
        body.CurrentDayOfWeek.Should().BeNull();
    }

    [Fact]
    public async Task GetPlan_UnpublishedWeeks_AreExcluded()
    {
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        var plan = await InsertPlanAsync(client.UserId, NutritionPlanStatus.Active,
            ClientNutritionFixtures.CurrentWeekMondayUtc(),
            PublishedWeek(1), ClientNutritionFixtures.Week(2, WeekStatus.Draft));

        var response = await GetAsync(client, plan.ExternalId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<GetFullPlanResponse>(Json, TestContext.Current.CancellationToken))!;
        body.Weeks.Should().ContainSingle().Which.WeekNumber.Should().Be(1);
        body.PublishedWeekCount.Should().Be(1);
        body.TotalWeeks.Should().Be(2);
    }

    [Fact]
    public async Task GetPlan_NoPublishedWeek_Returns404()
    {
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        var plan = await InsertPlanAsync(client.UserId, NutritionPlanStatus.Active,
            ClientNutritionFixtures.CurrentWeekMondayUtc(), ClientNutritionFixtures.Week(1, WeekStatus.Draft));

        var response = await GetAsync(client, plan.ExternalId);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetPlan_AnotherClientsPlan_Returns404()
    {
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        var other = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        var plan = await InsertPlanAsync(other.UserId, NutritionPlanStatus.Active,
            ClientNutritionFixtures.CurrentWeekMondayUtc(), PublishedWeek(1));

        var response = await GetAsync(client, plan.ExternalId);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(NutritionPlanStatus.Draft)]
    [InlineData(NutritionPlanStatus.Archived)]
    public async Task GetPlan_DraftOrArchivedPlan_Returns404(NutritionPlanStatus status)
    {
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);
        var plan = await InsertPlanAsync(client.UserId, status,
            ClientNutritionFixtures.CurrentWeekMondayUtc(), PublishedWeek(1));

        var response = await GetAsync(client, plan.ExternalId);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetPlan_UnknownPlanId_Returns404()
    {
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);

        var response = await GetAsync(client, Guid.NewGuid());

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetPlan_EmptyPlanId_Returns400()
    {
        var client = await TestActors.Client(factory).CreateAsync(TestContext.Current.CancellationToken);

        var response = await GetAsync(client, Guid.Empty);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
