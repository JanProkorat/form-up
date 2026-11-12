using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Endpoints.Auth;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.PaymentSettings;

/// <summary>
/// The bank account endpoints sit on the auth rate-limit policy (10 per 15 minutes per IP). Runs on
/// <see cref="RateLimitEnabledFactory"/>, which keeps limiting on and lets a test pin the client IP.
/// </summary>
[Collection("RateLimitPolicyTests")]
public class PaymentSettingsRateLimitTests(RateLimitEnabledFactory factory)
{
    private static int _ipCounter;

    private static string UniqueIp()
    {
        var next = (uint)Interlocked.Increment(ref _ipCounter);
        return $"10.77.{(next >> 8) & 255}.{next & 255}";
    }

    /// <summary>
    /// Puts the registered coach on the seeded Pro plan; a coach without a subscription is on Free, which has no payments.
    /// </summary>
    private async Task SubscribeToProAsync(string email)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var ct = TestContext.Current.CancellationToken;

        var profileId = await db.ProfessionalProfiles
            .Where(p => p.User.Email == email)
            .Select(p => p.Id)
            .SingleAsync(ct);
        var price = await db.SubscriptionPlanPrices
            .Where(p => p.SubscriptionPlan.Code == "pro" && p.ValidTo == null)
            .SingleAsync(ct);

        db.CoachSubscriptions.Add(new CoachSubscription
        {
            ProfessionalProfileId = profileId,
            SubscriptionPlanId = price.SubscriptionPlanId,
            SubscriptionPlanPriceId = price.Id,
            Status = SubscriptionStatus.Active,
        });
        await db.SaveChangesAsync(ct);
    }

    [Fact]
    public async Task BankAccountCode_MoreThanTenRequestsFromOneIp_Returns429OnTheEleventh()
    {
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestClientIpStartupFilter.Header, UniqueIp());

        // Register and login use two of the ten permits; the code endpoint (400 for a coach with
        // a password) uses the other eight, so the ninth call is the eleventh request overall.
        var email = $"{Guid.NewGuid():N}@payments-ratelimit.com";
        (await TestHelpers.RegisterAsync(client, email, "TestPass1!", "Rate", "Coach", "Trainer")).IsSuccessStatusCode.Should().BeTrue();
        await SubscribeToProAsync(email);
        var (accessToken, _) = await TestHelpers.LoginAsync(client, email, "TestPass1!");
        TestHelpers.SetBearerToken(client, accessToken);

        for (var attempt = 1; attempt <= 8; attempt++)
        {
            var response = await client.PostAsync("/payments/bank-account/code", null, TestContext.Current.CancellationToken);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, $"request {attempt} is within the budget");
        }

        var limited = await client.PostAsync("/payments/bank-account/code", null, TestContext.Current.CancellationToken);

        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }
}
