using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.AdminOverview.GetAdminAttention;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.AdminOverview;

/// <summary>
/// Integration tests for <c>GET /admin/overview/attention</c> (<c>GetAdminAttentionEndpoint</c>). The endpoint counts
/// whole tables, so each test clears coach subscriptions and promotions first. There is no fake clock: boundary dates
/// are seeded one minute inside or outside the 14-day horizon.
/// </summary>
[Collection(TestCollection.Name)]
public class GetAdminAttentionEndpointTests(FitnessApiFactory factory) : IDisposable
{
    private const string Route = "/admin/overview/attention";
    private const int HorizonMinutes = 14 * 24 * 60;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Leaves the subscriptions and promotions tables empty for the next test class.</summary>
    public void Dispose() => ClearAsync().GetAwaiter().GetResult();

    private async Task ClearAsync()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.CoachSubscriptions.ExecuteDeleteAsync(Ct);
        }

        await PromotionBuilder.ClearAsync(factory);
    }

    private async Task<GetAdminAttentionResponse> GetAsync()
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var response = await admin.Http.GetAsync(Route, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));

        return (await response.Content.ReadFromJsonAsync<GetAdminAttentionResponse>(JsonOptions, Ct))!;
    }

    private async Task<Actor> NewCoachAsync(string? lastName = null) =>
        lastName is null
            ? await TestActors.Trainer(factory).CreateAsync(Ct)
            : await TestActors.Trainer(factory).WithName("First", lastName).CreateAsync(Ct);

    [Fact]
    public async Task Get_WithNoCoachesOrPromotions_ReturnsZerosEmptyListsAndNulls()
    {
        await ClearAsync();

        var body = await GetAsync();

        body.PaymentFailedCount.Should().Be(0);
        body.PaymentFailedCoaches.Should().BeEmpty();
        body.CancellingCount.Should().Be(0);
        body.CancellingEarliestPeriodEndsAt.Should().BeNull();
        body.CancellingLatestPeriodEndsAt.Should().BeNull();
        body.GivenFreeEndingSoonCount.Should().Be(0);
        body.PromotionsEndingSoon.Should().BeEmpty();
    }

    [Fact]
    public async Task Get_WithFourFailedPayments_CountsFourAndNamesTheFirstThreeInOrder()
    {
        await ClearAsync();
        var plan = await PaidPlanBuilder.CreateAsync(factory);
        var coaches = new List<Actor>();

        foreach (var lastName in new[] { "Delta", "Alpha", "Charlie", "Bravo" })
        {
            var coach = await NewCoachAsync(lastName);
            await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, plan, SubscriptionStatus.PastDue);
            coaches.Add(coach);
        }

        var body = await GetAsync();

        body.PaymentFailedCount.Should().Be(4);
        body.PaymentFailedCoaches.Select(coach => coach.Name).Should().Equal("First Alpha", "First Bravo", "First Charlie");
        body.PaymentFailedCoaches[0].CoachId.Should().Be(coaches[1].UserId);
    }

    [Fact]
    public async Task Get_ForAPastDueCoachWhoCancelledRenewal_CountsPaymentFailedOnly()
    {
        await ClearAsync();
        var plan = await PaidPlanBuilder.CreateAsync(factory);
        var coach = await NewCoachAsync();
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(
            factory, coach, plan, SubscriptionStatus.PastDue, cancelAtPeriodEnd: true,
            currentPeriodEndsAt: DateTimeOffset.UtcNow.AddDays(5));

        var body = await GetAsync();

        body.PaymentFailedCount.Should().Be(1);
        body.CancellingCount.Should().Be(0);
        body.CancellingEarliestPeriodEndsAt.Should().BeNull();
    }

    [Fact]
    public async Task Get_WithCancellingCoaches_ReturnsCountAndEarliestAndLatestPeriodEnd()
    {
        await ClearAsync();
        var plan = await PaidPlanBuilder.CreateAsync(factory);
        var now = DateTimeOffset.UtcNow;
        var earliest = now.AddDays(3);
        var latest = now.AddDays(20);

        foreach (var endsAt in new[] { now.AddDays(10), earliest, latest })
        {
            var coach = await NewCoachAsync();
            await CoachSubscriptionBuilder.SubscribeToPlanAsync(
                factory, coach, plan, cancelAtPeriodEnd: true, currentPeriodEndsAt: endsAt);
        }

        var renewing = await NewCoachAsync();
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(
            factory, renewing, plan, currentPeriodEndsAt: now.AddDays(1));

        var body = await GetAsync();

        body.CancellingCount.Should().Be(3);
        body.PaymentFailedCount.Should().Be(0);
        body.CancellingEarliestPeriodEndsAt.Should().BeCloseTo(earliest, TimeSpan.FromSeconds(1));
        body.CancellingLatestPeriodEndsAt.Should().BeCloseTo(latest, TimeSpan.FromSeconds(1));
    }

    [Theory]
    [InlineData(HorizonMinutes - 1, 1)]
    [InlineData(HorizonMinutes + 1, 0)]
    [InlineData(null, 0)]
    [InlineData(-24 * 60, 0)]
    public async Task Get_ForAGivenFreePlan_CountsOnlyAnEndWithinFourteenDays(int? endsInMinutes, int expectedCount)
    {
        await ClearAsync();
        var plan = await PaidPlanBuilder.CreateAsync(factory);
        var now = DateTimeOffset.UtcNow;
        var coach = await NewCoachAsync();
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(
            factory, coach, plan,
            givenFreeAt: now.AddDays(-60),
            freeUntil: endsInMinutes is { } minutes ? now.AddMinutes(minutes) : null);

        var body = await GetAsync();

        body.GivenFreeEndingSoonCount.Should().Be(expectedCount);
    }

    [Fact]
    public async Task Get_ForPromotionUsage_IncludesFromNinetyPercentUsingIntegerMath()
    {
        await ClearAsync();
        var coaches = new List<Actor>();

        for (var index = 0; index < 10; index++)
        {
            coaches.Add(await NewCoachAsync());
        }

        async Task<Guid> SeedAsync(int maxUses, int used)
        {
            var promotion = await PromotionBuilder.CreateAsync(factory, ["pro"], configure: p => p.MaxUses = maxUses);

            foreach (var coach in coaches.Take(used))
            {
                await PromotionBuilder.AddRedemptionAsync(factory, promotion.Id, coach.ProfileId);
            }

            return promotion.PublicId;
        }

        var ninetyPercent = await SeedAsync(maxUses: 10, used: 9);
        await SeedAsync(maxUses: 10, used: 8);
        await SeedAsync(maxUses: 11, used: 9);
        var aboveNinety = await SeedAsync(maxUses: 11, used: 10);
        var unlimited = await PromotionBuilder.CreateAsync(factory, ["pro"]);

        var body = await GetAsync();

        body.PromotionsEndingSoon.Select(item => item.PromotionId)
            .Should().BeEquivalentTo([ninetyPercent, aboveNinety]);
        body.PromotionsEndingSoon.Should().NotContain(item => item.PromotionId == unlimited.PublicId);
        body.PromotionsEndingSoon.Single(item => item.PromotionId == ninetyPercent)
            .Should().Match<AdminAttentionPromotionDto>(item => item.UsedCount == 9 && item.MaxUses == 10 && item.ClaimUntil == null);
    }

    [Fact]
    public async Task Get_ForPromotionWindow_IncludesAnActiveEndWithinFourteenDaysOnly()
    {
        await ClearAsync();
        var now = DateTimeOffset.UtcNow;
        var coach = await NewCoachAsync();

        async Task<Promotion> SeedAsync(Action<Promotion> configure) =>
            await PromotionBuilder.CreateAsync(factory, ["pro"], configure: configure);

        var inside = await SeedAsync(p => p.ClaimUntil = now.AddMinutes(HorizonMinutes - 1));
        await SeedAsync(p => p.ClaimUntil = now.AddMinutes(HorizonMinutes + 1));
        await SeedAsync(_ => { });
        await SeedAsync(p => p.ClaimUntil = now.AddDays(-1));
        await SeedAsync(p =>
        {
            p.ClaimUntil = now.AddDays(2);
            p.EndedAt = now.AddMinutes(-5);
        });
        await SeedAsync(p =>
        {
            p.ClaimFrom = now.AddDays(1);
            p.ClaimUntil = now.AddDays(2);
        });
        var usedUp = await SeedAsync(p =>
        {
            p.ClaimUntil = now.AddDays(2);
            p.MaxUses = 1;
        });
        await PromotionBuilder.AddRedemptionAsync(factory, usedUp.Id, coach.ProfileId);
        var both = await SeedAsync(p =>
        {
            p.ClaimUntil = now.AddDays(1);
            p.MaxUses = 10;
        });

        for (var index = 0; index < 9; index++)
        {
            var redeemer = await NewCoachAsync();
            await PromotionBuilder.AddRedemptionAsync(factory, both.Id, redeemer.ProfileId);
        }

        var body = await GetAsync();

        body.PromotionsEndingSoon.Select(item => item.PromotionId)
            .Should().Equal(both.PublicId, inside.PublicId);
    }
}
