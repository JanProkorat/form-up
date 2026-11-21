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
[Collection(TestCollection.Shard3)]
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
    public async Task Checkout_ForATwoRoleCoachSendingFalse_StillPassesTheAddOnAmount()
    {
        var fake = BillingTestData.ResetProvider(factory);
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        var (plan, _) = await StripePlanAsync(bothRolesIncluded: false, addOn: 9900);

        var response = await coach.Http.PostAsJsonAsync(Route, Body(plan, addOn: false), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.Checkouts.Should().ContainSingle(call => call.UserId == coach.UserId).Which.AddOnAmountMinorUnits.Should().Be(9900);
    }

    [Fact]
    public async Task Checkout_ForAOneRoleCoachSendingTrue_IgnoresTheFlagAndPassesNoAddOn()
    {
        var fake = BillingTestData.ResetProvider(factory);
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);
        var (plan, _) = await StripePlanAsync(bothRolesIncluded: false, addOn: 9900);

        var response = await coach.Http.PostAsJsonAsync(Route, Body(plan, addOn: true), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.Checkouts.Should().ContainSingle(call => call.UserId == coach.UserId).Which.AddOnAmountMinorUnits.Should().BeNull();
    }

    [Fact]
    public async Task Checkout_ForATwoRoleCoachOnAPlanThatIncludesBothRoles_PassesNoAddOn()
    {
        var fake = BillingTestData.ResetProvider(factory);
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        var (plan, _) = await StripePlanAsync(bothRolesIncluded: true, addOn: 9900);

        var response = await coach.Http.PostAsJsonAsync(Route, Body(plan, addOn: true), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.Checkouts.Should().ContainSingle(call => call.UserId == coach.UserId).Which.AddOnAmountMinorUnits.Should().BeNull();
    }

    [Fact]
    public async Task Checkout_ForATwoRoleCoachOnAPlanWithoutAnAddOnPrice_Returns400_AndStartsNoCheckout()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        var (plan, _) = await StripePlanAsync(bothRolesIncluded: false, addOn: null);

        await AssertNotPurchasableAsync(await coach.Http.PostAsJsonAsync(Route, Body(plan), Ct));
        fake.Checkouts.Should().NotContain(call => call.UserId == coach.UserId);
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
    public async Task Checkout_WithTheFlagOnAPlanThatIncludesBothRoles_Returns200WithNoAddOn()
    {
        var fake = BillingTestData.ResetProvider(factory);
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await StripePlanAsync(bothRolesIncluded: true);

        var response = await coach.Http.PostAsJsonAsync(Route, Body(plan, addOn: true), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.Checkouts.Should().ContainSingle(call => call.UserId == coach.UserId).Which.AddOnAmountMinorUnits.Should().BeNull();
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

    private static object BodyFor(string planCode, BillingInterval interval) =>
        new { PlanCode = planCode, BillingInterval = interval.ToString() };

    private static object BodyWithCode(string planCode, BillingInterval interval, string code) =>
        new { PlanCode = planCode, BillingInterval = interval.ToString(), Code = code };

    private async Task<(Actor Coach, string Plan)> CoachAndAnnualPlanAsync()
    {
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);
        await PromotionBuilder.ClearAsync(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _, _) = await BillingTestData.StripePlanWithAnnualAsync(factory);

        return (coach, plan);
    }

    private void AssertNothingSentToStripe(FakeSubscriptionService fake)
    {
        fake.Customers.Should().BeEmpty("a refused code is answered before the first Stripe call, so no customer is created");
        fake.Checkouts.Should().BeEmpty();
    }

    [Fact]
    public async Task Checkout_WithAValidCode_AttachesItsCoupon_InPlaceOfTheAutomaticPromotion()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var (coach, plan) = await CoachAndAnnualPlanAsync();
        await PromotionBuilder.CreateAsync(factory, [plan], PromotionMode.Automatic);
        var coded = await PromotionBuilder.CreateAsync(factory, [plan], PromotionMode.Code);

        var response = await coach.Http.PostAsJsonAsync(Route, BodyWithCode(plan, BillingInterval.Monthly, coded.Code!.ToLowerInvariant()), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        fake.Checkouts.Should().ContainSingle().Which.CouponId.Should().Be(coded.StripeCouponId);
    }

    [Fact]
    public async Task Checkout_WithABothPeriodsCode_AttachesTheCouponOfTheChosenInterval()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var (coach, plan) = await CoachAndAnnualPlanAsync();
        var coded = await PromotionBuilder.CreateAsync(
            factory, [plan], PromotionMode.Code, p =>
            {
                p.Duration = PromotionDuration.Months;
                p.DurationMonths = 3;
                p.StripeAnnualCouponId = $"coupon_annual_{Guid.NewGuid():N}";
            });

        await coach.Http.PostAsJsonAsync(Route, BodyWithCode(plan, BillingInterval.Annual, coded.Code!), Ct);
        await coach.Http.PostAsJsonAsync(Route, BodyWithCode(plan, BillingInterval.Monthly, coded.Code!), Ct);

        fake.Checkouts.Select(call => (call.Interval, call.CouponId)).Should().Equal(
            (BillingInterval.Annual, coded.StripeAnnualCouponId),
            (BillingInterval.Monthly, coded.StripeCouponId));
    }

    [Theory]
    [InlineData(PromotionBillingPeriod.Monthly, BillingInterval.Annual)]
    [InlineData(PromotionBillingPeriod.Annual, BillingInterval.Monthly)]
    public async Task Checkout_WithACodeThatDoesNotCoverTheInterval_Returns409_AndCallsStripeNotAtAll(
        PromotionBillingPeriod period, BillingInterval interval)
    {
        var fake = BillingTestData.ResetProvider(factory);
        var (coach, plan) = await CoachAndAnnualPlanAsync();
        var coded = await PromotionBuilder.CreateAsync(factory, [plan], PromotionMode.Code, p => p.BillingPeriod = period);

        var response = await coach.Http.PostAsJsonAsync(Route, BodyWithCode(plan, interval, coded.Code!), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("PROMOTION_CODE_INTERVAL_NOT_ELIGIBLE");
        AssertNothingSentToStripe(fake);
    }

    [Fact]
    public async Task Checkout_WithAnUnknownCode_Returns404_AndCallsStripeNotAtAll()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var (coach, plan) = await CoachAndAnnualPlanAsync();

        var response = await coach.Http.PostAsJsonAsync(Route, BodyWithCode(plan, BillingInterval.Monthly, "NOSUCHCODE"), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var raw = await response.Content.ReadAsStringAsync(Ct);
        raw.Should().Contain("PROMOTION_CODE_NOT_FOUND").And.NotContain("NOSUCHCODE");
        AssertNothingSentToStripe(fake);
    }

    [Fact]
    public async Task Checkout_WithACodeOfAnEndedPromotion_Returns404()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var (coach, plan) = await CoachAndAnnualPlanAsync();
        var coded = await PromotionBuilder.CreateAsync(
            factory, [plan], PromotionMode.Code, p => p.EndedAt = DateTimeOffset.UtcNow.AddMinutes(-5));

        var response = await coach.Http.PostAsJsonAsync(Route, BodyWithCode(plan, BillingInterval.Monthly, coded.Code!), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        AssertNothingSentToStripe(fake);
    }

    [Theory]
    [InlineData("PROMOTION_CODE_PLAN_NOT_ELIGIBLE")]
    [InlineData("PROMOTION_CODE_USED_UP")]
    [InlineData("PROMOTION_CODE_NOT_STARTED")]
    public async Task Checkout_WithACodeThatFailsASharedCoachRule_Returns409WithThatCode_AndCallsStripeNotAtAll(string expectedCode)
    {
        var fake = BillingTestData.ResetProvider(factory);
        var (coach, plan) = await CoachAndAnnualPlanAsync();
        var coded = await PromotionBuilder.CreateAsync(
            factory,
            expectedCode == "PROMOTION_CODE_PLAN_NOT_ELIGIBLE" ? ["another-plan"] : [plan],
            PromotionMode.Code,
            p =>
            {
                if (expectedCode == "PROMOTION_CODE_USED_UP")
                {
                    p.MaxUses = 0;
                }

                if (expectedCode == "PROMOTION_CODE_NOT_STARTED")
                {
                    p.ClaimFrom = DateTimeOffset.UtcNow.AddDays(3);
                }
            });

        var response = await coach.Http.PostAsJsonAsync(Route, BodyWithCode(plan, BillingInterval.Monthly, coded.Code!), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(expectedCode);
        AssertNothingSentToStripe(fake);
    }

    [Fact]
    public async Task Checkout_WithACodeOverTheMaximumLength_Returns400_AndCallsStripeNotAtAll()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var (coach, plan) = await CoachAndAnnualPlanAsync();

        var response = await coach.Http.PostAsJsonAsync(Route, BodyWithCode(plan, BillingInterval.Monthly, new string('A', 51)), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        AssertNothingSentToStripe(fake);
    }

    [Fact]
    public async Task Checkout_AnAutomaticMonthlyOnlyPromotionOnAnAnnualCheckout_AttachesNoCoupon()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var (coach, plan) = await CoachAndAnnualPlanAsync();
        await PromotionBuilder.CreateAsync(factory, [plan], PromotionMode.Automatic, p => p.BillingPeriod = PromotionBillingPeriod.Monthly);

        var response = await coach.Http.PostAsJsonAsync(Route, BodyFor(plan, BillingInterval.Annual), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        fake.Checkouts.Should().ContainSingle().Which.CouponId.Should().BeNull();
    }

    [Fact]
    public async Task Checkout_AnAutomaticBothPeriodsPromotionOnAnAnnualCheckout_AttachesTheAnnualCoupon()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var (coach, plan) = await CoachAndAnnualPlanAsync();
        var promotion = await PromotionBuilder.CreateAsync(
            factory, [plan], PromotionMode.Automatic, p =>
            {
                p.Duration = PromotionDuration.FirstMonth;
                p.StripeAnnualCouponId = $"coupon_annual_{Guid.NewGuid():N}";
            });

        var response = await coach.Http.PostAsJsonAsync(Route, BodyFor(plan, BillingInterval.Annual), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        fake.Checkouts.Should().ContainSingle().Which.CouponId.Should().Be(promotion.StripeAnnualCouponId);
    }

    [Fact]
    public async Task Checkout_AnnualForATwoRoleCoach_UsesTheAnnualPriceAndTheAnnualAddOn()
    {
        var fake = BillingTestData.ResetProvider(factory);
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        var (plan, _, annualPriceId) = await BillingTestData.StripePlanWithAnnualAsync(
            factory, monthlyPrice: 29900, annualPrice: 299000, monthlyAddOn: 9900, annualAddOn: 99000, bothRolesIncluded: false);

        var response = await coach.Http.PostAsJsonAsync(Route, BodyFor(plan, BillingInterval.Annual), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var call = fake.Checkouts.Should().ContainSingle(c => c.UserId == coach.UserId).Subject;
        call.PlanPriceExternalId.Should().Be(annualPriceId);
        call.AddOnAmountMinorUnits.Should().Be(99000, "the add-on is billed per year next to the annual plan");
        call.Interval.Should().Be(BillingInterval.Annual);
    }

    [Fact]
    public async Task Checkout_WithoutAnInterval_StaysMonthly_EvenWhenThePlanIsAlsoSoldAnnually()
    {
        var fake = BillingTestData.ResetProvider(factory);
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        var (plan, monthlyPriceId, _) = await BillingTestData.StripePlanWithAnnualAsync(
            factory, monthlyAddOn: 9900, annualAddOn: 99000, bothRolesIncluded: false);

        var response = await coach.Http.PostAsJsonAsync(Route, Body(plan), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var call = fake.Checkouts.Should().ContainSingle(c => c.UserId == coach.UserId).Subject;
        call.PlanPriceExternalId.Should().Be(monthlyPriceId);
        call.AddOnAmountMinorUnits.Should().Be(9900);
        call.Interval.Should().Be(BillingInterval.Monthly);
    }

    [Fact]
    public async Task Checkout_AnnualForAPlanSoldOnlyMonthly_Returns400_AndStartsNoCheckout()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await StripePlanAsync();

        await AssertNotPurchasableAsync(await coach.Http.PostAsJsonAsync(Route, BodyFor(plan, BillingInterval.Annual), Ct));
        fake.Checkouts.Should().NotContain(call => call.UserId == coach.UserId);
    }

    [Fact]
    public async Task Checkout_AnnualForATwoRoleCoachWhenTheAnnualRowHasNoAddOnPrice_Returns400_AndStartsNoCheckout()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        var (plan, _, _) = await BillingTestData.StripePlanWithAnnualAsync(
            factory, monthlyAddOn: 9900, annualAddOn: null, bothRolesIncluded: false);

        await AssertNotPurchasableAsync(await coach.Http.PostAsJsonAsync(Route, BodyFor(plan, BillingInterval.Annual), Ct));
        fake.Checkouts.Should().NotContain(call => call.UserId == coach.UserId);
    }

    [Fact]
    public async Task Checkout_WithAnOutOfRangeInterval_Returns400()
    {
        BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await StripePlanAsync();

        var response = await coach.Http.PostAsJsonAsync(Route, new { PlanCode = plan, BillingInterval = 99 }, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
