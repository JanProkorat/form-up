using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Features.Billing.PreviewSecondRoleAddOn;
using FormUp.Application.Infrastructure.Services.Billing;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;

namespace FormUp.Tests.Endpoints.Billing;

/// <summary>
/// Integration tests for <c>POST /billing/second-role/preview</c> (<c>PreviewSecondRoleAddOnEndpoint</c>) against the
/// fake payment provider.
/// </summary>
[Collection(TestCollection.Name)]
public class PreviewSecondRoleAddOnEndpointTests(FitnessApiFactory factory) : IDisposable
{
    /// <summary>Leaves the shared fake provider in its defaults for the next test class.</summary>
    public void Dispose() => BillingTestData.ResetProvider(factory);

    private const string Route = "/billing/second-role/preview";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var raw = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(status, raw);
        raw.Should().Contain(code);
    }

    [Fact]
    public async Task Preview_ReturnsThePlanLine_TheAddOnLine_TheNewTotal_AndTodaysProratedCharge()
    {
        var fake = BillingTestData.ResetProvider(factory);
        fake.PreviewResult = new SecondRoleAddOnPreview(4840, 840, "CZK");
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var (plan, _) = await BillingTestData.StripePlanAsync(factory, price: 29900, addOn: 9900, bothRolesIncluded: false);
        var periodEnd = DateTimeOffset.UtcNow.AddDays(20);
        var subscriptionId = await BillingTestData.SubscribeLiveAsync(factory, coach, plan, currentPeriodEndsAt: periodEnd);

        var response = await coach.Http.PostAsJsonAsync(Route, new { }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<PreviewSecondRoleAddOnResponse>(BillingTestData.Json, Ct))!;
        body.PlanAmountMinorUnits.Should().Be(29900);
        body.AddOnAmountMinorUnits.Should().Be(9900);
        body.NewTotalMinorUnits.Should().Be(39800);
        body.BillingInterval.Should().Be(BillingInterval.Monthly);
        body.Currency.Should().Be("CZK");
        body.TodayAmountMinorUnits.Should().Be(4840);
        body.TodayTaxMinorUnits.Should().Be(840);
        body.PeriodEndsAt.Should().BeCloseTo(periodEnd, TimeSpan.FromSeconds(1));
        body.TaxMode.Should().Be(BillingTaxMode.None, "the test host leaves the tax mode at its default");
        fake.Previews.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new FakeSubscriptionService.AddOnCall(subscriptionId, plan, 9900, null));
        (await BillingTestData.ReadRowAsync(factory, coach.ProfileId)).PaysSecondRoleAddOn.Should().BeFalse("a preview changes nothing");
    }

    [Fact]
    public async Task Preview_OnAPlanThatIncludesBothRoles_Returns409()
    {
        BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await BillingTestData.StripePlanAsync(factory, bothRolesIncluded: true);
        await BillingTestData.SubscribeLiveAsync(factory, coach, plan);

        await AssertErrorAsync(await coach.Http.PostAsJsonAsync(Route, new { }, Ct), HttpStatusCode.Conflict, "BILLING_ADD_ON_NOT_AVAILABLE");
    }

    [Fact]
    public async Task Preview_WhenTheAddOnIsAlreadyPaid_Returns409()
    {
        BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await BillingTestData.StripePlanAsync(factory, addOn: 9900, bothRolesIncluded: false);
        await BillingTestData.SubscribeLiveAsync(factory, coach, plan, paysSecondRoleAddOn: true);

        await AssertErrorAsync(await coach.Http.PostAsJsonAsync(Route, new { }, Ct), HttpStatusCode.Conflict, "BILLING_ADD_ON_ALREADY_PAID");
    }

    [Fact]
    public async Task Preview_WithoutALiveSubscription_Returns409()
    {
        BillingTestData.ResetProvider(factory);
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, "free");

        await AssertErrorAsync(await coach.Http.PostAsJsonAsync(Route, new { }, Ct), HttpStatusCode.Conflict, "BILLING_NO_SUBSCRIPTION");
    }

    [Fact]
    public async Task Preview_WhileAGiftIsInForce_Returns409()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await BillingTestData.StripePlanAsync(factory, addOn: 9900, bothRolesIncluded: false);
        await BillingTestData.SubscribeLiveAsync(factory, coach, plan, givenFreeAt: DateTimeOffset.UtcNow.AddDays(-1));

        await AssertErrorAsync(await coach.Http.PostAsJsonAsync(Route, new { }, Ct), HttpStatusCode.Conflict, "BILLING_GIFT_IN_FORCE");

        fake.Previews.Should().BeEmpty();
    }

    [Fact]
    public async Task Preview_WhenStripeFails_Returns502()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await BillingTestData.StripePlanAsync(factory, addOn: 9900, bothRolesIncluded: false);
        await BillingTestData.SubscribeLiveAsync(factory, coach, plan);
        fake.Fail = true;

        await AssertErrorAsync(await coach.Http.PostAsJsonAsync(Route, new { }, Ct), HttpStatusCode.BadGateway, "BILLING_PROVIDER_ERROR");
    }

    [Fact]
    public async Task Preview_WhenStripeIsNotConfigured_Returns503()
    {
        var fake = BillingTestData.ResetProvider(factory);
        fake.IsConfigured = false;
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);

        await AssertErrorAsync(await coach.Http.PostAsJsonAsync(Route, new { }, Ct), HttpStatusCode.ServiceUnavailable, "BILLING_NOT_CONFIGURED");
    }

    [Fact]
    public async Task Preview_ByACoachWhoseRolesAreAllRemoved_Returns403()
    {
        BillingTestData.ResetProvider(factory);
        var coach = await BillingTestData.CoachWithAllRolesRemovedAsync(factory);

        await AssertErrorAsync(await coach.Http.PostAsJsonAsync(Route, new { }, Ct), HttpStatusCode.Forbidden, ErrorCodes.CoachRoleRemoved);
    }
}
