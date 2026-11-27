using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.NutritionPlanTemplates;

/// <summary>
/// Real-DB integration tests for the <c>Maintain</c> and <c>Performance</c> template goals. Every search
/// narrows to the test's own unique name because the collection is shared.
/// </summary>
[Collection(TestCollection.Shard4)]
public class TemplateGoalValuesTests(FitnessApiFactory factory)
{
    private static string UniqueName() => $"tgv-{Guid.NewGuid():N}";

    private async Task SeedTemplateAsync(Guid ownerId, string name, PrimaryGoal goal)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        await mongo.NutritionPlanTemplates.InsertOneAsync(
            new NutritionPlanTemplate
            {
                ExternalId = Guid.NewGuid(),
                OwnerId = ownerId,
                Name = name,
                Visibility = LibraryVisibility.Private,
                Version = 1,
                DateCreated = DateTime.UtcNow,
                Goal = goal,
                Weeks = [new TemplateWeek { WeekNumber = 1, Days = [] }],
                WeekCount = 1
            },
            cancellationToken: TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("Maintain")]
    [InlineData("Performance")]
    public async Task Create_WithNewGoalValue_Returns201AndPersistsGoal(string goal)
    {
        var nutritionist = await TestActors.Nutritionist(factory).CreateAsync(TestContext.Current.CancellationToken);

        var response = await nutritionist.Http.PostAsJsonAsync(
            "/nutrition/plan-templates",
            new { Name = UniqueName(), WeekCount = 1, Goal = goal },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<SummaryDto>(
            cancellationToken: TestContext.Current.CancellationToken);
        body!.Goal.Should().Be(goal);
    }

    [Fact]
    public async Task Create_WithGoalOutsideEnum_Returns400()
    {
        var nutritionist = await TestActors.Nutritionist(factory).CreateAsync(TestContext.Current.CancellationToken);

        var response = await nutritionist.Http.PostAsJsonAsync(
            "/nutrition/plan-templates",
            new { Name = UniqueName(), WeekCount = 1, Goal = 7 },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Search_GoalPerformance_ReturnsOnlyPerformanceTemplates()
    {
        var nutritionist = await TestActors.Nutritionist(factory).CreateAsync(TestContext.Current.CancellationToken);
        var name = UniqueName();
        await SeedTemplateAsync(nutritionist.UserId, name, PrimaryGoal.Performance);
        await SeedTemplateAsync(nutritionist.UserId, name, PrimaryGoal.Maintain);
        await SeedTemplateAsync(nutritionist.UserId, name, PrimaryGoal.Health);

        var performance = await SearchAsync(nutritionist.Http, name, "&goal=Performance");
        performance.TotalCount.Should().Be(1);
        performance.Templates.Should().ContainSingle().Which.Goal.Should().Be("Performance");

        var maintain = await SearchAsync(nutritionist.Http, name, "&goal=Maintain");
        maintain.TotalCount.Should().Be(1);
        maintain.Templates.Should().ContainSingle().Which.Goal.Should().Be("Maintain");

        var unfiltered = await SearchAsync(nutritionist.Http, name, string.Empty);
        unfiltered.TotalCount.Should().Be(3);
    }

    private static async Task<SearchDto> SearchAsync(HttpClient client, string name, string extraQuery)
    {
        var response = await client.GetAsync(
            $"/nutrition/plan-templates?search={name}&pageSize=100{extraQuery}",
            TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<SearchDto>(
            cancellationToken: TestContext.Current.CancellationToken);
        return body!;
    }

    private sealed class SummaryDto
    {
        public string? Goal { get; set; }
    }

    private sealed class SearchDto
    {
        public List<SummaryDto> Templates { get; set; } = [];
        public long TotalCount { get; set; }
    }
}
