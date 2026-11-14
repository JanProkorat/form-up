using System.Text.Json;
using FormUp.Tests.Infrastructure;
using FluentAssertions;

namespace FormUp.Tests.Architecture;

/// <summary>
/// Swagger contract for operations whose declared status codes once drifted from what they send: the
/// generated clients only accept statuses the spec declares, so each operation must list exactly the
/// codes it can return (FastEndpoints adds 401 and, for role-gated routes, 403 itself). Typed success
/// responses must keep their schema, and a 400 that sends a body must keep its ProblemDetails schema.
/// </summary>
[Collection(TestCollection.Name)]
public class StatusCodeContractTests(FitnessApiFactory factory)
{
    private const string Body = "body";
    private const string Empty = "empty";
    private const string Absent = "absent";

    private static Task<JsonDocument>? swaggerDocument;

    [Theory]
    [InlineData("put", "/users/me", "200,400,401,404", false, Body)]
    [InlineData("put", "/users/me/timezone", "200,400,401,404", false, Body)]
    [InlineData("put", "/trainer/profile", "200,400,401,403,404", false, Body)]
    [InlineData("post", "/auth/password/reset", "200,400,404,429", false, Body)]
    [InlineData("put", "/auth/password/reset", "200,400,429", false, Body)]
    [InlineData("post", "/foods/{foodId}/gallery/promote", "204,400,401,403,404,409", false, Body)]
    [InlineData("post", "/recipes/{recipeId}/gallery/promote", "204,400,401,403,404,409", false, Body)]
    [InlineData("put", "/auth/email", "200,400,401,404,429", true, Body)]
    [InlineData("post", "/auth/verify-email", "200,400,429", true, Body)]
    [InlineData("post", "/auth/register", "201,400,429", true, Body)]
    [InlineData("post", "/auth/resend-verification/anonymous", "200,400,429", true, Body)]
    [InlineData("post", "/auth/login", "200,400,429", true, Body)]
    [InlineData("post", "/auth/refresh", "200,400,429", true, Body)]
    [InlineData("post", "/auth/social/google", "200,400,401,403,409,429", true, Body)]
    [InlineData("post", "/auth/social/apple", "200,400,401,403,409,422,429", true, Body)]
    [InlineData("post", "/auth/social/nonce", "200,429", true, Absent)]
    [InlineData("post", "/users/me/password", "200,400,401,404,429", true, Body)]
    [InlineData("post", "/users/email-links/turn-on", "200,400,429", true, Body)]
    [InlineData("post", "/users/email-links/turn-off", "200,400,429", true, Body)]
    [InlineData("post", "/users/email-links/one-click", "200,400,429", false, Empty)]
    [InlineData("put", "/payments/bank-account", "200,400,401,403,404,429", true, Body)]
    [InlineData("post", "/payments/bank-account/undo", "204,400,409,429", false, Body)]
    [InlineData("post", "/payments/bank-account/code", "204,400,401,403,404,429", false, Body)]
    [InlineData("post", "/payments/clients/{clientId}/payments/{paymentId}/request", "200,400,401,403,404,409,429", true, Body)]
    [InlineData("get", "/users/me/subscription", "200,401,403,404", true, Absent)]
    [InlineData("get", "/users/me/subscription/keep-clients", "200,401,403,404,409", true, Absent)]
    [InlineData("put", "/users/me/subscription/keep-clients", "204,400,401,403,404,409", false, Body)]
    [InlineData("get", "/users/me/subscription/plans", "200,401,403", true, Absent)]
    [InlineData("post", "/users/me/subscription/choose-free", "204,400,401,403,404,409", false, Body)]
    [InlineData("put", "/admin/coaches/{coachId}/subscription", "200,400,401,403,404", true, Body)]
    [InlineData("get", "/admin/coaches", "200,400,401,403", true, Body)]
    [InlineData("get", "/admin/coaches/{coachId}", "200,401,403,404", true, Absent)]
    [InlineData("post", "/admin/coaches/{coachId}/give-free", "200,400,401,403,404,502", true, Body)]
    [InlineData("post", "/billing/checkout-session", "200,400,401,403,404,409,502,503", true, Body)]
    [InlineData("post", "/billing/portal-session", "200,401,403,404,502,503", true, Absent)]
    [InlineData("post", "/billing/change-plan", "200,400,401,403,409,502,503", true, Body)]
    [InlineData("post", "/billing/switch-to-free", "200,401,403,409,502,503", true, Absent)]
    [InlineData("post", "/billing/promotion-code", "204,400,401,403,404,409,429,502,503", false, Body)]
    [InlineData("post", "/billing/second-role/preview", "200,401,403,409,502,503", true, Absent)]
    [InlineData("post", "/billing/second-role/confirm", "200,400,401,402,403,409,502,503", true, Body)]
    [InlineData("post", "/admin/coaches/{coachId}/cancel-renewal", "200,400,401,403,404,409", true, Body)]
    [InlineData("post", "/admin/coaches/{coachId}/end-plan", "200,400,401,403,404,409", true, Body)]
    [InlineData("post", "/admin/coaches/{coachId}/disable", "200,400,401,403,404", true, Body)]
    public async Task Swagger_Operation_DeclaresExactlyTheStatusCodesItCanReturn(
        string method, string route, string expected, bool typedSuccess, string badRequest)
    {
        var document = await LoadSwaggerAsync();

        document.RootElement.GetProperty("paths").TryGetProperty(route, out var pathItem)
            .Should().BeTrue("the spec must contain {0}", route);

        var responses = pathItem.GetProperty(method).GetProperty("responses");
        var codes = responses.EnumerateObject()
            .Select(response => response.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        codes.Should().Equal(expected.Split(','), "the generated client throws on an undeclared status");

        var success = codes.Single(code => code.StartsWith('2'));
        HasSchema(responses.GetProperty(success)).Should().Be(
            typedSuccess, "a typed success keeps its schema and an untyped one stays void in the generated client");

        switch (badRequest)
        {
            case Body:
                HasSchema(responses.GetProperty("400")).Should().BeTrue("a 400 with a body keeps its ProblemDetails schema");
                break;
            case Empty:
                HasSchema(responses.GetProperty("400")).Should().BeFalse("this 400 is sent without a body");
                break;
            default:
                responses.TryGetProperty("400", out _).Should().BeFalse("this operation sends no 400");
                break;
        }
    }

    private static bool HasSchema(JsonElement response) =>
        response.TryGetProperty("content", out var content)
        && content.EnumerateObject().Any()
        && content.EnumerateObject().All(media => media.Value.TryGetProperty("schema", out _));

    private Task<JsonDocument> LoadSwaggerAsync() => swaggerDocument ??= FetchAsync();

    private async Task<JsonDocument> FetchAsync()
    {
        using var client = factory.CreateClient();
        var json = await client.GetStringAsync("/swagger/v1/swagger.json", TestContext.Current.CancellationToken);

        return JsonDocument.Parse(json);
    }
}
