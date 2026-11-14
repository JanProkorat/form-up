using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.Billing.CreateCheckoutSession;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Billing;

/// <summary>
/// Integration tests for <c>POST /billing/checkout-session</c> (<c>CreateCheckoutSessionEndpoint</c>) against the fake
/// payment provider.
/// </summary>
[Collection(TestCollection.Name)]
public class CreateCheckoutSessionEndpointTests(FitnessApiFactory factory) : IDisposable
{
    /// <summary>Leaves the shared fake provider and the promotions table in their defaults for the next test class.</summary>
    public void Dispose()
    {
        BillingTestData.ResetProvider(factory);
        PromotionBuilder.ClearAsync(factory).GetAwaiter().GetResult();
    }

    private const string Route = "/billing/checkout-session";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(string PlanCode, string PriceId)> StripePlanAsync(
        bool bothRolesIncluded = true, long? addOn = null, bool active = true, long price = 29900)
    {
        var plan = await PaidPlanBuilder.CreateAsync(factory, priceMinorUnits: price, addOnMinorUnits: addOn, bothRolesIncluded: bothRolesIncluded);
        var priceId = await BillingTestData.SetStripePriceAsync(factory, plan);

        if (!active)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.SubscriptionPlans.Where(p => p.Code == plan).ExecuteUpdateAsync(s => s.SetProperty(p => p.IsActive, false), Ct);
        }

        return (plan, priceId);
    }

    private async Task<CoachSubscription?> ReadRowAsync(long profileId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.CoachSubscriptions.AsNoTracking()
            .Include(cs => cs.SubscriptionPlan)
            .SingleOrDefaultAsync(cs => cs.ProfessionalProfileId == profileId, Ct);
    }

    private static object Body(string planCode, bool addOn = false) => new { PlanCode = planCode, IncludeSecondRoleAddOn = addOn };

    private async Task AssertNotPurchasableAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("PLAN_NOT_PURCHASABLE");
    }

    [Fact]
    public async Task Checkout_ForAPaidPlan_ReturnsTheUrl_CreatesTheCustomerOnce_AndPassesTheCoachLanguage()
    {
        var fake = BillingTestData.ResetProvider(factory);
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, priceId) = await StripePlanAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Users.Where(u => u.Id == coach.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.Language, "de"), Ct);
        }

        var first = await coach.Http.PostAsJsonAsync(Route, Body(plan), Ct);
        var second = await coach.Http.PostAsJsonAsync(Route, Body(plan), Ct);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        (await first.Content.ReadFromJsonAsync<CreateCheckoutSessionResponse>(Ct))!.Url.Should().Be("https://checkout.stripe.test/session");
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.Customers.Should().ContainSingle(call => call.UserId == coach.UserId, "the customer is reused per profile");
        var row = await ReadRowAsync(coach.ProfileId);
        row!.ExternalCustomerId.Should().StartWith("cus_fake_");
        row.SubscriptionPlan.Code.Should().Be("free", "starting a checkout does not change the plan");
        var calls = fake.Checkouts.Where(call => call.UserId == coach.UserId).ToList();
        calls.Should().HaveCount(2);
        calls.Should().OnlyContain(call => call.PlanCode == plan && call.PlanPriceExternalId == priceId && call.CouponId == null && call.Language == "de");
        calls.Should().OnlyContain(call => call.CustomerId == row.ExternalCustomerId);
    }

    [Fact]
    public async Task Checkout_WithTheSignUpFlow_PassesItToTheProvider_AndDefaultsToSettings()
    {
        var fake = BillingTestData.ResetProvider(factory);
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await StripePlanAsync();

        var signUp = await coach.Http.PostAsJsonAsync(Route, new { PlanCode = plan, Flow = "SignUp" }, Ct);
        var defaulted = await coach.Http.PostAsJsonAsync(Route, Body(plan), Ct);

        signUp.StatusCode.Should().Be(HttpStatusCode.OK);
        defaulted.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.Checkouts.Where(call => call.UserId == coach.UserId).Select(call => call.Flow)
            .Should().Equal(CheckoutFlow.SignUp, CheckoutFlow.Settings);
    }

    [Fact]
    public async Task Checkout_WithAnUnknownFlow_Returns400()
    {
        BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await StripePlanAsync();

        var response = await coach.Http.PostAsJsonAsync(Route, new { PlanCode = plan, Flow = 99 }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Checkout_WithTheSecondRoleAddOn_PassesTheAddOnAmount()
    {
        var fake = BillingTestData.ResetProvider(factory);
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var (plan, _) = await StripePlanAsync(bothRolesIncluded: false, addOn: 9900);

        var response = await coach.Http.PostAsJsonAsync(Route, Body(plan, addOn: true), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.Checkouts.Should().ContainSingle(call => call.UserId == coach.UserId).Which.AddOnAmountMinorUnits.Should().Be(9900);
    }

    [Fact]
    public async Task Checkout_ForACoachWhoseGivenPlanEnded_IsNotBlockedByThePlanChoiceGate()
    {
        BillingTestData.ResetProvider(factory);
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await StripePlanAsync();
        await CoachSubscriptionBuilder.SubscribeWithEndedGiftAsync(factory, coach, plan);

        var response = await coach.Http.PostAsJsonAsync(Route, Body(plan), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Checkout_WithoutAPlanCode_Returns400()
    {
        BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);

        var response = await coach.Http.PostAsJsonAsync(Route, Body(string.Empty), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Checkout_ForAnUnknownPlan_Returns400()
    {
        BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);

        await AssertNotPurchasableAsync(await coach.Http.PostAsJsonAsync(Route, Body("no-such-plan"), Ct));
    }

    [Fact]
    public async Task Checkout_ForTheFreePlan_Returns400()
    {
        BillingTestData.ResetProvider(factory);
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);

        await AssertNotPurchasableAsync(await coach.Http.PostAsJsonAsync(Route, Body("free"), Ct));
    }

    [Fact]
    public async Task Checkout_ForAnInactivePlan_Returns400()
    {
        BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await StripePlanAsync(active: false);

        await AssertNotPurchasableAsync(await coach.Http.PostAsJsonAsync(Route, Body(plan), Ct));
    }

    [Fact]
    public async Task Checkout_ForAPlanNotPricedInStripe_Returns400()
    {
        BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(factory);

        await AssertNotPurchasableAsync(await coach.Http.PostAsJsonAsync(Route, Body(plan), Ct));
    }

    [Fact]
    public async Task Checkout_WithAnAddOnOnAPlanThatIncludesBothRoles_Returns400()
    {
        BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await StripePlanAsync(bothRolesIncluded: true);

        await AssertNotPurchasableAsync(await coach.Http.PostAsJsonAsync(Route, Body(plan, addOn: true), Ct));
    }

    [Fact]
    public async Task Checkout_ForACoachWithALiveSubscription_Returns409()
    {
        BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await StripePlanAsync();
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, plan);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.CoachSubscriptions.Where(cs => cs.ProfessionalProfileId == coach.ProfileId)
                .ExecuteUpdateAsync(s => s.SetProperty(cs => cs.ExternalSubscriptionId, "sub_live"), Ct);
        }

        var response = await coach.Http.PostAsJsonAsync(Route, Body(plan), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("BILLING_ALREADY_SUBSCRIBED");
    }

    [Fact]
    public async Task Checkout_WhenStripeIsNotConfigured_Returns503()
    {
        var fake = BillingTestData.ResetProvider(factory);
        fake.IsConfigured = false;
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await StripePlanAsync();

        var response = await coach.Http.PostAsJsonAsync(Route, Body(plan), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("BILLING_NOT_CONFIGURED");
        fake.Customers.Should().BeEmpty();
    }

    [Fact]
    public async Task Checkout_WhenStripeFails_Returns502()
    {
        var fake = BillingTestData.ResetProvider(factory);
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);
        fake.Fail = true;
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await StripePlanAsync();

        var response = await coach.Http.PostAsJsonAsync(Route, Body(plan), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("BILLING_PROVIDER_ERROR");
    }

    [Fact]
    public async Task Checkout_WithoutAToken_Returns401()
    {
        var response = await factory.CreateClient().PostAsJsonAsync(Route, Body("anything"), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Checkout_WithAnAutomaticPromotionCoveringThePlan_PassesItsCoupon()
    {
        var fake = BillingTestData.ResetProvider(factory);
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);
        await PromotionBuilder.ClearAsync(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await StripePlanAsync();
        var promotion = await PromotionBuilder.CreateAsync(factory, [plan], PromotionMode.Automatic);

        var response = await coach.Http.PostAsJsonAsync(Route, Body(plan), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.Checkouts.Should().ContainSingle(call => call.UserId == coach.UserId).Which.CouponId.Should().Be(promotion.StripeCouponId);
    }

    [Fact]
    public async Task Checkout_AfterAnAdminEndedTheAutomaticPromotion_PassesNoCoupon()
    {
        var fake = BillingTestData.ResetProvider(factory);
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);
        await PromotionBuilder.ClearAsync(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await StripePlanAsync();
        await PromotionBuilder.CreateAsync(
            factory, [plan], PromotionMode.Automatic, p => p.EndedAt = DateTimeOffset.UtcNow.AddMinutes(-5));

        var response = await coach.Http.PostAsJsonAsync(Route, Body(plan), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.Checkouts.Should().ContainSingle(call => call.UserId == coach.UserId).Which.CouponId.Should().BeNull();
    }

    [Fact]
    public async Task Checkout_WhenTwoAutomaticPromotionsOverlap_PassesTheLatestClaimFrom()
    {
        var fake = BillingTestData.ResetProvider(factory);
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);
        await PromotionBuilder.ClearAsync(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await StripePlanAsync();
        await PromotionBuilder.CreateAsync(factory, [plan], PromotionMode.Automatic, p => p.ClaimFrom = DateTimeOffset.UtcNow.AddDays(-20));
        var newer = await PromotionBuilder.CreateAsync(factory, [plan], PromotionMode.Automatic, p => p.ClaimFrom = DateTimeOffset.UtcNow.AddDays(-2));

        var response = await coach.Http.PostAsJsonAsync(Route, Body(plan), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.Checkouts.Should().ContainSingle(call => call.UserId == coach.UserId).Which.CouponId.Should().Be(newer.StripeCouponId);
    }

    [Fact]
    public async Task Checkout_ForAPlanTheWinningPromotionDoesNotCover_PassesNoCoupon_AndDoesNotFallBackToAnotherPromotion()
    {
        var fake = BillingTestData.ResetProvider(factory);
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);
        await PromotionBuilder.ClearAsync(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await StripePlanAsync();
        var (otherPlan, _) = await StripePlanAsync();
        await PromotionBuilder.CreateAsync(factory, [plan], PromotionMode.Automatic, p => p.ClaimFrom = DateTimeOffset.UtcNow.AddDays(-20));
        await PromotionBuilder.CreateAsync(factory, [otherPlan], PromotionMode.Automatic, p => p.ClaimFrom = DateTimeOffset.UtcNow.AddDays(-2));

        var response = await coach.Http.PostAsJsonAsync(Route, Body(plan), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.Checkouts.Should().ContainSingle(call => call.UserId == coach.UserId).Which.CouponId.Should().BeNull(
            "the newer promotion wins per coach and does not cover this plan");
    }

    [Fact]
    public async Task Checkout_IgnoresCodePromotionsAndPromotionsThatAreNotClaimableYet()
    {
        var fake = BillingTestData.ResetProvider(factory);
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);
        await PromotionBuilder.ClearAsync(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await StripePlanAsync();
        await PromotionBuilder.CreateAsync(factory, [plan], PromotionMode.Code);
        await PromotionBuilder.CreateAsync(factory, [plan], PromotionMode.Automatic, p => p.ClaimFrom = DateTimeOffset.UtcNow.AddDays(3));

        var response = await coach.Http.PostAsJsonAsync(Route, Body(plan), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.Checkouts.Should().ContainSingle(call => call.UserId == coach.UserId).Which.CouponId.Should().BeNull();
    }
}
