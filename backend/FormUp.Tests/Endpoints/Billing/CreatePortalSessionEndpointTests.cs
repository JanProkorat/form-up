using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FormUp.Application.Features.Billing.CreatePortalSession;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Billing;

/// <summary>
/// Integration tests for <c>POST /billing/portal-session</c> (<c>CreatePortalSessionEndpoint</c>) against the fake
/// payment provider.
/// </summary>
[Collection(TestCollection.Name)]
public class CreatePortalSessionEndpointTests(FitnessApiFactory factory)
{
    private const string Route = "/billing/portal-session";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<string> SubscribeWithCustomerAsync(Actor coach, string? customerId)
    {
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, "free");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.CoachSubscriptions.Where(cs => cs.ProfessionalProfileId == coach.ProfileId)
            .ExecuteUpdateAsync(s => s.SetProperty(cs => cs.ExternalCustomerId, customerId), Ct);

        return customerId ?? string.Empty;
    }

    [Fact]
    public async Task Portal_ForACoachWithACustomer_ReturnsTheUrlForThatCustomer()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var customerId = await SubscribeWithCustomerAsync(coach, $"cus_{Guid.NewGuid():N}");

        var response = await coach.Http.PostAsync(Route, null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<CreatePortalSessionResponse>(Ct))!.Url.Should().Be("https://portal.stripe.test/session");
        fake.Portals.Should().ContainSingle().Which.Should().Be(customerId, "the customer comes from the caller's own subscription");
    }

    [Fact]
    public async Task Portal_ForACoachWithoutACustomer_Returns404()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await SubscribeWithCustomerAsync(coach, null);

        var response = await coach.Http.PostAsync(Route, null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("BILLING_CUSTOMER_NOT_FOUND");
        fake.Portals.Should().BeEmpty();
    }

    [Fact]
    public async Task Portal_WhenStripeIsNotConfigured_Returns503()
    {
        var fake = BillingTestData.ResetProvider(factory);
        fake.IsConfigured = false;
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await SubscribeWithCustomerAsync(coach, $"cus_{Guid.NewGuid():N}");

        var response = await coach.Http.PostAsync(Route, null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("BILLING_NOT_CONFIGURED");
    }

    [Fact]
    public async Task Portal_WhenStripeFails_Returns502()
    {
        var fake = BillingTestData.ResetProvider(factory);
        fake.Fail = true;
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await SubscribeWithCustomerAsync(coach, $"cus_{Guid.NewGuid():N}");

        var response = await coach.Http.PostAsync(Route, null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
    }

    [Fact]
    public async Task Portal_CalledByAClient_Returns403()
    {
        BillingTestData.ResetProvider(factory);
        var client = await TestActors.Client(factory).CreateAsync(Ct);

        var response = await client.Http.PostAsync(Route, null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
