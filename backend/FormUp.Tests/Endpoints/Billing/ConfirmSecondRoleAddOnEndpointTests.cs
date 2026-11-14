using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.Billing.ConfirmSecondRoleAddOn;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;

namespace FormUp.Tests.Endpoints.Billing;

/// <summary>
/// Integration tests for <c>POST /billing/second-role/confirm</c> (<c>ConfirmSecondRoleAddOnEndpoint</c>) against the
/// fake payment provider.
/// </summary>
[Collection(TestCollection.Name)]
public class ConfirmSecondRoleAddOnEndpointTests(FitnessApiFactory factory) : IDisposable
{
    /// <summary>Leaves the shared fake provider in its defaults for the next test class.</summary>
    public void Dispose() => BillingTestData.ResetProvider(factory);

    private const string Route = "/billing/second-role/confirm";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Body(Guid? attemptId = null) => new { AttemptId = attemptId ?? Guid.NewGuid() };

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var raw = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(status, raw);
        raw.Should().Contain(code);
    }

    private async Task<(Actor Coach, string Plan, string SubscriptionId)> LiveCoachWithAddOnPlanAsync()
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var (plan, _) = await BillingTestData.StripePlanAsync(factory, price: 29900, addOn: 9900, bothRolesIncluded: false);
        var subscriptionId = await BillingTestData.SubscribeLiveAsync(factory, coach, plan);

        return (coach, plan, subscriptionId);
    }

    [Fact]
    public async Task Confirm_ChargesOnce_WithTheAttemptAsIdempotencyKey_AndMarksTheAddOnPaid()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var (coach, plan, subscriptionId) = await LiveCoachWithAddOnPlanAsync();
        var attemptId = Guid.NewGuid();
        var lastEvent = DateTimeOffset.UtcNow.AddHours(-1);
        await BillingTestData.SetLastExternalEventAsync(factory, coach.ProfileId, lastEvent);

        var response = await coach.Http.PostAsJsonAsync(Route, Body(attemptId), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<ConfirmSecondRoleAddOnResponse>(Ct))!.PaysSecondRoleAddOn.Should().BeTrue();
        var charge = fake.Charges.Should().ContainSingle().Subject;
        charge.SubscriptionId.Should().Be(subscriptionId);
        charge.PlanCode.Should().Be(plan);
        charge.AmountMinorUnits.Should().Be(9900);
        charge.IdempotencyKey.Should().Contain(attemptId.ToString("N"));
        var row = await BillingTestData.ReadRowAsync(factory, coach.ProfileId);
        row.PaysSecondRoleAddOn.Should().BeTrue();
        row.LastExternalEventAt.Should().BeCloseTo(lastEvent, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task Confirm_WhenTheAddOnIsAlreadyPaid_Returns409_WithoutChargingAgain()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var (plan, _) = await BillingTestData.StripePlanAsync(factory, addOn: 9900, bothRolesIncluded: false);
        await BillingTestData.SubscribeLiveAsync(factory, coach, plan, paysSecondRoleAddOn: true);

        await AssertErrorAsync(await coach.Http.PostAsJsonAsync(Route, Body(), Ct), HttpStatusCode.Conflict, "BILLING_ADD_ON_ALREADY_PAID");

        fake.Charges.Should().BeEmpty();
    }

    [Fact]
    public async Task Confirm_OnAPlanThatIncludesBothRoles_Returns409()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await BillingTestData.StripePlanAsync(factory, bothRolesIncluded: true);
        await BillingTestData.SubscribeLiveAsync(factory, coach, plan);

        await AssertErrorAsync(await coach.Http.PostAsJsonAsync(Route, Body(), Ct), HttpStatusCode.Conflict, "BILLING_ADD_ON_NOT_AVAILABLE");

        fake.Charges.Should().BeEmpty();
    }

    [Fact]
    public async Task Confirm_WhileAPlanChangeIsScheduled_Returns409()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var (coach, _, _) = await LiveCoachWithAddOnPlanAsync();
        var (other, _) = await BillingTestData.StripePlanAsync(factory, price: 9900);
        await BillingTestData.SetPendingPlanAsync(factory, coach.ProfileId, other);

        await AssertErrorAsync(await coach.Http.PostAsJsonAsync(Route, Body(), Ct), HttpStatusCode.Conflict, "BILLING_PLAN_CHANGE_PENDING");

        fake.Charges.Should().BeEmpty();
    }

    [Theory]
    [InlineData(AddOnChargeOutcome.Declined, "BILLING_PAYMENT_DECLINED")]
    [InlineData(AddOnChargeOutcome.AuthenticationRequired, "BILLING_PAYMENT_AUTHENTICATION_REQUIRED")]
    public async Task Confirm_WhenTheCardIsRefused_Returns402_AndAppliesNothing(AddOnChargeOutcome outcome, string code)
    {
        var fake = BillingTestData.ResetProvider(factory);
        fake.ChargeOutcome = outcome;
        var (coach, _, _) = await LiveCoachWithAddOnPlanAsync();

        await AssertErrorAsync(await coach.Http.PostAsJsonAsync(Route, Body(), Ct), HttpStatusCode.PaymentRequired, code);

        (await BillingTestData.ReadRowAsync(factory, coach.ProfileId)).PaysSecondRoleAddOn.Should().BeFalse();
    }

    [Fact]
    public async Task Confirm_WithoutAnAttemptId_Returns400_WithoutCharging()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var (coach, _, _) = await LiveCoachWithAddOnPlanAsync();

        var response = await coach.Http.PostAsJsonAsync(Route, new { AttemptId = Guid.Empty }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        fake.Charges.Should().BeEmpty();
    }

    [Fact]
    public async Task Confirm_WithoutALiveSubscription_Returns409()
    {
        BillingTestData.ResetProvider(factory);
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, "free");

        await AssertErrorAsync(await coach.Http.PostAsJsonAsync(Route, Body(), Ct), HttpStatusCode.Conflict, "BILLING_NO_SUBSCRIPTION");
    }

    [Fact]
    public async Task Confirm_WhileAGiftIsInForce_Returns409()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await BillingTestData.StripePlanAsync(factory, addOn: 9900, bothRolesIncluded: false);
        await BillingTestData.SubscribeLiveAsync(factory, coach, plan, givenFreeAt: DateTimeOffset.UtcNow.AddDays(-1));

        await AssertErrorAsync(await coach.Http.PostAsJsonAsync(Route, Body(), Ct), HttpStatusCode.Conflict, "BILLING_GIFT_IN_FORCE");

        fake.Charges.Should().BeEmpty();
    }

    [Fact]
    public async Task Confirm_WhenStripeFails_Returns502_AndSavesNothing()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var (coach, _, _) = await LiveCoachWithAddOnPlanAsync();
        fake.Fail = true;

        await AssertErrorAsync(await coach.Http.PostAsJsonAsync(Route, Body(), Ct), HttpStatusCode.BadGateway, "BILLING_PROVIDER_ERROR");

        (await BillingTestData.ReadRowAsync(factory, coach.ProfileId)).PaysSecondRoleAddOn.Should().BeFalse();
    }

    [Fact]
    public async Task Confirm_WhenStripeIsNotConfigured_Returns503()
    {
        var fake = BillingTestData.ResetProvider(factory);
        fake.IsConfigured = false;
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);

        await AssertErrorAsync(await coach.Http.PostAsJsonAsync(Route, Body(), Ct), HttpStatusCode.ServiceUnavailable, "BILLING_NOT_CONFIGURED");
    }

    [Fact]
    public async Task Confirm_ByACoachWhoseRolesAreAllRemoved_Returns403()
    {
        BillingTestData.ResetProvider(factory);
        var coach = await BillingTestData.CoachWithAllRolesRemovedAsync(factory);

        await AssertErrorAsync(await coach.Http.PostAsJsonAsync(Route, Body(), Ct), HttpStatusCode.Forbidden, ErrorCodes.CoachRoleRemoved);
    }
}
