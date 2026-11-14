using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;

namespace FormUp.Tests.Endpoints.Billing;

/// <summary>
/// Integration tests for <c>POST /billing/promotion-code</c> (<c>ApplyPromotionCodeEndpoint</c>) against the fake
/// payment provider.
/// </summary>
[Collection(TestCollection.Name)]
public class ApplyPromotionCodeEndpointTests(FitnessApiFactory factory) : IDisposable
{
    /// <summary>Leaves the shared fake provider in its defaults for the next test class.</summary>
    public void Dispose() => BillingTestData.ResetProvider(factory);

    private const string Route = "/billing/promotion-code";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Body(string code) => new { Code = code };

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var raw = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(status, raw);
        raw.Should().Contain(code);
    }

    [Fact]
    public async Task ApplyCode_OnTheCallersOwnLiveSubscription_Returns204()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await BillingTestData.StripePlanAsync(factory);
        var subscriptionId = await BillingTestData.SubscribeLiveAsync(factory, coach, plan);

        var response = await coach.Http.PostAsJsonAsync(Route, Body("WELCOME-10"), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        fake.PromotionCodeSubscriptions.Should().ContainSingle().Which.Should().Be(subscriptionId);
    }

    [Fact]
    public async Task ApplyCode_ThatStripeDoesNotKnow_Returns404_AndDoesNotEchoTheCode()
    {
        var fake = BillingTestData.ResetProvider(factory);
        fake.RejectPromotionCodes = true;
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await BillingTestData.StripePlanAsync(factory);
        await BillingTestData.SubscribeLiveAsync(factory, coach, plan);

        var response = await coach.Http.PostAsJsonAsync(Route, Body("SECRETCODE"), Ct);

        await AssertErrorAsync(response, HttpStatusCode.NotFound, "PROMOTION_CODE_NOT_FOUND");
        (await response.Content.ReadAsStringAsync(Ct)).Should().NotContain("SECRETCODE");
    }

    [Fact]
    public async Task ApplyCode_WithInvalidCharacters_Returns400_WithoutCallingStripe()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);

        var response = await coach.Http.PostAsJsonAsync(Route, Body("bad code!"), Ct);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "PROMOTION_CODE_INVALID");
        fake.PromotionCodeSubscriptions.Should().BeEmpty();
    }

    [Fact]
    public async Task ApplyCode_WithoutALiveSubscription_Returns409()
    {
        BillingTestData.ResetProvider(factory);
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, "free");

        await AssertErrorAsync(await coach.Http.PostAsJsonAsync(Route, Body("WELCOME"), Ct), HttpStatusCode.Conflict, "BILLING_NO_SUBSCRIPTION");
    }

    [Fact]
    public async Task ApplyCode_WhileAGiftIsInForce_Returns409()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await BillingTestData.StripePlanAsync(factory);
        await BillingTestData.SubscribeLiveAsync(factory, coach, plan, givenFreeAt: DateTimeOffset.UtcNow.AddDays(-1));

        await AssertErrorAsync(await coach.Http.PostAsJsonAsync(Route, Body("WELCOME"), Ct), HttpStatusCode.Conflict, "BILLING_GIFT_IN_FORCE");

        fake.PromotionCodeSubscriptions.Should().BeEmpty();
    }

    [Fact]
    public async Task ApplyCode_WhenStripeFails_Returns502()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await BillingTestData.StripePlanAsync(factory);
        await BillingTestData.SubscribeLiveAsync(factory, coach, plan);
        fake.Fail = true;

        await AssertErrorAsync(await coach.Http.PostAsJsonAsync(Route, Body("WELCOME"), Ct), HttpStatusCode.BadGateway, "BILLING_PROVIDER_ERROR");
    }

    [Fact]
    public async Task ApplyCode_WhenStripeIsNotConfigured_Returns503()
    {
        var fake = BillingTestData.ResetProvider(factory);
        fake.IsConfigured = false;
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);

        await AssertErrorAsync(await coach.Http.PostAsJsonAsync(Route, Body("WELCOME"), Ct), HttpStatusCode.ServiceUnavailable, "BILLING_NOT_CONFIGURED");
    }

    [Fact]
    public async Task ApplyCode_ByACoachWhoseRolesAreAllRemoved_Returns403()
    {
        BillingTestData.ResetProvider(factory);
        var coach = await BillingTestData.CoachWithAllRolesRemovedAsync(factory);

        await AssertErrorAsync(await coach.Http.PostAsJsonAsync(Route, Body("WELCOME"), Ct), HttpStatusCode.Forbidden, ErrorCodes.CoachRoleRemoved);
    }
}
