using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FormUp.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FormUp.Tests.Endpoints.Auth;

/// <summary>
/// The anonymous resolve sits on its own policy (20 per 15 minutes per IP), not the auth budget. Runs on
/// <see cref="RateLimitEnabledFactory"/>, which keeps limiting on and lets a test pin the client IP.
/// </summary>
[Collection("RateLimitPolicyTests")]
public class ResolveInviteTokenRateLimitTests(RateLimitEnabledFactory factory)
{
    private static int _ipCounter;

    private static string UniqueIp()
    {
        var next = (uint)Interlocked.Increment(ref _ipCounter);
        return $"10.79.{(next >> 8) & 255}.{next & 255}";
    }

    private static Task<HttpResponseMessage> ResolveAsync(HttpClient client) =>
        client.PostAsJsonAsync(
            "/auth/invite/resolve",
            new { token = Guid.NewGuid().ToString("N") },
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task Resolve_MoreThanTwentyRequestsFromOneIp_Returns429OnTheTwentyFirst()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestClientIpStartupFilter.Header, UniqueIp());

        for (var attempt = 1; attempt <= 20; attempt++)
        {
            var response = await ResolveAsync(client);
            response.StatusCode.Should().Be(HttpStatusCode.NotFound, $"request {attempt} is within the budget");
        }

        var limited = await ResolveAsync(client);

        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }
}
