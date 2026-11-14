using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FormUp.Tests.Endpoints.Auth;
using FormUp.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FormUp.Tests.Endpoints.Billing;

/// <summary>
/// The promotion-code budget is per coach account, not per IP: exhausting one coach's 10 permits answers 429 for that
/// coach only, even from the same address. Runs on the rate-limit-enabled host, which has no Stripe keys, so every
/// permitted call answers 503 after the limiter.
/// </summary>
[Collection("RateLimitPolicyTests")]
public class ApplyPromotionCodeRateLimitTests(RateLimitEnabledFactory factory)
{
    private const string Route = "/billing/promotion-code";
    private const int PermitLimit = 10;

    private static int ipCounter;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient ClientWithIp(string ip)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestClientIpStartupFilter.Header, ip);

        return client;
    }

    private static string UniqueTrustedIp()
    {
        var next = (uint)Interlocked.Increment(ref ipCounter) % 65_000u + 1;

        return $"10.200.{(next >> 8) & 255}.{next & 255}";
    }

    private static async Task SignInCoachAsync(HttpClient client)
    {
        var email = $"{Guid.NewGuid():N}@ratelimit-test.com";
        await TestHelpers.RegisterAsync(client, email, "TestPass1!", "Coach", "Promo", "Trainer");
        var (accessToken, _) = await TestHelpers.LoginAsync(client, email, "TestPass1!");
        TestHelpers.SetBearerToken(client, accessToken);
    }

    [Fact]
    public async Task PromotionCodeBudget_IsPerCoach_AndAnotherCoachOnTheSameIpIsUnaffected()
    {
        var sharedIp = UniqueTrustedIp();
        using var first = ClientWithIp(sharedIp);
        using var second = ClientWithIp(sharedIp);
        await SignInCoachAsync(first);
        await SignInCoachAsync(second);

        for (var attempt = 0; attempt < PermitLimit; attempt++)
        {
            var response = await first.PostAsJsonAsync(Route, new { Code = "WELCOME" }, Ct);
            response.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests, $"call {attempt + 1} is inside the budget");
        }

        var exhausted = await first.PostAsJsonAsync(Route, new { Code = "WELCOME" }, Ct);
        var other = await second.PostAsJsonAsync(Route, new { Code = "WELCOME" }, Ct);

        exhausted.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        other.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests, "the partition key is the coach, not the IP");
    }
}
