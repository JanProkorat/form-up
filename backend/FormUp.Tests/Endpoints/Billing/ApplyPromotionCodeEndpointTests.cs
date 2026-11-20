using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Billing;

/// <summary>
/// Integration tests for <c>POST /billing/promotion-code</c> (<c>ApplyPromotionCodeEndpoint</c>) against the fake
/// payment provider and our own promotions.
/// </summary>
[Collection(TestCollection.Name)]
public class ApplyPromotionCodeEndpointTests(FitnessApiFactory factory) : IDisposable
{
    /// <summary>Leaves the shared fake provider and the promotions table in their defaults for the next test class.</summary>
    public void Dispose()
    {
        BillingTestData.ResetProvider(factory);
        PromotionBuilder.ClearAsync(factory).GetAwaiter().GetResult();
    }

    private const string Route = "/billing/promotion-code";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Body(string code) => new { Code = code };

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var raw = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(status, raw);
        raw.Should().Contain(code);
    }

    /// <summary>A coach with a live paid subscription, and a code promotion covering the plan.</summary>
    private async Task<(Actor Coach, string SubscriptionId, Promotion Promotion)> ArrangeAsync(
        Action<Promotion>? configure = null, bool coversPlan = true)
    {
        await PromotionBuilder.ClearAsync(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await BillingTestData.StripePlanAsync(factory);
        var subscriptionId = await BillingTestData.SubscribeLiveAsync(factory, coach, plan);
        var promotion = await PromotionBuilder.CreateAsync(factory, coversPlan ? [plan] : ["another-plan"], configure: configure);

        return (coach, subscriptionId, promotion);
    }

    [Fact]
    public async Task ApplyCode_OnTheCallersOwnLiveSubscription_Returns204_AndAttachesTheCouponNotAPromotionCode()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var (coach, subscriptionId, promotion) = await ArrangeAsync();

        var response = await coach.Http.PostAsJsonAsync(Route, Body(promotion.Code!), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        fake.PromotionCodeSubscriptions.Should().ContainSingle().Which.Should().Be(subscriptionId);
        fake.AppliedCouponIds.Should().ContainSingle().Which.Should().Be(promotion.StripeCouponId);
    }

    [Fact]
    public async Task ApplyCode_TypedInLowerCase_FindsThePromotion()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var (coach, _, promotion) = await ArrangeAsync();

        var response = await coach.Http.PostAsJsonAsync(Route, Body(promotion.Code!.ToLowerInvariant()), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        fake.AppliedCouponIds.Should().ContainSingle();
    }

    [Fact]
    public async Task ApplyCode_ThatNoPromotionHas_Returns404_AndDoesNotEchoTheCode()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var (coach, _, _) = await ArrangeAsync();

        var response = await coach.Http.PostAsJsonAsync(Route, Body("SECRETCODE"), Ct);

        await AssertErrorAsync(response, HttpStatusCode.NotFound, "PROMOTION_CODE_NOT_FOUND");
        (await response.Content.ReadAsStringAsync(Ct)).Should().NotContain("SECRETCODE");
        fake.AppliedCouponIds.Should().BeEmpty();
    }

    [Fact]
    public async Task ApplyCode_AfterTheClaimWindowClosed_Returns404()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var (coach, _, promotion) = await ArrangeAsync(p =>
        {
            p.ClaimFrom = DateTimeOffset.UtcNow.AddDays(-10);
            p.ClaimUntil = DateTimeOffset.UtcNow.AddDays(-1);
        });

        await AssertErrorAsync(
            await coach.Http.PostAsJsonAsync(Route, Body(promotion.Code!), Ct), HttpStatusCode.NotFound, "PROMOTION_CODE_NOT_FOUND");
        fake.AppliedCouponIds.Should().BeEmpty();
    }

    [Fact]
    public async Task ApplyCode_AfterAnAdminEndedThePromotion_Returns404_WithoutCallingStripe()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var (coach, _, promotion) = await ArrangeAsync(p => p.EndedAt = DateTimeOffset.UtcNow.AddMinutes(-5));

        await AssertErrorAsync(
            await coach.Http.PostAsJsonAsync(Route, Body(promotion.Code!), Ct), HttpStatusCode.NotFound, "PROMOTION_CODE_NOT_FOUND");
        fake.AppliedCouponIds.Should().BeEmpty();
    }

    [Fact]
    public async Task ApplyCode_BeforeTheClaimWindowOpens_Returns409()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var (coach, _, promotion) = await ArrangeAsync(p => p.ClaimFrom = DateTimeOffset.UtcNow.AddDays(3));

        await AssertErrorAsync(
            await coach.Http.PostAsJsonAsync(Route, Body(promotion.Code!), Ct), HttpStatusCode.Conflict, "PROMOTION_CODE_NOT_STARTED");
        fake.AppliedCouponIds.Should().BeEmpty();
    }

    [Fact]
    public async Task ApplyCode_ForAPlanThePromotionDoesNotCover_Returns409()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var (coach, _, promotion) = await ArrangeAsync(coversPlan: false);

        await AssertErrorAsync(
            await coach.Http.PostAsJsonAsync(Route, Body(promotion.Code!), Ct), HttpStatusCode.Conflict, "PROMOTION_CODE_PLAN_NOT_ELIGIBLE");
        fake.AppliedCouponIds.Should().BeEmpty();
    }

    [Fact]
    public async Task ApplyCode_WhenMaxUsesAreTaken_Returns409()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var (coach, _, promotion) = await ArrangeAsync(p => p.MaxUses = 1);
        var other = await TestActors.Trainer(factory).CreateAsync(Ct);
        await PromotionBuilder.AddRedemptionAsync(factory, promotion.Id, other.ProfileId);

        await AssertErrorAsync(
            await coach.Http.PostAsJsonAsync(Route, Body(promotion.Code!), Ct), HttpStatusCode.Conflict, "PROMOTION_CODE_USED_UP");
        fake.AppliedCouponIds.Should().BeEmpty();
    }

    [Fact]
    public async Task ApplyCode_ForNewCoachesOnly_WhenTheCoachPaidBefore_Returns409()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var (coach, _, promotion) = await ArrangeAsync(p => p.NewCoachesOnly = true);
        await PromotionBuilder.MarkPaidBeforeAsync(factory, coach.ProfileId);

        await AssertErrorAsync(
            await coach.Http.PostAsJsonAsync(Route, Body(promotion.Code!), Ct), HttpStatusCode.Conflict, "PROMOTION_CODE_NEW_COACHES_ONLY");
        fake.AppliedCouponIds.Should().BeEmpty();
    }

    [Fact]
    public async Task ApplyCode_WithOneUsePerCoach_WhenTheCoachAlreadyRedeemedIt_Returns409()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var (coach, _, promotion) = await ArrangeAsync(p => p.OneUsePerCoach = true);
        await PromotionBuilder.AddRedemptionAsync(factory, promotion.Id, coach.ProfileId);

        await AssertErrorAsync(
            await coach.Http.PostAsJsonAsync(Route, Body(promotion.Code!), Ct), HttpStatusCode.Conflict, "PROMOTION_CODE_ALREADY_USED");
        fake.AppliedCouponIds.Should().BeEmpty();
    }

    /// <summary>A coach on the annual price of a plan that is also sold monthly, and a code promotion covering the plan.</summary>
    private async Task<(Actor Coach, string SubscriptionId, Promotion Promotion)> ArrangeAnnualAsync(
        BillingInterval interval, Action<Promotion>? configure = null)
    {
        await PromotionBuilder.ClearAsync(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _, _) = await BillingTestData.StripePlanWithAnnualAsync(factory);
        var subscriptionId = await BillingTestData.SubscribeLiveAsync(factory, coach, plan, interval: interval);
        var promotion = await PromotionBuilder.CreateAsync(factory, [plan], configure: configure);

        return (coach, subscriptionId, promotion);
    }

    [Theory]
    [InlineData(PromotionBillingPeriod.Monthly, BillingInterval.Annual)]
    [InlineData(PromotionBillingPeriod.Annual, BillingInterval.Monthly)]
    public async Task ApplyCode_ThatDoesNotCoverTheSubscriptionsInterval_Returns409_WithoutCallingStripe(
        PromotionBillingPeriod period, BillingInterval interval)
    {
        var fake = BillingTestData.ResetProvider(factory);
        var (coach, _, promotion) = await ArrangeAnnualAsync(interval, p => p.BillingPeriod = period);

        await AssertErrorAsync(
            await coach.Http.PostAsJsonAsync(Route, Body(promotion.Code!), Ct),
            HttpStatusCode.Conflict,
            "PROMOTION_CODE_INTERVAL_NOT_ELIGIBLE");
        fake.AppliedCouponIds.Should().BeEmpty();
    }

    [Theory]
    [InlineData(BillingInterval.Annual, true)]
    [InlineData(BillingInterval.Monthly, false)]
    public async Task ApplyCode_OfABothPeriodsPromotion_AttachesTheCouponOfTheSubscriptionsInterval(
        BillingInterval interval, bool expectsAnnualCoupon)
    {
        var fake = BillingTestData.ResetProvider(factory);
        var (coach, _, promotion) = await ArrangeAnnualAsync(
            interval, p =>
            {
                p.Duration = PromotionDuration.Months;
                p.DurationMonths = 3;
                p.StripeAnnualCouponId = $"coupon_annual_{Guid.NewGuid():N}";
            });

        var response = await coach.Http.PostAsJsonAsync(Route, Body(promotion.Code!), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        fake.AppliedCouponIds.Should().ContainSingle().Which.Should().Be(
            expectsAnnualCoupon ? promotion.StripeAnnualCouponId : promotion.StripeCouponId);
    }

    [Fact]
    public async Task ApplyCode_OfACodePromotionWithoutAStripePromotionCode_StillWorks()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var (coach, _, promotion) = await ArrangeAsync(p => p.StripePromotionCodeId = null);

        var response = await coach.Http.PostAsJsonAsync(Route, Body(promotion.Code!), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        fake.AppliedCouponIds.Should().ContainSingle().Which.Should().Be(promotion.StripeCouponId);
    }

    [Fact]
    public async Task ApplyCode_ThatIsAlreadyTheCurrentPromotion_Returns204_WithoutCallingStripeAgain()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var (coach, _, promotion) = await ArrangeAsync(p => p.OneUsePerCoach = true);
        await PromotionBuilder.AddRedemptionAsync(factory, promotion.Id, coach.ProfileId);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.CoachSubscriptions
                .Where(cs => cs.ProfessionalProfileId == coach.ProfileId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(cs => cs.CurrentPromotionId, promotion.Id), Ct);
        }

        var first = await coach.Http.PostAsJsonAsync(Route, Body(promotion.Code!), Ct);
        var second = await coach.Http.PostAsJsonAsync(Route, Body(promotion.Code!), Ct);

        first.StatusCode.Should().Be(HttpStatusCode.NoContent);
        second.StatusCode.Should().Be(HttpStatusCode.NoContent);
        fake.AppliedCouponIds.Should().BeEmpty("the discount is already on the subscription");
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
        var (coach, _, promotion) = await ArrangeAsync();
        fake.Fail = true;

        await AssertErrorAsync(
            await coach.Http.PostAsJsonAsync(Route, Body(promotion.Code!), Ct), HttpStatusCode.BadGateway, "BILLING_PROVIDER_ERROR");
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
