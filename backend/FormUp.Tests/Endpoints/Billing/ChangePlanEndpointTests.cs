using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Features.Billing.ChangePlan;
using FormUp.Tests.Builders;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Services.Billing;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

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

    private static object Body(string planCode, Guid? attemptId = null) => new { PlanCode = planCode, AttemptId = attemptId ?? Guid.NewGuid() };

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

        var attemptId = Guid.NewGuid();

        var response = await coach.Http.PostAsJsonAsync(Route, Body(target, attemptId), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<ChangePlanResponse>(BillingTestData.Json, Ct))!;
        body.PlanCode.Should().Be(target);
        body.Timing.Should().Be(PlanChangeTiming.Now);
        fake.PlanChanges.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new FakeSubscriptionService.ChangePlanCall(
                subscriptionId,
                target,
                targetPriceId,
                AtNextRenewal: false,
                IdempotencyKey: $"formup-plan-upgrade-{subscriptionId}-{targetPriceId}-{attemptId:N}"));
        var row = await BillingTestData.ReadRowAsync(factory, coach.ProfileId);
        row.SubscriptionPlan.Code.Should().Be(target);
        row.PendingSubscriptionPlanPriceId.Should().BeNull();
        row.LastExternalEventAt.Should().BeCloseTo(lastEvent, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task ChangePlan_WhenTheCardNeedsAuthentication_Returns200AwaitingPayment_WithTheUrl_AndLeavesTheRowAlone()
    {
        var fake = BillingTestData.ResetProvider(factory);
        fake.UpgradeOutcome = PlanUpgradeOutcome.AwaitingPayment("https://invoice.stripe.test/i/in_1");
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (current, _) = await BillingTestData.StripePlanAsync(factory, price: 29900);
        var (target, _) = await BillingTestData.StripePlanAsync(factory, price: 59900);
        var (scheduled, _) = await BillingTestData.StripePlanAsync(factory, price: 9900);
        await BillingTestData.SubscribeLiveAsync(factory, coach, current);
        await BillingTestData.SetPendingPlanAsync(factory, coach.ProfileId, scheduled);
        var scheduledRowPriceId = (await BillingTestData.ReadRowAsync(factory, coach.ProfileId)).PendingSubscriptionPlanPriceId;
        scheduledRowPriceId.Should().NotBeNull();
        var lastEvent = DateTimeOffset.UtcNow.AddHours(-3);
        await BillingTestData.SetLastExternalEventAsync(factory, coach.ProfileId, lastEvent);

        var response = await coach.Http.PostAsJsonAsync(Route, Body(target), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<ChangePlanResponse>(BillingTestData.Json, Ct))!;
        body.Timing.Should().Be(PlanChangeTiming.AwaitingPayment);
        body.PaymentUrl.Should().Be("https://invoice.stripe.test/i/in_1");
        body.EffectiveAt.Should().BeNull();
        var row = await BillingTestData.ReadRowAsync(factory, coach.ProfileId);
        row.SubscriptionPlan.Code.Should().Be(current, "Stripe applies the plan when the coach pays; the webhook then writes the row");
        row.PendingSubscriptionPlanPriceId.Should().Be(scheduledRowPriceId, "a scheduled downgrade survives an unpaid upgrade");
        row.LastExternalEventAt.Should().BeCloseTo(lastEvent, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task ChangePlan_WhenTheUpgradeIsApplied_HasNoPaymentUrl()
    {
        BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (current, _) = await BillingTestData.StripePlanAsync(factory, price: 29900);
        var (target, _) = await BillingTestData.StripePlanAsync(factory, price: 59900);
        await BillingTestData.SubscribeLiveAsync(factory, coach, current);

        var response = await coach.Http.PostAsJsonAsync(Route, Body(target), Ct);

        var body = (await response.Content.ReadFromJsonAsync<ChangePlanResponse>(BillingTestData.Json, Ct))!;
        body.Timing.Should().Be(PlanChangeTiming.Now);
        body.PaymentUrl.Should().BeNull();
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
        var call = fake.PlanChanges.Should().ContainSingle().Subject;
        call.AtNextRenewal.Should().BeTrue();
        call.IdempotencyKey.Should().BeNull("a scheduled change charges nothing");
        var row = await BillingTestData.ReadRowAsync(factory, coach.ProfileId);
        row.SubscriptionPlan.Code.Should().Be(current, "the paid plan stays until the renewal");
        row.PendingSubscriptionPlanPriceId.Should().NotBeNull();
    }

    [Fact]
    public async Task ChangePlan_UpgradeWhileASwitchToFreeIsScheduled_DropsTheSwitch()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (current, _) = await BillingTestData.StripePlanAsync(factory, price: 29900);
        var (target, _) = await BillingTestData.StripePlanAsync(factory, price: 59900);
        await BillingTestData.SubscribeLiveAsync(factory, coach, current, cancelAtPeriodEnd: true);

        var response = await coach.Http.PostAsJsonAsync(Route, Body(target), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.PlanChanges.Should().ContainSingle().Which.AtNextRenewal.Should().BeFalse();
        var row = await BillingTestData.ReadRowAsync(factory, coach.ProfileId);
        row.SubscriptionPlan.Code.Should().Be(target);
        row.CancelAtPeriodEnd.Should().BeFalse("the upgrade cancels the scheduled switch to Free");
    }

    [Fact]
    public async Task ChangePlan_UpgradeOverARenewalCancelledByAnAdmin_ClearsTheAdminMarker()
    {
        BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (current, _) = await BillingTestData.StripePlanAsync(factory, price: 29900);
        var (target, _) = await BillingTestData.StripePlanAsync(factory, price: 59900);
        await BillingTestData.SubscribeLiveAsync(factory, coach, current, cancelAtPeriodEnd: true);
        await BillingTestData.UpdateRowAsync(
            factory, coach.ProfileId, row => row.RenewalCancelledByAdminAt = DateTimeOffset.UtcNow);

        var response = await coach.Http.PostAsJsonAsync(Route, Body(target), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var row = await BillingTestData.ReadRowAsync(factory, coach.ProfileId);
        row.CancelAtPeriodEnd.Should().BeFalse();
        row.RenewalCancelledByAdminAt.Should().BeNull("a new purchase ends the admin cancellation");
    }

    [Fact]
    public async Task ChangePlan_UpgradeAfterTheSecondRoleWasRemoved_ClearsTheScheduledAddOnEnd()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (current, _) = await BillingTestData.StripePlanAsync(factory, price: 29900, addOn: 9900, bothRolesIncluded: false);
        var (target, _) = await BillingTestData.StripePlanAsync(factory, price: 59900, addOn: 14900, bothRolesIncluded: false);
        await BillingTestData.SubscribeLiveAsync(factory, coach, current, paysSecondRoleAddOn: true);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.CoachSubscriptions
                .Where(cs => cs.ProfessionalProfileId == coach.ProfileId)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(cs => cs.SecondRoleAddOnEndsAt, DateTimeOffset.UtcNow.AddDays(5)), Ct);
        }

        var response = await coach.Http.PostAsJsonAsync(Route, Body(target), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.PlanChanges.Should().ContainSingle().Which.AddOnAmountMinorUnits.Should().BeNull("a one-role coach carries no add-on");
        var row = await BillingTestData.ReadRowAsync(factory, coach.ProfileId);
        row.PaysSecondRoleAddOn.Should().BeFalse();
        row.SecondRoleAddOnEndsAt.Should().BeNull();
    }

    [Fact]
    public async Task ChangePlan_UpgradeWhoseChargeFails_Returns502_AndKeepsTheScheduledSwitchAndPlan()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (current, _) = await BillingTestData.StripePlanAsync(factory, price: 29900);
        var (target, _) = await BillingTestData.StripePlanAsync(factory, price: 59900);
        await BillingTestData.SubscribeLiveAsync(factory, coach, current, cancelAtPeriodEnd: true);
        fake.FailPlanChanges = true;

        await AssertErrorAsync(await coach.Http.PostAsJsonAsync(Route, Body(target), Ct), HttpStatusCode.BadGateway, "BILLING_PROVIDER_ERROR");

        var row = await BillingTestData.ReadRowAsync(factory, coach.ProfileId);
        row.SubscriptionPlan.Code.Should().Be(current);
        row.CancelAtPeriodEnd.Should().BeTrue();
        row.PendingSubscriptionPlanPriceId.Should().BeNull();
        row.PaysSecondRoleAddOn.Should().BeFalse();
    }

    [Fact]
    public async Task ChangePlan_WithoutAnAttemptId_Returns400_AndCallsNothing()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (current, _) = await BillingTestData.StripePlanAsync(factory, price: 29900);
        var (target, _) = await BillingTestData.StripePlanAsync(factory, price: 59900);
        await BillingTestData.SubscribeLiveAsync(factory, coach, current);

        var response = await coach.Http.PostAsJsonAsync(Route, new { PlanCode = target }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        fake.PlanChanges.Should().BeEmpty();
    }

    [Fact]
    public async Task ChangePlan_WithAPaidAddOn_CarriesTheTargetAddOnAmount()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
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
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        var (current, _) = await BillingTestData.StripePlanAsync(factory, price: 29900, addOn: 9900, bothRolesIncluded: false);
        var (target, _) = await BillingTestData.StripePlanAsync(factory, price: 59900, bothRolesIncluded: true);
        await BillingTestData.SubscribeLiveAsync(factory, coach, current, paysSecondRoleAddOn: true);

        var response = await coach.Http.PostAsJsonAsync(Route, Body(target), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.PlanChanges.Should().ContainSingle().Which.AddOnAmountMinorUnits.Should().BeNull();
        (await BillingTestData.ReadRowAsync(factory, coach.ProfileId)).PaysSecondRoleAddOn.Should().BeFalse();
    }

    [Fact]
    public async Task ChangePlan_TwoRoleCoachDowngradingFromABothRolesPlan_BillsTheAddOnAtRenewal_AndKeepsTheFlagFalse()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        var (current, _) = await BillingTestData.StripePlanAsync(factory, price: 59900, bothRolesIncluded: true);
        var (target, _) = await BillingTestData.StripePlanAsync(factory, price: 29900, addOn: 9900, bothRolesIncluded: false);
        await BillingTestData.SubscribeLiveAsync(factory, coach, current);

        var response = await coach.Http.PostAsJsonAsync(Route, Body(target), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<ChangePlanResponse>(BillingTestData.Json, Ct))!;
        body.Timing.Should().Be(PlanChangeTiming.AtNextRenewal);
        var call = fake.PlanChanges.Should().ContainSingle().Subject;
        call.AtNextRenewal.Should().BeTrue();
        call.AddOnAmountMinorUnits.Should().Be(9900);
        var row = await BillingTestData.ReadRowAsync(factory, coach.ProfileId);
        row.SubscriptionPlan.Code.Should().Be(current, "the both-roles plan stays until the renewal");
        row.PaysSecondRoleAddOn.Should().BeFalse("the webhook sets the flag when the renewal applies");
        row.PendingSubscriptionPlanPriceId.Should().NotBeNull();
    }

    [Fact]
    public async Task ChangePlan_TwoRoleCoachUpgradingFromABothRolesPlan_AppliesTheAddOnNow_AndSetsTheFlag()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        var (current, _) = await BillingTestData.StripePlanAsync(factory, price: 29900, bothRolesIncluded: true);
        var (target, _) = await BillingTestData.StripePlanAsync(factory, price: 59900, addOn: 14900, bothRolesIncluded: false);
        await BillingTestData.SubscribeLiveAsync(factory, coach, current);

        var response = await coach.Http.PostAsJsonAsync(Route, Body(target), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var call = fake.PlanChanges.Should().ContainSingle().Subject;
        call.AtNextRenewal.Should().BeFalse();
        call.AddOnAmountMinorUnits.Should().Be(14900);
        var row = await BillingTestData.ReadRowAsync(factory, coach.ProfileId);
        row.SubscriptionPlan.Code.Should().Be(target);
        row.PaysSecondRoleAddOn.Should().BeTrue();
    }

    [Fact]
    public async Task ChangePlan_ByACoachWithOneActiveRole_CarriesNoAddOn()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (current, _) = await BillingTestData.StripePlanAsync(factory, price: 29900, bothRolesIncluded: true);
        var (target, _) = await BillingTestData.StripePlanAsync(factory, price: 59900, addOn: 14900, bothRolesIncluded: false);
        await BillingTestData.SubscribeLiveAsync(factory, coach, current);

        var response = await coach.Http.PostAsJsonAsync(Route, Body(target), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.PlanChanges.Should().ContainSingle().Which.AddOnAmountMinorUnits.Should().BeNull();
        (await BillingTestData.ReadRowAsync(factory, coach.ProfileId)).PaysSecondRoleAddOn.Should().BeFalse();
    }

    [Fact]
    public async Task ChangePlan_ByACoachWhoRemovedASecondRoleButStillPaysTheAddOn_LosesTheAddOn()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist)
            .WithRemovedCoachRoles(UserRole.Nutritionist)
            .CreateAsync(Ct);
        var (current, _) = await BillingTestData.StripePlanAsync(factory, price: 29900, addOn: 9900, bothRolesIncluded: false);
        var (target, _) = await BillingTestData.StripePlanAsync(factory, price: 59900, addOn: 14900, bothRolesIncluded: false);
        await BillingTestData.SubscribeLiveAsync(factory, coach, current, paysSecondRoleAddOn: true);

        var response = await coach.Http.PostAsJsonAsync(Route, Body(target), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.PlanChanges.Should().ContainSingle().Which.AddOnAmountMinorUnits.Should().BeNull("only one role is still active");
        (await BillingTestData.ReadRowAsync(factory, coach.ProfileId)).PaysSecondRoleAddOn.Should().BeFalse();
    }

    [Fact]
    public async Task ChangePlan_TwoRoleCoachToAOneRolePlanWithoutAnAddOnPrice_Returns400_AndCallsNothing()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        var (current, _) = await BillingTestData.StripePlanAsync(factory, price: 59900, bothRolesIncluded: true);
        var (target, _) = await BillingTestData.StripePlanAsync(factory, price: 29900, bothRolesIncluded: false);
        await BillingTestData.SubscribeLiveAsync(factory, coach, current);

        await AssertErrorAsync(await coach.Http.PostAsJsonAsync(Route, Body(target), Ct), HttpStatusCode.BadRequest, "PLAN_NOT_PURCHASABLE");

        fake.PlanChanges.Should().BeEmpty();
        (await BillingTestData.ReadRowAsync(factory, coach.ProfileId)).PendingSubscriptionPlanPriceId.Should().BeNull();
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

    private static object BodyFor(string planCode, BillingInterval interval) =>
        new { PlanCode = planCode, BillingInterval = interval.ToString(), AttemptId = Guid.NewGuid() };

    private async Task<long> OpenPriceRowIdAsync(string planCode, BillingInterval interval)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.SubscriptionPlanPrices
            .Where(p => p.SubscriptionPlan.Code == planCode && p.ValidTo == null && p.BillingInterval == interval)
            .Select(p => p.Id)
            .SingleAsync(Ct);
    }

    private async Task<ChangePlanResponse> ReadBodyAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

        return (await response.Content.ReadFromJsonAsync<ChangePlanResponse>(BillingTestData.Json, Ct))!;
    }

    [Fact]
    public async Task ChangePlan_MonthlyToAnnualOnTheSamePlan_AppliesNow_WithTheAnnualPriceAndAddOn_AndDropsTheScheduledSwitch()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        var (plan, _, annualPriceId) = await BillingTestData.StripePlanWithAnnualAsync(
            factory, monthlyPrice: 29900, annualPrice: 299000, monthlyAddOn: 9900, annualAddOn: 99000, bothRolesIncluded: false);
        var (scheduled, _) = await BillingTestData.StripePlanAsync(factory, price: 9900, addOn: 1900, bothRolesIncluded: false);
        var subscriptionId = await BillingTestData.SubscribeLiveAsync(factory, coach, plan, paysSecondRoleAddOn: true);
        await BillingTestData.SetPendingPlanAsync(factory, coach.ProfileId, scheduled);

        var body = await ReadBodyAsync(await coach.Http.PostAsJsonAsync(Route, BodyFor(plan, BillingInterval.Annual), Ct));

        body.Timing.Should().Be(PlanChangeTiming.Now);
        var call = fake.PlanChanges.Should().ContainSingle().Subject;
        call.AtNextRenewal.Should().BeFalse();
        call.SubscriptionId.Should().Be(subscriptionId);
        call.PlanPriceExternalId.Should().Be(annualPriceId);
        call.AddOnAmountMinorUnits.Should().Be(99000, "the add-on is the annual one, per year");
        call.Interval.Should().Be(BillingInterval.Annual);
        var row = await BillingTestData.ReadRowAsync(factory, coach.ProfileId);
        row.SubscriptionPlanPrice.BillingInterval.Should().Be(BillingInterval.Annual);
        row.PaysSecondRoleAddOn.Should().BeTrue();
        row.PendingSubscriptionPlanPriceId.Should().BeNull("an immediate change drops the switch that was scheduled for the renewal");
    }

    private async Task<(Actor Coach, string Plan, Promotion Promotion)> CoachWithPromotionAsync(
        BillingInterval currentInterval, Action<Promotion>? configure = null)
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _, _) = await BillingTestData.StripePlanWithAnnualAsync(factory);
        await BillingTestData.SubscribeLiveAsync(factory, coach, plan, interval: currentInterval);
        var promotion = await PromotionBuilder.CreateAsync(factory, [plan], PromotionMode.Code, configure);
        await BillingTestData.UpdateRowAsync(factory, coach.ProfileId, row => row.CurrentPromotionId = promotion.Id);

        return (coach, plan, promotion);
    }

    [Fact]
    public async Task ChangePlan_MonthlyToAnnual_SwapsABothPeriodsPromotionToItsAnnualCoupon_InTheSameUpdate()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var (coach, plan, promotion) = await CoachWithPromotionAsync(
            BillingInterval.Monthly, p =>
            {
                p.Duration = PromotionDuration.Months;
                p.DurationMonths = 3;
                p.StripeAnnualCouponId = $"coupon_annual_{Guid.NewGuid():N}";
            });

        await ReadBodyAsync(await coach.Http.PostAsJsonAsync(Route, BodyFor(plan, BillingInterval.Annual), Ct));

        fake.PlanChanges.Should().ContainSingle().Which.Discount.Should().Be(SubscriptionDiscountChange.Replace(promotion.StripeAnnualCouponId!));
    }

    [Fact]
    public async Task ChangePlan_MonthlyToAnnual_DropsAMonthlyOnlyPromotion_InTheSameUpdate()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var (coach, plan, _) = await CoachWithPromotionAsync(BillingInterval.Monthly, p => p.BillingPeriod = PromotionBillingPeriod.Monthly);

        await ReadBodyAsync(await coach.Http.PostAsJsonAsync(Route, BodyFor(plan, BillingInterval.Annual), Ct));

        fake.PlanChanges.Should().ContainSingle().Which.Discount.Should().Be(SubscriptionDiscountChange.Clear);
    }

    [Fact]
    public async Task ChangePlan_MonthlyToAnnualWithoutAPromotion_LeavesTheDiscountAlone()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _, _) = await BillingTestData.StripePlanWithAnnualAsync(factory);
        await BillingTestData.SubscribeLiveAsync(factory, coach, plan);

        await ReadBodyAsync(await coach.Http.PostAsJsonAsync(Route, BodyFor(plan, BillingInterval.Annual), Ct));

        fake.PlanChanges.Should().ContainSingle().Which.Discount.Should().BeNull();
    }

    [Fact]
    public async Task ChangePlan_UpgradeAtTheSameInterval_LeavesThePromotionAlone()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var (coach, _, _) = await CoachWithPromotionAsync(BillingInterval.Monthly);
        var (dearer, _) = await BillingTestData.StripePlanAsync(factory, price: 59900);

        await ReadBodyAsync(await coach.Http.PostAsJsonAsync(Route, Body(dearer), Ct));

        fake.PlanChanges.Should().ContainSingle().Which.Discount.Should().BeNull("only a change of interval touches the discount");
    }

    [Theory]
    [InlineData(PromotionDuration.Forever, true)]
    [InlineData(PromotionDuration.Months, false)]
    public async Task ChangePlan_AnnualToMonthlyAtRenewal_CarriesOnlyAForeverPromotionIntoTheNewPhase(
        PromotionDuration duration, bool expectsCoupon)
    {
        var fake = BillingTestData.ResetProvider(factory);
        var (coach, plan, promotion) = await CoachWithPromotionAsync(
            BillingInterval.Annual, p =>
            {
                p.Duration = duration;
                p.DurationMonths = duration == PromotionDuration.Months ? 3 : null;
                p.StripeAnnualCouponId = $"coupon_annual_{Guid.NewGuid():N}";
            });

        await ReadBodyAsync(await coach.Http.PostAsJsonAsync(Route, BodyFor(plan, BillingInterval.Monthly), Ct));

        fake.PlanChanges.Should().ContainSingle().Which.PendingPhaseCouponId.Should().Be(expectsCoupon ? promotion.StripeCouponId : null);
    }

    [Fact]
    public async Task ChangePlan_AnnualToMonthlyAtRenewal_DoesNotCarryAnAnnualOnlyPromotionIntoTheMonthlyPhase()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var (coach, plan, _) = await CoachWithPromotionAsync(BillingInterval.Annual, p => p.BillingPeriod = PromotionBillingPeriod.Annual);

        await ReadBodyAsync(await coach.Http.PostAsJsonAsync(Route, BodyFor(plan, BillingInterval.Monthly), Ct));

        fake.PlanChanges.Should().ContainSingle().Which.PendingPhaseCouponId.Should().BeNull();
    }

    [Fact]
    public async Task ChangePlan_AnnualToMonthlyOnTheSamePlan_WaitsForRenewal_WithTheMonthlyPrice()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, monthlyPriceId, _) = await BillingTestData.StripePlanWithAnnualAsync(factory);
        var periodEnd = DateTimeOffset.UtcNow.AddDays(200);
        await BillingTestData.SubscribeLiveAsync(factory, coach, plan, currentPeriodEndsAt: periodEnd, interval: BillingInterval.Annual);

        var body = await ReadBodyAsync(await coach.Http.PostAsJsonAsync(Route, BodyFor(plan, BillingInterval.Monthly), Ct));

        body.Timing.Should().Be(PlanChangeTiming.AtNextRenewal);
        body.EffectiveAt.Should().BeCloseTo(periodEnd, TimeSpan.FromSeconds(1));
        var call = fake.PlanChanges.Should().ContainSingle().Subject;
        call.AtNextRenewal.Should().BeTrue();
        call.PlanPriceExternalId.Should().Be(monthlyPriceId);
        call.Interval.Should().Be(BillingInterval.Monthly);
        var row = await BillingTestData.ReadRowAsync(factory, coach.ProfileId);
        row.SubscriptionPlanPrice.BillingInterval.Should().Be(BillingInterval.Annual, "the year is paid for until it ends");
        row.PendingSubscriptionPlanPriceId.Should().Be(await OpenPriceRowIdAsync(plan, BillingInterval.Monthly));
    }

    [Fact]
    public async Task ChangePlan_AnnualToMonthlyOnADearerPlan_StillWaitsForRenewal()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (current, _, _) = await BillingTestData.StripePlanWithAnnualAsync(factory, monthlyPrice: 29900, annualPrice: 299000);
        var (target, _, _) = await BillingTestData.StripePlanWithAnnualAsync(factory, monthlyPrice: 99900, annualPrice: 999000);
        await BillingTestData.SubscribeLiveAsync(factory, coach, current, interval: BillingInterval.Annual);

        var body = await ReadBodyAsync(await coach.Http.PostAsJsonAsync(Route, BodyFor(target, BillingInterval.Monthly), Ct));

        body.Timing.Should().Be(PlanChangeTiming.AtNextRenewal);
        fake.PlanChanges.Should().ContainSingle().Which.AtNextRenewal.Should().BeTrue();
    }

    [Theory]
    [InlineData(399000, PlanChangeTiming.Now)]
    [InlineData(299000, PlanChangeTiming.AtNextRenewal)]
    [InlineData(199000, PlanChangeTiming.AtNextRenewal)]
    public async Task ChangePlan_AnnualToAnotherAnnualPlan_AppliesNowOnlyWhenStrictlyDearerPerYear(long targetAnnual, PlanChangeTiming expected)
    {
        BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (current, _, _) = await BillingTestData.StripePlanWithAnnualAsync(factory, annualPrice: 299000);
        var (target, _, _) = await BillingTestData.StripePlanWithAnnualAsync(factory, annualPrice: targetAnnual);
        await BillingTestData.SubscribeLiveAsync(factory, coach, current, interval: BillingInterval.Annual);

        var body = await ReadBodyAsync(await coach.Http.PostAsJsonAsync(Route, BodyFor(target, BillingInterval.Annual), Ct));

        body.Timing.Should().Be(expected);
    }

    [Theory]
    [InlineData(29900L, 299000L, 59900L, 599000L, PlanChangeTiming.Now)]
    [InlineData(29900L, 299000L, 29900L, 299000L, PlanChangeTiming.Now)]
    [InlineData(29900L, 299000L, 19900L, 199000L, PlanChangeTiming.AtNextRenewal)]
    [InlineData(59900L, null, 79900L, 700000L, PlanChangeTiming.Now)]
    [InlineData(59900L, null, 29900L, 900000L, PlanChangeTiming.AtNextRenewal)]
    public async Task ChangePlan_MonthlyToAnnualOnAnotherPlan_AppliesNowWhenTheTargetRanksTheSameOrHigherAtOneInterval(
        long currentMonthly, long? currentAnnual, long targetMonthly, long targetAnnual, PlanChangeTiming expected)
    {
        // Rows 4 and 5 have no annual row on the current plan, so the monthly rows are compared: the target annual price
        // against 12 times the current monthly one would give the opposite answer in both.
        BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        string current;

        if (currentAnnual is { } annual)
        {
            (current, _, _) = await BillingTestData.StripePlanWithAnnualAsync(factory, currentMonthly, annual);
        }
        else
        {
            (current, _) = await BillingTestData.StripePlanAsync(factory, price: currentMonthly);
        }

        var (target, _, _) = await BillingTestData.StripePlanWithAnnualAsync(factory, targetMonthly, targetAnnual);
        await BillingTestData.SubscribeLiveAsync(factory, coach, current);

        var body = await ReadBodyAsync(await coach.Http.PostAsJsonAsync(Route, BodyFor(target, BillingInterval.Annual), Ct));

        body.Timing.Should().Be(expected);
    }

    [Fact]
    public async Task ChangePlan_SameIntervalComparison_CountsTheAddOnTheCoachPaysNow()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        var (current, _) = await BillingTestData.StripePlanAsync(factory, price: 30000, addOn: 5000, bothRolesIncluded: false);
        var (target, _) = await BillingTestData.StripePlanAsync(factory, price: 33000, bothRolesIncluded: true);
        await BillingTestData.SubscribeLiveAsync(factory, coach, current, paysSecondRoleAddOn: true);

        var body = await ReadBodyAsync(await coach.Http.PostAsJsonAsync(Route, Body(target), Ct));

        body.Timing.Should().Be(PlanChangeTiming.AtNextRenewal, "33000 is below the 35000 the coach pays now, although the plan price alone is higher");
        fake.PlanChanges.Should().ContainSingle().Which.AtNextRenewal.Should().BeTrue();
    }

    [Fact]
    public async Task ChangePlan_SameIntervalComparison_CountsTheAddOnTheTargetWouldCarry()
    {
        BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        var (current, _) = await BillingTestData.StripePlanAsync(factory, price: 30000, bothRolesIncluded: true);
        var (target, _) = await BillingTestData.StripePlanAsync(factory, price: 28000, addOn: 5000, bothRolesIncluded: false);
        await BillingTestData.SubscribeLiveAsync(factory, coach, current);

        var body = await ReadBodyAsync(await coach.Http.PostAsJsonAsync(Route, Body(target), Ct));

        body.Timing.Should().Be(PlanChangeTiming.Now, "28000 plus the 5000 add-on is dearer than the 30000 now, although the plan price alone is lower");
    }

    [Fact]
    public async Task ChangePlan_ToThePlanAndIntervalTheCoachIsOnAnnually_Returns409()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _, _) = await BillingTestData.StripePlanWithAnnualAsync(factory);
        await BillingTestData.SubscribeLiveAsync(factory, coach, plan, interval: BillingInterval.Annual);

        await AssertErrorAsync(
            await coach.Http.PostAsJsonAsync(Route, BodyFor(plan, BillingInterval.Annual), Ct), HttpStatusCode.Conflict, "BILLING_PLAN_UNCHANGED");

        fake.PlanChanges.Should().BeEmpty();
    }

    [Fact]
    public async Task ChangePlan_ToAnIntervalThePlanHasNoOpenRowFor_Returns400_AndCallsNothing()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (current, _) = await BillingTestData.StripePlanAsync(factory, price: 29900);
        var (monthlyOnly, _) = await BillingTestData.StripePlanAsync(factory, price: 59900);
        await BillingTestData.SubscribeLiveAsync(factory, coach, current);

        await AssertErrorAsync(
            await coach.Http.PostAsJsonAsync(Route, BodyFor(monthlyOnly, BillingInterval.Annual), Ct), HttpStatusCode.BadRequest, "PLAN_NOT_PURCHASABLE");

        fake.PlanChanges.Should().BeEmpty();
    }

    [Fact]
    public async Task ChangePlan_TwoRoleCoachToAnAnnualRowWithoutAnAddOnPrice_Returns400_AndCallsNothing()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        var (current, _) = await BillingTestData.StripePlanAsync(factory, price: 29900, addOn: 9900, bothRolesIncluded: false);
        var (target, _, _) = await BillingTestData.StripePlanWithAnnualAsync(
            factory, monthlyPrice: 59900, annualPrice: 599000, monthlyAddOn: 9900, annualAddOn: null, bothRolesIncluded: false);
        await BillingTestData.SubscribeLiveAsync(factory, coach, current, paysSecondRoleAddOn: true);

        await AssertErrorAsync(
            await coach.Http.PostAsJsonAsync(Route, BodyFor(target, BillingInterval.Annual), Ct), HttpStatusCode.BadRequest, "PLAN_NOT_PURCHASABLE");

        fake.PlanChanges.Should().BeEmpty();
    }

    [Fact]
    public async Task ChangePlan_WithAnOutOfRangeInterval_Returns400_AndCallsNothing()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (current, _) = await BillingTestData.StripePlanAsync(factory, price: 29900);
        var (target, _) = await BillingTestData.StripePlanAsync(factory, price: 59900);
        await BillingTestData.SubscribeLiveAsync(factory, coach, current);

        var response = await coach.Http.PostAsJsonAsync(Route, new { PlanCode = target, BillingInterval = 99, AttemptId = Guid.NewGuid() }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        fake.PlanChanges.Should().BeEmpty();
    }

    [Fact]
    public async Task ChangePlan_DeferredChangeWhileAnotherSwitchIsScheduled_ReplacesIt_SoTheLatestChoiceWins()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (current, _, _) = await BillingTestData.StripePlanWithAnnualAsync(factory, monthlyPrice: 99900, annualPrice: 999000);
        var (earlier, _) = await BillingTestData.StripePlanAsync(factory, price: 9900);
        var (later, laterPriceId) = await BillingTestData.StripePlanAsync(factory, price: 19900);
        await BillingTestData.SubscribeLiveAsync(factory, coach, current, interval: BillingInterval.Annual);
        await BillingTestData.SetPendingPlanAsync(factory, coach.ProfileId, earlier);

        await ReadBodyAsync(await coach.Http.PostAsJsonAsync(Route, BodyFor(later, BillingInterval.Monthly), Ct));

        var call = fake.PlanChanges.Should().ContainSingle().Subject;
        call.AtNextRenewal.Should().BeTrue();
        call.PlanPriceExternalId.Should().Be(laterPriceId);
        (await BillingTestData.ReadRowAsync(factory, coach.ProfileId)).PendingSubscriptionPlanPriceId
            .Should().Be(await OpenPriceRowIdAsync(later, BillingInterval.Monthly));
    }
}
