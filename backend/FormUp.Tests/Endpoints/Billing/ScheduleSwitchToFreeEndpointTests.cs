using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Features.Billing.ScheduleSwitchToFree;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;

namespace FormUp.Tests.Endpoints.Billing;

/// <summary>
/// Integration tests for <c>POST /billing/switch-to-free</c> (<c>ScheduleSwitchToFreeEndpoint</c>) against the fake
/// payment provider.
/// </summary>
[Collection(TestCollection.Name)]
public class ScheduleSwitchToFreeEndpointTests(FitnessApiFactory factory) : IDisposable
{
    /// <summary>Leaves the shared fake provider in its defaults for the next test class.</summary>
    public void Dispose() => BillingTestData.ResetProvider(factory);

    private const string Route = "/billing/switch-to-free";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var raw = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(status, raw);
        raw.Should().Contain(code);
    }

    [Fact]
    public async Task SwitchToFree_OnALiveSubscription_CancelsAtPeriodEnd_AndKeepsThePlanUntilTheWebhook()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await BillingTestData.StripePlanAsync(factory);
        var periodEnd = DateTimeOffset.UtcNow.AddDays(12);
        var subscriptionId = await BillingTestData.SubscribeLiveAsync(factory, coach, plan, currentPeriodEndsAt: periodEnd);
        var lastEvent = DateTimeOffset.UtcNow.AddHours(-2);
        await BillingTestData.SetLastExternalEventAsync(factory, coach.ProfileId, lastEvent);

        var response = await coach.Http.PostAsync(Route, null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<ScheduleSwitchToFreeResponse>(Ct))!.EndsAt
            .Should().BeCloseTo(periodEnd, TimeSpan.FromSeconds(1));
        fake.CancelledAtPeriodEnd.Should().ContainSingle().Which.Should().Be(subscriptionId);
        var row = await BillingTestData.ReadRowAsync(factory, coach.ProfileId);
        row.CancelAtPeriodEnd.Should().BeTrue();
        row.SubscriptionPlan.Code.Should().Be(plan, "the paid plan stays until the deleted webhook");
        row.LastExternalEventAt.Should().BeCloseTo(lastEvent, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task SwitchToFree_WhileACancellationIsPending_Returns409()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await BillingTestData.StripePlanAsync(factory);
        await BillingTestData.SubscribeLiveAsync(factory, coach, plan, cancelAtPeriodEnd: true);

        await AssertErrorAsync(await coach.Http.PostAsync(Route, null, Ct), HttpStatusCode.Conflict, "BILLING_CANCELLATION_PENDING");

        fake.CancelledAtPeriodEnd.Should().BeEmpty();
    }

    [Fact]
    public async Task SwitchToFree_WithoutALiveSubscription_Returns409()
    {
        BillingTestData.ResetProvider(factory);
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, "free");

        await AssertErrorAsync(await coach.Http.PostAsync(Route, null, Ct), HttpStatusCode.Conflict, "BILLING_NO_SUBSCRIPTION");
    }

    [Fact]
    public async Task SwitchToFree_WhileAGiftIsInForce_Returns409()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await BillingTestData.StripePlanAsync(factory);
        await BillingTestData.SubscribeLiveAsync(factory, coach, plan, givenFreeAt: DateTimeOffset.UtcNow.AddDays(-1));

        await AssertErrorAsync(await coach.Http.PostAsync(Route, null, Ct), HttpStatusCode.Conflict, "BILLING_GIFT_IN_FORCE");

        fake.CancelledAtPeriodEnd.Should().BeEmpty();
    }

    [Fact]
    public async Task SwitchToFree_WhenStripeFails_Returns502_AndSavesNothing()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await BillingTestData.StripePlanAsync(factory);
        await BillingTestData.SubscribeLiveAsync(factory, coach, plan);
        fake.Fail = true;

        await AssertErrorAsync(await coach.Http.PostAsync(Route, null, Ct), HttpStatusCode.BadGateway, "BILLING_PROVIDER_ERROR");

        (await BillingTestData.ReadRowAsync(factory, coach.ProfileId)).CancelAtPeriodEnd.Should().BeFalse();
    }

    [Fact]
    public async Task SwitchToFree_WhenStripeIsNotConfigured_Returns503()
    {
        var fake = BillingTestData.ResetProvider(factory);
        fake.IsConfigured = false;
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);

        await AssertErrorAsync(await coach.Http.PostAsync(Route, null, Ct), HttpStatusCode.ServiceUnavailable, "BILLING_NOT_CONFIGURED");
    }

    [Fact]
    public async Task SwitchToFree_ByACoachWhoseRolesAreAllRemoved_Returns403()
    {
        BillingTestData.ResetProvider(factory);
        var coach = await BillingTestData.CoachWithAllRolesRemovedAsync(factory);

        await AssertErrorAsync(await coach.Http.PostAsync(Route, null, Ct), HttpStatusCode.Forbidden, ErrorCodes.CoachRoleRemoved);
    }
}
