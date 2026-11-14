using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Endpoints.Billing;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.AdminPromotions;

/// <summary>
/// Integration tests for <c>POST /admin/promotions</c> (<c>CreatePromotionEndpoint</c>) against the fake payment provider.
/// </summary>
[Collection(TestCollection.Name)]
public class CreatePromotionEndpointTests(FitnessApiFactory factory) : IDisposable
{
    private const string Route = "/admin/promotions";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Leaves the shared fake provider and the promotions table in their defaults for the next test class.</summary>
    public void Dispose()
    {
        BillingTestData.ResetProvider(factory);
        PromotionBuilder.ClearAsync(factory).GetAwaiter().GetResult();
    }

    private static object Body(
        string name,
        IEnumerable<string> plans,
        string mode = "Code",
        string? code = "Coach20",
        string duration = "Forever",
        int? months = null,
        int? maxUses = null,
        DateTimeOffset? claimUntil = null,
        bool newCoachesOnly = false,
        bool oneUsePerCoach = false) => new
        {
            Name = name,
            Kind = "Percent",
            Value = 20,
            Mode = mode,
            Code = code,
            Duration = duration,
            DurationMonths = months,
            ClaimFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ClaimUntil = claimUntil,
            PlanCodes = plans,
            MaxUses = maxUses,
            NewCoachesOnly = newCoachesOnly,
            OneUsePerCoach = oneUsePerCoach,
        };

    private static string UniqueName() => $"Promo {Guid.NewGuid():N}"[..20];

    /// <summary>
    /// A paid plan priced in Stripe, plus another plan that is also priced in Stripe, so selecting the first never covers
    /// every purchasable plan whatever else is in the shared database.
    /// </summary>
    private async Task<(string Code, string PriceId)> SubsetPlanAsync()
    {
        await BillingTestData.StripePlanAsync(factory);

        return await BillingTestData.StripePlanAsync(factory);
    }

    private async Task<List<Promotion>> PromotionsNamedAsync(string name)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.Promotions.AsNoTracking().Where(p => p.Name == name).ToListAsync(Ct);
    }

    private async Task<List<AuditLog>> AuditRowsAsync(Guid promotionId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.AuditLogs.AsNoTracking()
            .Where(a => a.EntityId == promotionId && a.Action == "AdminCreatePromotion")
            .ToListAsync(Ct);
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var raw = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.Should().Be(status, raw);
        raw.Should().Contain(code);
    }

    [Fact]
    public async Task Create_CodePromotion_CreatesCouponAndCodeAtStripe_SavesOurCopy_AndAudits()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var (plan, priceId) = await SubsetPlanAsync();
        var name = UniqueName();
        var until = DateTimeOffset.UtcNow.AddDays(60);

        var response = await admin.Http.PostAsJsonAsync(
            Route, Body(name, [plan], maxUses: 50, claimUntil: until, newCoachesOnly: true, oneUsePerCoach: true), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        var coupon = fake.Coupons.Should().ContainSingle().Subject;
        coupon.Spec.MaxUses.Should().Be(50);
        coupon.Spec.ClaimUntil.Should().BeCloseTo(until, TimeSpan.FromSeconds(1));
        coupon.Spec.PlanPriceExternalIds.Should().Equal(priceId);
        var code = fake.PromotionCodes.Should().ContainSingle().Subject;
        code.CouponId.Should().Be(coupon.CouponId);
        code.Code.Should().Be("COACH20");
        code.FirstTimeOnly.Should().BeTrue();

        var saved = (await PromotionsNamedAsync(name)).Should().ContainSingle().Subject;
        saved.StripeCouponId.Should().Be(coupon.CouponId);
        saved.StripePromotionCodeId.Should().Be(code.PromotionCodeId);
        saved.CodeNormalized.Should().Be("COACH20");
        saved.PlanCodes.Should().Equal(plan);
        saved.CoversSecondRoleAddOn.Should().BeFalse();
        saved.OneUsePerCoach.Should().BeTrue();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(BillingTestData.Json, Ct);
        body.GetProperty("promotionId").GetGuid().Should().Be(saved.PublicId);
        body.GetProperty("status").GetString().Should().Be("Active");
        body.GetProperty("usedCount").GetInt32().Should().Be(0);

        var audit = (await AuditRowsAsync(saved.PublicId)).Should().ContainSingle().Subject;
        audit.UserId.Should().Be(admin.UserId);
        audit.EntityType.Should().Be(nameof(Promotion));
        audit.NewValues.Should().NotContain("COACH20", "the code text is not written to the audit log");
    }

    [Fact]
    public async Task Create_AutomaticPromotion_CreatesOnlyACoupon()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var (plan, _) = await SubsetPlanAsync();
        var name = UniqueName();

        var response = await admin.Http.PostAsJsonAsync(
            Route, Body(name, [plan], mode: "Automatic", code: null, duration: "Months", months: 3), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        fake.Coupons.Should().ContainSingle().Which.Spec.DurationMonths.Should().Be(3);
        fake.PromotionCodes.Should().BeEmpty();
        var saved = (await PromotionsNamedAsync(name)).Should().ContainSingle().Subject;
        saved.StripePromotionCodeId.Should().BeNull();
        saved.Code.Should().BeNull();
        saved.DurationMonths.Should().Be(3);
    }

    [Fact]
    public async Task Create_CoveringEveryPurchasablePlan_LeavesTheCouponUnrestricted_SoItAlsoDiscountsTheAddOn()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        await BillingTestData.StripePlanAsync(factory);
        var name = UniqueName();

        // Purchasable = active, paid and priced in Stripe. Plans other tests insert without a Stripe price are not part of
        // it, so they can neither change the outcome nor put an invalid code into the request. The set is only read, never
        // modified, and every class that shares this database runs one after another in TestCollection.
        List<string> everyPurchasablePlan;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            everyPurchasablePlan = await db.SubscriptionPlanPrices
                .AsNoTracking()
                .Where(price => price.ValidTo == null && price.SubscriptionPlan.IsActive
                                && price.SubscriptionPlan.Code != "free" && price.PriceMinorUnits > 0
                                && price.ExternalPriceId != null && price.ExternalPriceId != "")
                .Select(price => price.SubscriptionPlan.Code)
                .ToListAsync(Ct);
        }

        var response = await admin.Http.PostAsJsonAsync(Route, Body(name, everyPurchasablePlan), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        fake.Coupons.Should().ContainSingle().Which.Spec.PlanPriceExternalIds.Should().BeNull();
        (await PromotionsNamedAsync(name)).Should().ContainSingle().Which.CoversSecondRoleAddOn.Should().BeTrue();
    }

    [Fact]
    public async Task Create_WhenAPaidPlanHasNoStripePrice_ThatPlanCannotBePartOfASubset()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var (pricedPlan, _) = await SubsetPlanAsync();
        var unpricedPlan = await PaidPlanBuilder.CreateAsync(factory);

        var response = await admin.Http.PostAsJsonAsync(Route, Body(UniqueName(), [pricedPlan, unpricedPlan]), Ct);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "PROMOTION_PLANS_INVALID");
        fake.Coupons.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_WithACodeAnotherPromotionThatHasNotEndedUses_Returns409_AndCreatesNothing()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var (plan, _) = await SubsetPlanAsync();
        await PromotionBuilder.ClearAsync(factory);
        await PromotionBuilder.CreateAsync(factory, [plan], configure: p => p.CodeNormalized = "COACH20");
        var name = UniqueName();

        var response = await admin.Http.PostAsJsonAsync(Route, Body(name, [plan], code: "coach20"), Ct);

        await AssertErrorAsync(response, HttpStatusCode.Conflict, "PROMOTION_CODE_TAKEN");
        fake.Coupons.Should().BeEmpty();
        (await PromotionsNamedAsync(name)).Should().BeEmpty();
    }

    [Fact]
    public async Task Create_WithTheCodeOfAnEndedPromotion_Succeeds()
    {
        BillingTestData.ResetProvider(factory);
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var (plan, _) = await SubsetPlanAsync();
        await PromotionBuilder.ClearAsync(factory);
        await PromotionBuilder.CreateAsync(factory, [plan], configure: p =>
        {
            p.CodeNormalized = "COACH20";
            p.ClaimFrom = DateTimeOffset.UtcNow.AddDays(-30);
            p.ClaimUntil = DateTimeOffset.UtcNow.AddDays(-1);
        });

        var response = await admin.Http.PostAsJsonAsync(Route, Body(UniqueName(), [plan]), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Create_WhenTheCouponCallFails_Returns502_AndSavesNothing()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var (plan, _) = await SubsetPlanAsync();
        var name = UniqueName();
        fake.Fail = true;

        var response = await admin.Http.PostAsJsonAsync(Route, Body(name, [plan]), Ct);

        await AssertErrorAsync(response, HttpStatusCode.BadGateway, "BILLING_PROVIDER_ERROR");
        (await PromotionsNamedAsync(name)).Should().BeEmpty();
    }

    [Fact]
    public async Task Create_WhenThePromotionCodeCallFails_Returns502_DeletesTheNewCoupon_AndSavesNothing()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var (plan, _) = await SubsetPlanAsync();
        var name = UniqueName();
        fake.FailPromotionCodeCreation = true;

        var response = await admin.Http.PostAsJsonAsync(Route, Body(name, [plan]), Ct);

        await AssertErrorAsync(response, HttpStatusCode.BadGateway, "BILLING_PROVIDER_ERROR");
        fake.DeletedCoupons.Should().ContainSingle().Which.Should().Be(fake.Coupons.Single().CouponId);
        (await PromotionsNamedAsync(name)).Should().BeEmpty();
    }

    [Fact]
    public async Task Create_WhenStripeIsNotConfigured_Returns503_AndSavesNothing()
    {
        var fake = BillingTestData.ResetProvider(factory);
        fake.IsConfigured = false;
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var (plan, _) = await SubsetPlanAsync();
        var name = UniqueName();

        var response = await admin.Http.PostAsJsonAsync(Route, Body(name, [plan]), Ct);

        await AssertErrorAsync(response, HttpStatusCode.ServiceUnavailable, "BILLING_NOT_CONFIGURED");
        fake.Coupons.Should().BeEmpty();
        (await PromotionsNamedAsync(name)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("free")]
    [InlineData("no-such-plan")]
    public async Task Create_WithAPlanThatIsNotAnActivePaidPlan_Returns400(string planCode)
    {
        var fake = BillingTestData.ResetProvider(factory);
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);

        var response = await admin.Http.PostAsJsonAsync(Route, Body(UniqueName(), [planCode]), Ct);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "PROMOTION_PLANS_INVALID");
        fake.Coupons.Should().BeEmpty();
    }

    [Fact]
    public async Task Create_WithAnInvalidBody_Returns400_WithoutCallingStripe()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);

        var response = await admin.Http.PostAsJsonAsync(Route, Body("", ["pro"], code: null), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        fake.Coupons.Should().BeEmpty();
    }
}
