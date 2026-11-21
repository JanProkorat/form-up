using System.Text.Json;
using System.Text.RegularExpressions;
using FormUp.Tests.Infrastructure;
using FluentAssertions;

namespace FormUp.Tests.Architecture;

/// <summary>
/// Every path placeholder in the published OpenAPI document must match a declared path parameter
/// name exactly, and the reverse. The generated clients substitute by name, so a case mismatch
/// sends the literal placeholder.
/// </summary>
[Collection(TestCollection.Shard4)]
public class OpenApiPathParameterNameTests(FitnessApiFactory factory)
{
    private static readonly string[] HttpMethods = ["get", "put", "post", "delete", "options", "head", "patch", "trace"];

    [Fact]
    public async Task Swagger_EveryPathPlaceholder_MatchesDeclaredPathParameterNameExactly()
    {
        using var client = factory.CreateClient();
        var json = await client.GetStringAsync("/swagger/v1/swagger.json", TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(json);

        var offenders = new List<string>();

        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            var placeholders = Regex.Matches(path.Name, "{([^}]+)}").Select(m => m.Groups[1].Value).ToList();

            var pathLevel = DeclaredPathParameters(path.Value);

            foreach (var operation in path.Value.EnumerateObject().Where(p => HttpMethods.Contains(p.Name)))
            {
                var declared = pathLevel.Concat(DeclaredPathParameters(operation.Value)).ToList();

                var missing = placeholders.Except(declared, StringComparer.Ordinal).ToList();
                var extra = declared.Except(placeholders, StringComparer.Ordinal).ToList();

                if (missing.Count > 0 || extra.Count > 0)
                {
                    offenders.Add(
                        $"{operation.Name.ToUpperInvariant()} {path.Name}: placeholders without a parameter [{string.Join(", ", missing)}], parameters without a placeholder [{string.Join(", ", extra)}]");
                }
            }
        }

        string.Join(Environment.NewLine, offenders).Should().BeEmpty("a placeholder that differs in case from its parameter is sent literally by the generated client");
    }

    private static List<string> DeclaredPathParameters(JsonElement element) =>
        element.TryGetProperty("parameters", out var parameters)
            ? parameters.EnumerateArray()
                .Where(p => p.TryGetProperty("in", out var location) && location.GetString() == "path")
                .Select(p => p.GetProperty("name").GetString()!)
                .ToList()
            : [];
}
