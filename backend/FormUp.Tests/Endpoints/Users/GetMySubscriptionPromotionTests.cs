using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.Users.GetMySubscription;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Users;

/// <summary>
/// Integration tests for the current promotion on <c>GET /users/me/subscription</c>.
/// </summary>
[Collection(TestCollection.Name)]
public class GetMySubscriptionPromotionTests(FitnessApiFactory factory) : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Leaves the promotions table empty for the next test class.</summary>
    public void Dispose() => PromotionBuilder.ClearAsync(factory).GetAwaiter().GetResult();

    private static async Task<GetMySubscriptionResponse> GetAsync(Actor coach)
    {
        var response = await coach.Http.GetAsync("/users/me/subscription", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return (await response.Content.ReadFromJsonAsync<GetMySubscriptionResponse>(JsonOptions, Ct))!;
    }

    private async Task SetCurrentPromotionAsync(long profileId, long promotionId, DateTimeOffset? endsAt)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        await db.CoachSubscriptions
            .Where(cs => cs.ProfessionalProfileId == profileId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(cs => cs.CurrentPromotionId, promotionId)
                    .SetProperty(cs => cs.CurrentPromotionEndsAt, endsAt),
                Ct);
    }

    [Fact]
    public async Task Get_LiveSubscriptionWithACurrentPromotion_ReturnsItsNameValueDurationAndEnd()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(factory);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, plan);
        var promotion = await PromotionBuilder.CreateAsync(factory, [plan], PromotionMode.Automatic, p =>
        {
            p.Value = 30;
            p.Duration = PromotionDuration.Months;
            p.DurationMonths = 3;
        });
        var endsAt = DateTimeOffset.UtcNow.AddMonths(2);
        await SetCurrentPromotionAsync(coach.ProfileId, promotion.Id, endsAt);

        var response = await GetAsync(coach);

        response.Promotion.Should().NotBeNull();
        response.Promotion!.Name.Should().Be(promotion.Name);
        response.Promotion.Kind.Should().Be(PromotionKind.Percent);
        response.Promotion.Value.Should().Be(30);
        response.Promotion.Duration.Should().Be(PromotionDuration.Months);
        response.Promotion.DurationMonths.Should().Be(3);
        response.Promotion.EndsAt.Should().BeCloseTo(endsAt, TimeSpan.FromSeconds(1));
        response.Promotion.DiscountMinorUnits.Should().Be(8970, "30 % of the 29 900 plan line");
        response.Promotion.DiscountedTotalMinorUnits.Should().Be(20930);
    }

    [Fact]
    public async Task Get_PromotionCoveringTheAddOn_DiscountsPlanAndAddOnLines()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(factory, priceMinorUnits: 29900, addOnMinorUnits: 9900, bothRolesIncluded: false);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, plan, paysSecondRoleAddOn: true);
        var promotion = await PromotionBuilder.CreateAsync(factory, [plan], PromotionMode.Automatic, p =>
        {
            p.Value = 30;
            p.CoversSecondRoleAddOn = true;
        });
        await SetCurrentPromotionAsync(coach.ProfileId, promotion.Id, null);

        var response = await GetAsync(coach);

        response.PriceLines.Should().HaveCount(2);
        response.Promotion!.DiscountMinorUnits.Should().Be(11940, "30 % of 29 900 plus 30 % of 9 900");
        response.Promotion.DiscountedTotalMinorUnits.Should().Be(27860);
    }

    [Fact]
    public async Task Get_PromotionNotCoveringTheAddOn_DiscountsOnlyThePlanLine()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(factory, priceMinorUnits: 29900, addOnMinorUnits: 9900, bothRolesIncluded: false);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, plan, paysSecondRoleAddOn: true);
        var promotion = await PromotionBuilder.CreateAsync(factory, [plan], PromotionMode.Automatic, p =>
        {
            p.Value = 30;
            p.CoversSecondRoleAddOn = false;
        });
        await SetCurrentPromotionAsync(coach.ProfileId, promotion.Id, null);

        var response = await GetAsync(coach);

        response.Promotion!.DiscountMinorUnits.Should().Be(8970);
        response.Promotion.DiscountedTotalMinorUnits.Should().Be(30830, "the add-on line keeps its full price");
    }

    [Fact]
    public async Task Get_PromotionThatDoesNotCoverTheCoachsPlan_ShowsNoDiscount()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(factory, priceMinorUnits: 29900);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, plan);
        var promotion = await PromotionBuilder.CreateAsync(factory, ["another-plan"], PromotionMode.Automatic, p => p.Value = 30);
        await SetCurrentPromotionAsync(coach.ProfileId, promotion.Id, null);

        var response = await GetAsync(coach);

        response.Promotion!.DiscountMinorUnits.Should().Be(0);
        response.Promotion.DiscountedTotalMinorUnits.Should().Be(29900);
    }

    [Fact]
    public async Task Get_ForeverPromotion_HasNoEnd()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(factory);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, plan);
        var promotion = await PromotionBuilder.CreateAsync(factory, [plan]);
        await SetCurrentPromotionAsync(coach.ProfileId, promotion.Id, null);

        (await GetAsync(coach)).Promotion!.EndsAt.Should().BeNull();
    }

    [Fact]
    public async Task Get_WithoutACurrentPromotion_ReturnsNone()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(factory);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, plan);

        (await GetAsync(coach)).Promotion.Should().BeNull();
    }

    [Fact]
    public async Task Get_PromotionOnASubscriptionThatIsNoLongerLive_IsNotShown()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(factory);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, plan, SubscriptionStatus.Canceled);
        var promotion = await PromotionBuilder.CreateAsync(factory, [plan]);
        await SetCurrentPromotionAsync(coach.ProfileId, promotion.Id, null);

        (await GetAsync(coach)).Promotion.Should().BeNull();
    }
}
