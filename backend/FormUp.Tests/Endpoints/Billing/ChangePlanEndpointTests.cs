using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.Billing.ChangePlan;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;

namespace FormUp.Tests.Endpoints.Billing;

/// <summary>
/// Integration tests for <c>POST /billing/change-plan</c> (<c>ChangePlanEndpoint</c>) against the fake payment provider.
/// </summary>
[Collection(TestCollection.Name)]
public class ChangePlanEndpointTests(FitnessApiFactory factory) : IDisposable
{
    /// <summary>Leaves the shared fake provider in its defaults for the next test class.</summary>
    public void Dispose() => BillingTestData.ResetProvider(factory);

    private const string Route = "/billing/change-plan";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Body(string planCode) => new { PlanCode = planCode };

    private async Task<string> AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var raw = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(status, raw);
        raw.Should().Contain(code);

        return raw;
    }

    [Fact]
    public async Task ChangePlan_ToADearerPlan_AppliesNow_AndWritesOurRowWithoutTouchingTheLastEvent()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (current, _) = await BillingTestData.StripePlanAsync(factory, price: 29900);
        var (target, targetPriceId) = await BillingTestData.StripePlanAsync(factory, price: 59900);
        var subscriptionId = await BillingTestData.SubscribeLiveAsync(factory, coach, current);
        var lastEvent = DateTimeOffset.UtcNow.AddHours(-3);
        await BillingTestData.SetLastExternalEventAsync(factory, coach.ProfileId, lastEvent);

        var response = await coach.Http.PostAsJsonAsync(Route, Body(target), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<ChangePlanResponse>(BillingTestData.Json, Ct))!;
        body.PlanCode.Should().Be(target);
        body.Timing.Should().Be(PlanChangeTiming.Now);
        fake.PlanChanges.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new FakeSubscriptionService.ChangePlanCall(subscriptionId, target, targetPriceId, AtNextRenewal: false));
        var row = await BillingTestData.ReadRowAsync(factory, coach.ProfileId);
        row.SubscriptionPlan.Code.Should().Be(target);
        row.PendingSubscriptionPlanPriceId.Should().BeNull();
        row.LastExternalEventAt.Should().BeCloseTo(lastEvent, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task ChangePlan_ToACheaperPlan_IsScheduledForRenewal_AndKeepsTheCurrentPlan()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (current, _) = await BillingTestData.StripePlanAsync(factory, price: 59900);
        var (target, _) = await BillingTestData.StripePlanAsync(factory, price: 29900);
        await BillingTestData.SubscribeLiveAsync(factory, coach, current);

        var response = await coach.Http.PostAsJsonAsync(Route, Body(target), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<ChangePlanResponse>(BillingTestData.Json, Ct))!;
        body.Timing.Should().Be(PlanChangeTiming.AtNextRenewal);
        body.EffectiveAt.Should().NotBeNull("the period end is when the cheaper plan starts");
        fake.PlanChanges.Should().ContainSingle().Which.AtNextRenewal.Should().BeTrue();
        var row = await BillingTestData.ReadRowAsync(factory, coach.ProfileId);
        row.SubscriptionPlan.Code.Should().Be(current, "the paid plan stays until the renewal");
        row.PendingSubscriptionPlanPriceId.Should().NotBeNull();
    }

    [Fact]
    public async Task ChangePlan_WithAPaidAddOn_CarriesTheTargetAddOnAmount()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var (current, _) = await BillingTestData.StripePlanAsync(factory, price: 29900, addOn: 9900, bothRolesIncluded: false);
        var (target, _) = await BillingTestData.StripePlanAsync(factory, price: 59900, addOn: 14900, bothRolesIncluded: false);
        await BillingTestData.SubscribeLiveAsync(factory, coach, current, paysSecondRoleAddOn: true);

        var response = await coach.Http.PostAsJsonAsync(Route, Body(target), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.PlanChanges.Should().ContainSingle().Which.AddOnAmountMinorUnits.Should().Be(14900);
        (await BillingTestData.ReadRowAsync(factory, coach.ProfileId)).PaysSecondRoleAddOn.Should().BeTrue();
    }

    [Fact]
    public async Task ChangePlan_ToAPlanThatIncludesBothRoles_DropsTheAddOn()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var (current, _) = await BillingTestData.StripePlanAsync(factory, price: 29900, addOn: 9900, bothRolesIncluded: false);
        var (target, _) = await BillingTestData.StripePlanAsync(factory, price: 59900, bothRolesIncluded: true);
        await BillingTestData.SubscribeLiveAsync(factory, coach, current, paysSecondRoleAddOn: true);

        var response = await coach.Http.PostAsJsonAsync(Route, Body(target), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.PlanChanges.Should().ContainSingle().Which.AddOnAmountMinorUnits.Should().BeNull();
        (await BillingTestData.ReadRowAsync(factory, coach.ProfileId)).PaysSecondRoleAddOn.Should().BeFalse();
    }

    [Fact]
    public async Task ChangePlan_ToThePlanTheCoachIsOn_Returns409()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (current, _) = await BillingTestData.StripePlanAsync(factory);
        await BillingTestData.SubscribeLiveAsync(factory, coach, current);

        await AssertErrorAsync(await coach.Http.PostAsJsonAsync(Route, Body(current), Ct), HttpStatusCode.Conflict, "BILLING_PLAN_UNCHANGED");

        fake.PlanChanges.Should().BeEmpty();
    }

    [Fact]
    public async Task ChangePlan_WithoutALiveSubscription_Returns409_SoTheClientUsesCheckout()
    {
        var fake = BillingTestData.ResetProvider(factory);
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, "free");
        var (target, _) = await BillingTestData.StripePlanAsync(factory);

        await AssertErrorAsync(await coach.Http.PostAsJsonAsync(Route, Body(target), Ct), HttpStatusCode.Conflict, "BILLING_NO_SUBSCRIPTION");

        fake.PlanChanges.Should().BeEmpty();
    }

    [Fact]
    public async Task ChangePlan_WhileAGiftIsInForce_Returns409()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (current, _) = await BillingTestData.StripePlanAsync(factory, price: 29900);
        var (target, _) = await BillingTestData.StripePlanAsync(factory, price: 59900);
        await BillingTestData.SubscribeLiveAsync(
            factory, coach, current, givenFreeAt: DateTimeOffset.UtcNow.AddDays(-1), freeUntil: DateTimeOffset.UtcNow.AddDays(20));

        await AssertErrorAsync(await coach.Http.PostAsJsonAsync(Route, Body(target), Ct), HttpStatusCode.Conflict, "BILLING_GIFT_IN_FORCE");

        fake.PlanChanges.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-such-plan")]
    [InlineData("free")]
    public async Task ChangePlan_ToAPlanThatCannotBeBought_Returns400(string planCode)
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
    public async Task ChangePlan_ToAnInactivePlan_Returns400()
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
    public async Task ChangePlan_WhenStripeFails_Returns502_AndSavesNothing()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (current, _) = await BillingTestData.StripePlanAsync(factory, price: 29900);
        var (target, _) = await BillingTestData.StripePlanAsync(factory, price: 59900);
        await BillingTestData.SubscribeLiveAsync(factory, coach, current);
        fake.FailPlanChanges = true;

        await AssertErrorAsync(await coach.Http.PostAsJsonAsync(Route, Body(target), Ct), HttpStatusCode.BadGateway, "BILLING_PROVIDER_ERROR");

        (await BillingTestData.ReadRowAsync(factory, coach.ProfileId)).SubscriptionPlan.Code.Should().Be(current);
    }

    [Fact]
    public async Task ChangePlan_WhenStripeIsNotConfigured_Returns503()
    {
        var fake = BillingTestData.ResetProvider(factory);
        fake.IsConfigured = false;
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);

        await AssertErrorAsync(await coach.Http.PostAsJsonAsync(Route, Body("anything"), Ct), HttpStatusCode.ServiceUnavailable, "BILLING_NOT_CONFIGURED");
    }

    [Fact]
    public async Task ChangePlan_ByACoachWhoseRolesAreAllRemoved_Returns403()
    {
        BillingTestData.ResetProvider(factory);
        var coach = await BillingTestData.CoachWithAllRolesRemovedAsync(factory);

        await AssertErrorAsync(await coach.Http.PostAsJsonAsync(Route, Body("anything"), Ct), HttpStatusCode.Forbidden, ErrorCodes.CoachRoleRemoved);
    }
}
