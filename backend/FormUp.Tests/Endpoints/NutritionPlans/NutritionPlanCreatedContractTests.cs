using System.Text.Json;
using FormUp.Tests.Infrastructure;
using FluentAssertions;

namespace FormUp.Tests.Endpoints.NutritionPlans;

/// <summary>
/// Swagger contract for the nutrition-plan endpoints that send 201: the generated client only accepts
/// statuses the spec declares, so each must list 201 and not 200.
/// </summary>
[Collection(TestCollection.Shard3)]
public class NutritionPlanCreatedContractTests(FitnessApiFactory factory)
{
    [Theory]
    [InlineData("/nutrition/plans")]
    [InlineData("/nutrition/plans/{planId}/copy")]
    public async Task Swagger_PlanCreatingPost_DeclaresCreatedAndNotOk(string route)
    {
        using var client = factory.CreateClient();

        var json = await client.GetStringAsync("/swagger/v1/swagger.json", TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(json);

        var paths = document.RootElement.GetProperty("paths");
        paths.TryGetProperty(route, out var pathItem).Should().BeTrue("the spec must contain {0}", route);

        var responses = pathItem.GetProperty("post").GetProperty("responses");

        responses.TryGetProperty("201", out _).Should().BeTrue();
        responses.TryGetProperty("200", out _).Should().BeFalse();
    }
}
