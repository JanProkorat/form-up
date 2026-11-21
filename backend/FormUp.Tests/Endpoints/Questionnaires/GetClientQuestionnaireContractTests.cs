using System.Text.Json;
using FormUp.Tests.Infrastructure;
using FluentAssertions;

namespace FormUp.Tests.Endpoints.Questionnaires;

/// <summary>Swagger contract of GET /client/questionnaire, which the generated clients are built from.</summary>
[Collection(TestCollection.Shard3)]
public class GetClientQuestionnaireContractTests(FitnessApiFactory factory)
{
    [Fact]
    public async Task Swagger_GetClientQuestionnaire_ListsBothQueryParameters()
    {
        using var client = factory.CreateClient();
        var json = await client.GetStringAsync("/swagger/v1/swagger.json", TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(json);

        var operation = document.RootElement.GetProperty("paths").GetProperty("/client/questionnaire").GetProperty("get");
        var queryParameters = operation.GetProperty("parameters").EnumerateArray()
            .Where(p => p.GetProperty("in").GetString() == "query")
            .Select(p => p.GetProperty("name").GetString()!)
            .ToList();

        queryParameters.Should().Contain(name => string.Equals(name, "linkPublicId", StringComparison.OrdinalIgnoreCase));
        queryParameters.Should().Contain(name => string.Equals(name, "responsePublicId", StringComparison.OrdinalIgnoreCase));
    }
}
