using System.Net;
using FormUp.Tests.Infrastructure;
using FluentAssertions;

namespace FormUp.Tests.Endpoints.SessionTemplates;

/// <summary>
/// Full-pipeline role-gate tests for the session-template feature (#860). <c>LibraryAccessGuard</c>'s
/// own remarks state that role authorization is the endpoint's precondition, not something the
/// guard enforces — <c>CanRead</c> returns <c>true</c> for any caller on a Public entry. Without
/// an explicit <c>Roles(AppRoles.Trainer)</c> check on every route, a Nutritionist could enumerate
/// and read trainer session templates through search and detail. These tests exercise the real
/// ASP.NET Core authorization pipeline (unlike the Testcontainers <c>HandleAsync</c>-direct tests
/// in <c>SessionTemplateEndpointTests</c>, which bypass role middleware entirely), so this is the
/// only place the role gate is actually proven, not merely assumed from <c>Configure()</c>.
/// </summary>
[Collection(TestCollection.Shard3)]
public class SessionTemplateRoleGateIntegrationTests(FitnessApiFactory factory)
{
    private static string UniqueEmail(string tag) => $"{Guid.NewGuid():N}@sessiontemplate-rolegate-{tag}.com";

    private async Task<HttpClient> SetupNutritionistAsync()
    {
        var http = factory.CreateClient();
        var email = UniqueEmail("nutritionist");

        await TestHelpers.RegisterAsync(http, email, "TestPass1!", "Test", "Nutritionist", "Nutritionist");
        var (token, _) = await TestHelpers.LoginAsync(http, email, "TestPass1!");
        TestHelpers.SetBearerToken(http, token);

        return http;
    }

    [Fact]
    public async Task Search_NutritionistRole_Returns403()
    {
        var http = await SetupNutritionistAsync();

        var response = await http.GetAsync(
            "/training/session-templates", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetDetail_NutritionistRoleOnNonexistentTemplate_Returns403()
    {
        var http = await SetupNutritionistAsync();

        // A Nutritionist must be refused before any ownership/visibility check runs — even
        // against a route param for a template that does not exist, the role gate rejects first.
        var response = await http.GetAsync(
            $"/training/session-templates/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
