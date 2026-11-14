using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.CoachSubscriptions.GetCoachPlans;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;

namespace FormUp.Tests.Endpoints.CoachSubscriptions;

/// <summary>
/// Integration tests for the automatic promotion on <c>GET /users/me/subscription/plans</c>: the winner per coach and the
/// display-only discounted prices.
/// </summary>
[Collection(TestCollection.Name)]
public class GetCoachPlansPromotionTests(FitnessApiFactory factory) : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Leaves the promotions table empty for the next test class.</summary>
    public void Dispose() => PromotionBuilder.ClearAsync(factory).GetAwaiter().GetResult();

    private async Task<GetCoachPlansResponse> GetAsync(Actor coach)
    {
        var response = await coach.Http.GetAsync("/users/me/subscription/plans", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return (await response.Content.ReadFromJsonAsync<GetCoachPlansResponse>(JsonOptions, Ct))!;
    }

    private async Task<string> PlanWithAddOnAsync() =>
        await PaidPlanBuilder.CreateAsync(factory, priceMinorUnits: 29900, addOnMinorUnits: 9900, bothRolesIncluded: false);

    [Fact]
    public async Task Get_WithAnAutomaticPromotion_ReturnsItAndDiscountedPricesIncludingTheAddOnLine()
    {
        await PromotionBuilder.ClearAsync(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var covered = await PlanWithAddOnAsync();
        var uncovered = await PlanWithAddOnAsync();
        var promotion = await PromotionBuilder.CreateAsync(factory, [covered], PromotionMode.Automatic, p =>
        {
            p.Value = 30;
            p.Duration = PromotionDuration.Months;
            p.DurationMonths = 3;
            p.ClaimUntil = DateTimeOffset.UtcNow.AddDays(20);
        });

        var response = await GetAsync(coach);

        response.Promotion.Should().NotBeNull();
        response.Promotion!.Name.Should().Be(promotion.Name);
        response.Promotion.Value.Should().Be(30);
        response.Promotion.Duration.Should().Be(PromotionDuration.Months);
        response.Promotion.DurationMonths.Should().Be(3);
        response.Promotion.ClaimUntil.Should().BeCloseTo(DateTimeOffset.UtcNow.AddDays(20), TimeSpan.FromMinutes(1));
        var coveredPlan = response.Plans.Single(plan => plan.Code == covered);
        coveredPlan.DiscountedPriceMinorUnits.Should().Be(20930);
        coveredPlan.DiscountedSecondRoleAddOnPriceMinorUnits.Should().Be(6930);
        var uncoveredPlan = response.Plans.Single(plan => plan.Code == uncovered);
        uncoveredPlan.DiscountedPriceMinorUnits.Should().BeNull("a plan outside the promotion shows its full price");
        uncoveredPlan.DiscountedSecondRoleAddOnPriceMinorUnits.Should().BeNull();
        response.Plans.Single(plan => plan.Code == SubscriptionPlanCodes.Free).DiscountedPriceMinorUnits.Should().BeNull();
    }

    [Fact]
    public async Task Get_APromotionLimitedToSomePlans_DiscountsTheAddOnOfEachCoveredPlan_AndOnlyThose()
    {
        await PromotionBuilder.ClearAsync(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var first = await PlanWithAddOnAsync();
        var second = await PlanWithAddOnAsync();
        var left = await PlanWithAddOnAsync();
        await PromotionBuilder.CreateAsync(factory, [first, second], PromotionMode.Automatic, p => p.Value = 30);

        var plans = (await GetAsync(coach)).Plans;

        foreach (var code in new[] { first, second })
        {
            var covered = plans.Single(item => item.Code == code);
            covered.DiscountedPriceMinorUnits.Should().Be(20930);
            covered.DiscountedSecondRoleAddOnPriceMinorUnits.Should().Be(6930, "the add-on is discounted with its plan");
        }

        var uncovered = plans.Single(item => item.Code == left);
        uncovered.DiscountedPriceMinorUnits.Should().BeNull();
        uncovered.DiscountedSecondRoleAddOnPriceMinorUnits.Should().BeNull();
    }

    [Fact]
    public async Task Get_AFixedAmountPromotion_ComesOffThePlanLineFirstAndThenTheAddOn()
    {
        await PromotionBuilder.ClearAsync(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PlanWithAddOnAsync();
        await PromotionBuilder.CreateAsync(factory, [plan], PromotionMode.Automatic, p =>
        {
            p.Kind = PromotionKind.FixedAmount;
            p.Value = 30000;
            p.Currency = "CZK";
        });

        var covered = (await GetAsync(coach)).Plans.Single(item => item.Code == plan);

        covered.DiscountedPriceMinorUnits.Should().Be(0);
        covered.DiscountedSecondRoleAddOnPriceMinorUnits.Should().Be(9800);
    }

    [Fact]
    public async Task Get_WhenTwoOverlap_ReturnsTheLatestClaimFrom_AndDoesNotFallBackForAPlanItDoesNotCover()
    {
        await PromotionBuilder.ClearAsync(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var olderPlan = await PlanWithAddOnAsync();
        var newerPlan = await PlanWithAddOnAsync();
        await PromotionBuilder.CreateAsync(factory, [olderPlan], PromotionMode.Automatic, p => p.ClaimFrom = DateTimeOffset.UtcNow.AddDays(-20));
        var newer = await PromotionBuilder.CreateAsync(factory, [newerPlan], PromotionMode.Automatic, p => p.ClaimFrom = DateTimeOffset.UtcNow.AddDays(-2));

        var response = await GetAsync(coach);

        response.Promotion!.Name.Should().Be(newer.Name);
        response.Plans.Single(plan => plan.Code == newerPlan).DiscountedPriceMinorUnits.Should().NotBeNull();
        response.Plans.Single(plan => plan.Code == olderPlan).DiscountedPriceMinorUnits.Should().BeNull(
            "the older promotion lost, and there is no fallback to it");
    }

    [Fact]
    public async Task Get_WithoutAnyAutomaticPromotion_ReturnsNone()
    {
        await PromotionBuilder.ClearAsync(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PlanWithAddOnAsync();
        await PromotionBuilder.CreateAsync(factory, [plan], PromotionMode.Code);

        var response = await GetAsync(coach);

        response.Promotion.Should().BeNull("a code promotion is never shown automatically");
        response.Plans.Should().OnlyContain(item => item.DiscountedPriceMinorUnits == null);
    }

    [Fact]
    public async Task Get_ANewCoachesOnlyPromotion_IsHiddenFromACoachWhoPaidBefore()
    {
        await PromotionBuilder.ClearAsync(factory);
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PlanWithAddOnAsync();
        await PromotionBuilder.CreateAsync(factory, [plan], PromotionMode.Automatic, p => p.NewCoachesOnly = true);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, SubscriptionPlanCodes.Free);

        (await GetAsync(coach)).Promotion.Should().NotBeNull("the coach has not paid yet");

        await PromotionBuilder.MarkPaidBeforeAsync(factory, coach.ProfileId);

        (await GetAsync(coach)).Promotion.Should().BeNull();
    }

    [Fact]
    public async Task Get_AOneUsePerCoachPromotion_IsHiddenOnceTheCoachRedeemedIt()
    {
        await PromotionBuilder.ClearAsync(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PlanWithAddOnAsync();
        var promotion = await PromotionBuilder.CreateAsync(factory, [plan], PromotionMode.Automatic, p => p.OneUsePerCoach = true);

        (await GetAsync(coach)).Promotion.Should().NotBeNull();

        await PromotionBuilder.AddRedemptionAsync(factory, promotion.Id, coach.ProfileId);

        (await GetAsync(coach)).Promotion.Should().BeNull();
    }
}
