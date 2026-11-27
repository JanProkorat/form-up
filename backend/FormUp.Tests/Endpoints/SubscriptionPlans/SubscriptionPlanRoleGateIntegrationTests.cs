using System.Net;
using System.Net.Http.Json;
using FormUp.Tests.Infrastructure;
using FluentAssertions;

namespace FormUp.Tests.Endpoints.SubscriptionPlans;

/// <summary>
/// Full-pipeline role-gate tests for the Admin-only subscription-plan CRUD surface (#595).
/// <c>Factory.Create&lt;TEndpoint&gt;()</c> bypasses ASP.NET Core's role middleware entirely, so
/// this is the only place the <c>Roles(AppRoles.Admin)</c> gate is actually proven — same
/// precedent as <c>SessionTemplateRoleGateIntegrationTests</c>.
/// </summary>
[Collection(TestCollection.Shard4)]
public class SubscriptionPlanRoleGateIntegrationTests(FitnessApiFactory factory)
{
    private static string UniqueEmail(string tag) => $"{Guid.NewGuid():N}@subscriptionplan-rolegate-{tag}.com";
    private static string UniqueCode() => $"tier-{Guid.NewGuid():N}"[..30];

    private async Task<HttpClient> SetupTrainerAsync()
    {
        var http = factory.CreateClient();
        var email = UniqueEmail("trainer");

        await TestHelpers.RegisterAsync(http, email, "TestPass1!", "Test", "Trainer", "Trainer");
        var (token, _) = await TestHelpers.LoginAsync(http, email, "TestPass1!");
        TestHelpers.SetBearerToken(http, token);

        return http;
    }

    [Fact]
    public async Task List_NonAdminRole_Returns403()
    {
        var http = await SetupTrainerAsync();

        var response = await http.GetAsync(
            "/admin/subscription-plans", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Create_NonAdminRole_Returns403()
    {
        var http = await SetupTrainerAsync();

        var response = await http.PostAsJsonAsync(
            "/admin/subscription-plans",
            new { Code = UniqueCode() },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Update_NonAdminRole_Returns403()
    {
        var http = await SetupTrainerAsync();

        var response = await http.PutAsJsonAsync(
            $"/admin/subscription-plans/{UniqueCode()}",
            new { },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Deactivate_NonAdminRole_Returns403()
    {
        var http = await SetupTrainerAsync();

        var response = await http.DeleteAsync(
            $"/admin/subscription-plans/{UniqueCode()}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

}
