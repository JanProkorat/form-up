using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.Billing.CancelScheduledPlanChange;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;

namespace FormUp.Tests.Endpoints.Billing;

/// <summary>
/// Integration tests for <c>POST /billing/scheduled-change/cancel</c> (<c>CancelScheduledPlanChangeEndpoint</c>) against
/// the fake payment provider.
/// </summary>
[Collection(TestCollection.Shard4)]
public class CancelScheduledPlanChangeEndpointTests(FitnessApiFactory factory) : IDisposable
{
    /// <summary>Leaves the shared fake provider in its defaults for the next test class.</summary>
    public void Dispose() => BillingTestData.ResetProvider(factory);

    private const string Route = "/billing/scheduled-change/cancel";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var raw = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(status, raw);
        raw.Should().Contain(code);
    }

    [Fact]
    public async Task Cancel_ScheduledDowngrade_ClearsTheScheduleAtStripe_AndDropsThePendingPrice()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await BillingTestData.StripePlanAsync(factory);
        var (cheaperPlan, _) = await BillingTestData.StripePlanAsync(factory, price: 9900);
        var periodEnd = DateTimeOffset.UtcNow.AddDays(12);
        var subscriptionId = await BillingTestData.SubscribeLiveAsync(factory, coach, plan, currentPeriodEndsAt: periodEnd);
        await BillingTestData.SetPendingPlanAsync(factory, coach.ProfileId, cheaperPlan);

        var response = await coach.Http.PostAsync(Route, null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<CancelScheduledPlanChangeResponse>(BillingTestData.Json, Ct))!;
        body.Kind.Should().Be(ScheduledPlanChangeKind.Downgrade);
        body.PlanCode.Should().Be(plan);
        body.RenewsAt.Should().BeCloseTo(periodEnd, TimeSpan.FromSeconds(1));
        fake.ClearedScheduledChanges.Should().ContainSingle().Which.Should().Be(subscriptionId);
        fake.PlanChanges.Should().BeEmpty();
        (await BillingTestData.ReadRowAsync(factory, coach.ProfileId)).PendingSubscriptionPlanPriceId.Should().BeNull();
    }

    [Fact]
    public async Task Cancel_ScheduledDowngradeWithAScheduledAddOnRemoval_RewritesTheScheduleInsteadOfClearingIt()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, priceId) = await BillingTestData.StripePlanAsync(factory, addOn: 4900, bothRolesIncluded: false);
        var (cheaperPlan, _) = await BillingTestData.StripePlanAsync(factory, price: 9900);
        var subscriptionId = await BillingTestData.SubscribeLiveAsync(factory, coach, plan, paysSecondRoleAddOn: true);
        await BillingTestData.SetPendingPlanAsync(factory, coach.ProfileId, cheaperPlan);
        var addOnEndsAt = DateTimeOffset.UtcNow.AddDays(10);
        await BillingTestData.UpdateRowAsync(factory, coach.ProfileId, row => row.SecondRoleAddOnEndsAt = addOnEndsAt);

        var response = await coach.Http.PostAsync(Route, null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.ClearedScheduledChanges.Should().BeEmpty("a clear would drop the add-on removal");
        fake.PlanChanges.Should().ContainSingle().Which.Should().Be(
            new FakeSubscriptionService.ChangePlanCall(subscriptionId, plan, priceId, true));
        var row = await BillingTestData.ReadRowAsync(factory, coach.ProfileId);
        row.PendingSubscriptionPlanPriceId.Should().BeNull();
        row.SecondRoleAddOnEndsAt.Should().BeCloseTo(addOnEndsAt, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task Cancel_OwnSwitchToFree_ResumesTheRenewal_AndClearsTheFlag()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await BillingTestData.StripePlanAsync(factory);
        var subscriptionId = await BillingTestData.SubscribeLiveAsync(factory, coach, plan, cancelAtPeriodEnd: true);

        var response = await coach.Http.PostAsync(Route, null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<CancelScheduledPlanChangeResponse>(BillingTestData.Json, Ct))!;
        body.Kind.Should().Be(ScheduledPlanChangeKind.SwitchToFree);
        body.PlanCode.Should().Be(plan);
        fake.ResumedRenewals.Should().ContainSingle().Which.Should().Be(subscriptionId);
        fake.PlanChanges.Should().BeEmpty();
        (await BillingTestData.ReadRowAsync(factory, coach.ProfileId)).CancelAtPeriodEnd.Should().BeFalse();
    }

    [Fact]
    public async Task Cancel_OwnSwitchToFreeAfterARoleWasRemoved_SchedulesTheAddOnRemoval_AndSetsTheMarker()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist)
            .WithRemovedCoachRoles(UserRole.Nutritionist)
            .CreateAsync(Ct);
        var (plan, priceId) = await BillingTestData.StripePlanAsync(factory, addOn: 4900, bothRolesIncluded: false);
        var periodEnd = DateTimeOffset.UtcNow.AddDays(12);
        var subscriptionId = await BillingTestData.SubscribeLiveAsync(
            factory, coach, plan, paysSecondRoleAddOn: true, cancelAtPeriodEnd: true, currentPeriodEndsAt: periodEnd);

        var response = await coach.Http.PostAsync(Route, null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.ResumedRenewals.Should().ContainSingle();
        fake.PlanChanges.Should().ContainSingle().Which.Should().Be(
            new FakeSubscriptionService.ChangePlanCall(subscriptionId, plan, priceId, true));
        var row = await BillingTestData.ReadRowAsync(factory, coach.ProfileId);
        row.CancelAtPeriodEnd.Should().BeFalse();
        row.SecondRoleAddOnEndsAt.Should().BeCloseTo(periodEnd, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Cancel_OwnSwitchToFreeAfterARoleWasRemovedWhenTheAddOnRemovalFails_UndoesTheResume_Returns502_AndKeepsTheRow()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist)
            .WithRemovedCoachRoles(UserRole.Nutritionist)
            .CreateAsync(Ct);
        var (plan, _) = await BillingTestData.StripePlanAsync(factory, addOn: 4900, bothRolesIncluded: false);
        var subscriptionId = await BillingTestData.SubscribeLiveAsync(
            factory, coach, plan, paysSecondRoleAddOn: true, cancelAtPeriodEnd: true);
        fake.FailPlanChanges = true;

        await AssertErrorAsync(await coach.Http.PostAsync(Route, null, Ct), HttpStatusCode.BadGateway, "BILLING_PROVIDER_ERROR");

        fake.ResumedRenewals.Should().ContainSingle();
        fake.CancelledAtPeriodEnd.Should().ContainSingle().Which.Should().Be(subscriptionId);
        var row = await BillingTestData.ReadRowAsync(factory, coach.ProfileId);
        row.CancelAtPeriodEnd.Should().BeTrue();
        row.SecondRoleAddOnEndsAt.Should().BeNull();
    }

    [Fact]
    public async Task Cancel_RepeatedAfterASuccess_Returns409NothingScheduled()
    {
        BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await BillingTestData.StripePlanAsync(factory);
        await BillingTestData.SubscribeLiveAsync(factory, coach, plan, cancelAtPeriodEnd: true);

        (await coach.Http.PostAsync(Route, null, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        await AssertErrorAsync(await coach.Http.PostAsync(Route, null, Ct), HttpStatusCode.Conflict, "BILLING_NOTHING_SCHEDULED");
    }

    [Fact]
    public async Task Cancel_WithNothingScheduled_Returns409_AndCallsNoStripe()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await BillingTestData.StripePlanAsync(factory);
        await BillingTestData.SubscribeLiveAsync(factory, coach, plan);

        await AssertErrorAsync(await coach.Http.PostAsync(Route, null, Ct), HttpStatusCode.Conflict, "BILLING_NOTHING_SCHEDULED");

        fake.ClearedScheduledChanges.Should().BeEmpty();
        fake.ResumedRenewals.Should().BeEmpty();
        fake.PlanChanges.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancel_WhileTheCoachAccountIsBeingDisabled_Returns409_AndCallsNoStripe()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await BillingTestData.StripePlanAsync(factory);
        await BillingTestData.SubscribeLiveAsync(factory, coach, plan, cancelAtPeriodEnd: true);
        await BillingTestData.SetDisablingAsync(factory, coach.ProfileId);

        await AssertErrorAsync(await coach.Http.PostAsync(Route, null, Ct), HttpStatusCode.Conflict, "BILLING_ACCOUNT_DISABLING");

        fake.ResumedRenewals.Should().BeEmpty();
        (await BillingTestData.ReadRowAsync(factory, coach.ProfileId)).CancelAtPeriodEnd.Should().BeTrue();
    }

    [Fact]
    public async Task Cancel_WhenAnAdminCancelledTheRenewal_Returns409_CallsNoStripe_AndKeepsTheFlag()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await BillingTestData.StripePlanAsync(factory);
        await BillingTestData.SubscribeLiveAsync(factory, coach, plan, cancelAtPeriodEnd: true);
        await BillingTestData.UpdateRowAsync(
            factory, coach.ProfileId, row => row.RenewalCancelledByAdminAt = DateTimeOffset.UtcNow);

        await AssertErrorAsync(
            await coach.Http.PostAsync(Route, null, Ct), HttpStatusCode.Conflict, "BILLING_RENEWAL_CANCELLED_BY_ADMIN");

        fake.ResumedRenewals.Should().BeEmpty();
        (await BillingTestData.ReadRowAsync(factory, coach.ProfileId)).CancelAtPeriodEnd.Should().BeTrue();
    }

    [Fact]
    public async Task Cancel_WhileAGiftIsInForce_Returns409()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await BillingTestData.StripePlanAsync(factory);
        await BillingTestData.SubscribeLiveAsync(
            factory, coach, plan, givenFreeAt: DateTimeOffset.UtcNow.AddDays(-1), cancelAtPeriodEnd: true);

        await AssertErrorAsync(await coach.Http.PostAsync(Route, null, Ct), HttpStatusCode.Conflict, "BILLING_GIFT_IN_FORCE");

        fake.ResumedRenewals.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancel_WithoutALiveSubscription_Returns409()
    {
        BillingTestData.ResetProvider(factory);
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, "free");

        await AssertErrorAsync(await coach.Http.PostAsync(Route, null, Ct), HttpStatusCode.Conflict, "BILLING_NO_SUBSCRIPTION");
    }

    [Fact]
    public async Task Cancel_WhenStripeFailsOnADowngrade_Returns502_AndKeepsThePendingPrice()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await BillingTestData.StripePlanAsync(factory);
        var (cheaperPlan, _) = await BillingTestData.StripePlanAsync(factory, price: 9900);
        await BillingTestData.SubscribeLiveAsync(factory, coach, plan);
        await BillingTestData.SetPendingPlanAsync(factory, coach.ProfileId, cheaperPlan);
        fake.FailClearScheduledChange = true;

        await AssertErrorAsync(await coach.Http.PostAsync(Route, null, Ct), HttpStatusCode.BadGateway, "BILLING_PROVIDER_ERROR");

        (await BillingTestData.ReadRowAsync(factory, coach.ProfileId)).PendingSubscriptionPlanPriceId.Should().NotBeNull();
    }

    [Fact]
    public async Task Cancel_WhenStripeFailsOnASwitchToFree_Returns502_AndKeepsTheFlag()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await BillingTestData.StripePlanAsync(factory);
        await BillingTestData.SubscribeLiveAsync(factory, coach, plan, cancelAtPeriodEnd: true);
        fake.FailResumeRenewal = true;

        await AssertErrorAsync(await coach.Http.PostAsync(Route, null, Ct), HttpStatusCode.BadGateway, "BILLING_PROVIDER_ERROR");

        (await BillingTestData.ReadRowAsync(factory, coach.ProfileId)).CancelAtPeriodEnd.Should().BeTrue();
    }

    [Fact]
    public async Task Cancel_WhenStripeIsNotConfigured_Returns503()
    {
        var fake = BillingTestData.ResetProvider(factory);
        fake.IsConfigured = false;
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);

        await AssertErrorAsync(await coach.Http.PostAsync(Route, null, Ct), HttpStatusCode.ServiceUnavailable, "BILLING_NOT_CONFIGURED");
    }

    [Fact]
    public async Task Cancel_ByACoachWhoseRolesAreAllRemoved_Returns403()
    {
        BillingTestData.ResetProvider(factory);
        var coach = await BillingTestData.CoachWithAllRolesRemovedAsync(factory);

        await AssertErrorAsync(await coach.Http.PostAsync(Route, null, Ct), HttpStatusCode.Forbidden, ErrorCodes.CoachRoleRemoved);
    }
}
