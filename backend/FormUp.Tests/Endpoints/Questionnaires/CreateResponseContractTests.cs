using System.Text.Json;
using FormUp.Tests.Infrastructure;
using FluentAssertions;

namespace FormUp.Tests.Endpoints.Questionnaires;

/// <summary>Swagger contract of POST /client/questionnaire/response, which the generated clients are built from.</summary>
[Collection(TestCollection.Shard2)]
public class CreateResponseContractTests(FitnessApiFactory factory)
{
    [Fact]
    public async Task Swagger_CreateResponse_DeclaresTypedCreatedBodyAndNoOk()
    {
        using var client = factory.CreateClient();
        var json = await client.GetStringAsync("/swagger/v1/swagger.json", TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(json);

        var responses = document.RootElement.GetProperty("paths")
            .GetProperty("/client/questionnaire/response").GetProperty("post").GetProperty("responses");

        responses.TryGetProperty("200", out _).Should().BeFalse();
        var schema = responses.GetProperty("201").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema");

        var schemaRef = schema.GetProperty("$ref").GetString()!;
        var schemaName = schemaRef[(schemaRef.LastIndexOf('/') + 1)..];
        var properties = document.RootElement.GetProperty("components").GetProperty("schemas")
            .GetProperty(schemaName).GetProperty("properties");

        properties.EnumerateObject().Select(p => p.Name)
            .Should().Contain(name => string.Equals(name, "responsePublicId", StringComparison.OrdinalIgnoreCase));
    }
}
