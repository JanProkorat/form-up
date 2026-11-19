using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.Billing.PreviewPlanChange;
using FormUp.Application.Infrastructure.Services.Billing;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;

namespace FormUp.Tests.Endpoints.Billing;

/// <summary>
/// Integration tests for <c>POST /billing/change-plan/preview</c> (<c>PreviewPlanChangeEndpoint</c>) against the fake
/// payment provider.
/// </summary>
[Collection(TestCollection.Name)]
public class PreviewPlanChangeEndpointTests(FitnessApiFactory factory) : IDisposable
{
    /// <summary>Leaves the shared fake provider in its defaults for the next test class.</summary>
    public void Dispose() => BillingTestData.ResetProvider(factory);

    private const string Route = "/billing/change-plan/preview";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Body(string planCode) => new { PlanCode = planCode };

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var raw = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(status, raw);
        raw.Should().Contain(code);
    }

    [Fact]
    public async Task Preview_ForAnUpgrade_ReturnsTodaysChargeTheNextBillAndItsDate_AndChangesNothing()
    {
        var fake = BillingTestData.ResetProvider(factory);
        fake.PlanChangePreviewResult = new PlanChangePreview(4840, 840, "CZK");
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (current, _) = await BillingTestData.StripePlanAsync(factory, price: 29900);
        var (target, targetPriceId) = await BillingTestData.StripePlanAsync(factory, price: 59900);
        var periodEnd = DateTimeOffset.UtcNow.AddDays(20);
        var subscriptionId = await BillingTestData.SubscribeLiveAsync(factory, coach, current, currentPeriodEndsAt: periodEnd);

        var response = await coach.Http.PostAsJsonAsync(Route, Body(target), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<PreviewPlanChangeResponse>(BillingTestData.Json, Ct))!;
        body.PlanCode.Should().Be(target);
        body.Timing.Should().Be(PlanChangeTiming.Now);
        body.Currency.Should().Be("CZK");
        body.BillingInterval.Should().Be(BillingInterval.Monthly);
        body.CurrentBillingInterval.Should().Be(BillingInterval.Monthly);
        body.TodayAmountMinorUnits.Should().Be(4840);
        body.TodayTaxMinorUnits.Should().Be(840);
        body.NextAmountMinorUnits.Should().Be(59900);
        body.NextChargeAt.Should().BeCloseTo(periodEnd, TimeSpan.FromSeconds(1));
        body.CancelsScheduledSwitchToFree.Should().BeFalse();
        body.TaxMode.Should().Be(BillingTaxMode.None, "the test host leaves the tax mode at its default");
        var call = fake.PlanChangePreviews.Should().ContainSingle().Subject;
        call.SubscriptionId.Should().Be(subscriptionId);
        call.PlanPriceExternalId.Should().Be(targetPriceId);
        call.AddOnAmountMinorUnits.Should().BeNull();
        fake.PlanChanges.Should().BeEmpty("a preview changes nothing");
        (await BillingTestData.ReadRowAsync(factory, coach.ProfileId)).SubscriptionPlan.Code.Should().Be(current);
    }

    [Fact]
    public async Task Preview_FromAnAnnualSubscription_ReportsTheCurrentIntervalAndKeepsTheTargetMonthly()
    {
        BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (current, _) = await BillingTestData.StripePlanAsync(factory, price: 29900);
        await SubscriptionPlanPriceBuilder.AddOpenAnnualPriceAsync(factory, current, 299000);
        var (target, _) = await BillingTestData.StripePlanAsync(factory, price: 59900);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(
            factory, coach, current, currentPeriodEndsAt: DateTimeOffset.UtcNow.AddDays(20), interval: BillingInterval.Annual);
        await BillingTestData.UpdateRowAsync(factory, coach.ProfileId, row =>
        {
            row.ExternalSubscriptionId = $"sub_{Guid.NewGuid():N}";
            row.ExternalCustomerId = $"cus_{Guid.NewGuid():N}";
        });

        var response = await coach.Http.PostAsJsonAsync(Route, Body(target), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        var body = (await response.Content.ReadFromJsonAsync<PreviewPlanChangeResponse>(BillingTestData.Json, Ct))!;
        body.CurrentBillingInterval.Should().Be(BillingInterval.Annual);
        body.BillingInterval.Should().Be(BillingInterval.Monthly);
    }

    [Fact]
    public async Task Preview_ForAnUpgradeWhileASwitchToFreeIsScheduled_SaysTheSwitchIsDropped()
    {
        BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (current, _) = await BillingTestData.StripePlanAsync(factory, price: 29900);
        var (target, _) = await BillingTestData.StripePlanAsync(factory, price: 59900);
        await BillingTestData.SubscribeLiveAsync(factory, coach, current, cancelAtPeriodEnd: true);

        var response = await coach.Http.PostAsJsonAsync(Route, Body(target), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<PreviewPlanChangeResponse>(BillingTestData.Json, Ct))!;
        body.CancelsScheduledSwitchToFree.Should().BeTrue();
        (await BillingTestData.ReadRowAsync(factory, coach.ProfileId)).CancelAtPeriodEnd.Should().BeTrue("a preview changes nothing");
    }

    [Fact]
    public async Task Preview_ForADowngrade_ReturnsWhenItStarts_WithNothingDueToday_AndNoProviderCall()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (current, _) = await BillingTestData.StripePlanAsync(factory, price: 59900);
        var (target, _) = await BillingTestData.StripePlanAsync(factory, price: 29900);
        var periodEnd = DateTimeOffset.UtcNow.AddDays(7);
        await BillingTestData.SubscribeLiveAsync(factory, coach, current, cancelAtPeriodEnd: true, currentPeriodEndsAt: periodEnd);

        var response = await coach.Http.PostAsJsonAsync(Route, Body(target), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<PreviewPlanChangeResponse>(BillingTestData.Json, Ct))!;
        body.Timing.Should().Be(PlanChangeTiming.AtNextRenewal);
        body.TodayAmountMinorUnits.Should().Be(0);
        body.NextAmountMinorUnits.Should().Be(29900);
        body.NextChargeAt.Should().BeCloseTo(periodEnd, TimeSpan.FromSeconds(1));
        body.CancelsScheduledSwitchToFree.Should().BeFalse("a downgrade leaves a scheduled switch alone");
        fake.PlanChangePreviews.Should().BeEmpty();
    }

    [Fact]
    public async Task Preview_ForATwoRoleCoach_CarriesTheAddOnIntoTheNextBill()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        var (current, _) = await BillingTestData.StripePlanAsync(factory, price: 29900, bothRolesIncluded: true);
        var (target, _) = await BillingTestData.StripePlanAsync(factory, price: 59900, addOn: 14900, bothRolesIncluded: false);
        await BillingTestData.SubscribeLiveAsync(factory, coach, current);

        var response = await coach.Http.PostAsJsonAsync(Route, Body(target), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<PreviewPlanChangeResponse>(BillingTestData.Json, Ct))!;
        body.NextAmountMinorUnits.Should().Be(74800);
        fake.PlanChangePreviews.Should().ContainSingle().Which.AddOnAmountMinorUnits.Should().Be(14900);
    }

    [Fact]
    public async Task Preview_TwoRoleCoachToAOneRolePlanWithoutAnAddOnPrice_Returns400()
    {
        BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        var (current, _) = await BillingTestData.StripePlanAsync(factory, price: 59900, bothRolesIncluded: true);
        var (target, _) = await BillingTestData.StripePlanAsync(factory, price: 29900, bothRolesIncluded: false);
        await BillingTestData.SubscribeLiveAsync(factory, coach, current);

        await AssertErrorAsync(await coach.Http.PostAsJsonAsync(Route, Body(target), Ct), HttpStatusCode.BadRequest, "PLAN_NOT_PURCHASABLE");
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-such-plan")]
    [InlineData("free")]
    public async Task Preview_ForAPlanThatCannotBeBought_Returns400(string planCode)
    {
        BillingTestData.ResetProvider(factory);
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (current, _) = await BillingTestData.StripePlanAsync(factory);
        await BillingTestData.SubscribeLiveAsync(factory, coach, current);

        var response = await coach.Http.PostAsJsonAsync(Route, Body(planCode), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Preview_ForAnInactivePlan_Returns400()
    {
        BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (current, _) = await BillingTestData.StripePlanAsync(factory);
        var (target, _) = await BillingTestData.StripePlanAsync(factory, price: 59900);
        await BillingTestData.SubscribeLiveAsync(factory, coach, current);
        await BillingTestData.DeactivatePlanAsync(factory, target);

        await AssertErrorAsync(await coach.Http.PostAsJsonAsync(Route, Body(target), Ct), HttpStatusCode.BadRequest, "PLAN_NOT_PURCHASABLE");
    }

    [Fact]
    public async Task Preview_ForThePlanTheCoachIsOn_Returns409()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (current, _) = await BillingTestData.StripePlanAsync(factory);
        await BillingTestData.SubscribeLiveAsync(factory, coach, current);

        await AssertErrorAsync(await coach.Http.PostAsJsonAsync(Route, Body(current), Ct), HttpStatusCode.Conflict, "BILLING_PLAN_UNCHANGED");

        fake.PlanChangePreviews.Should().BeEmpty();
    }

    [Fact]
    public async Task Preview_WithoutALiveSubscription_Returns409()
    {
        BillingTestData.ResetProvider(factory);
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, "free");
        var (target, _) = await BillingTestData.StripePlanAsync(factory);

        await AssertErrorAsync(await coach.Http.PostAsJsonAsync(Route, Body(target), Ct), HttpStatusCode.Conflict, "BILLING_NO_SUBSCRIPTION");
    }

    [Fact]
    public async Task Preview_WhileAGiftIsInForce_Returns409()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (current, _) = await BillingTestData.StripePlanAsync(factory, price: 29900);
        var (target, _) = await BillingTestData.StripePlanAsync(factory, price: 59900);
        await BillingTestData.SubscribeLiveAsync(
            factory, coach, current, givenFreeAt: DateTimeOffset.UtcNow.AddDays(-1), freeUntil: DateTimeOffset.UtcNow.AddDays(20));

        await AssertErrorAsync(await coach.Http.PostAsJsonAsync(Route, Body(target), Ct), HttpStatusCode.Conflict, "BILLING_GIFT_IN_FORCE");

        fake.PlanChangePreviews.Should().BeEmpty();
    }

    [Fact]
    public async Task Preview_WhenStripeFails_Returns502()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (current, _) = await BillingTestData.StripePlanAsync(factory, price: 29900);
        var (target, _) = await BillingTestData.StripePlanAsync(factory, price: 59900);
        await BillingTestData.SubscribeLiveAsync(factory, coach, current);
        fake.Fail = true;

        await AssertErrorAsync(await coach.Http.PostAsJsonAsync(Route, Body(target), Ct), HttpStatusCode.BadGateway, "BILLING_PROVIDER_ERROR");
    }

    [Fact]
    public async Task Preview_WhenStripeIsNotConfigured_Returns503()
    {
        var fake = BillingTestData.ResetProvider(factory);
        fake.IsConfigured = false;
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);

        await AssertErrorAsync(await coach.Http.PostAsJsonAsync(Route, Body("anything"), Ct), HttpStatusCode.ServiceUnavailable, "BILLING_NOT_CONFIGURED");
    }

    [Fact]
    public async Task Preview_ByACoachWhoseRolesAreAllRemoved_Returns403()
    {
        BillingTestData.ResetProvider(factory);
        var coach = await BillingTestData.CoachWithAllRolesRemovedAsync(factory);

        await AssertErrorAsync(await coach.Http.PostAsJsonAsync(Route, Body("anything"), Ct), HttpStatusCode.Forbidden, ErrorCodes.CoachRoleRemoved);
    }
}
