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
/// Integration tests for <c>GET /users/me/subscription</c> (<see cref="GetMySubscriptionEndpoint"/>).
/// </summary>
[Collection(TestCollection.Name)]
public class GetMySubscriptionEndpointTests(FitnessApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<GetMySubscriptionResponse> GetAsync(Actor coach)
    {
        var response = await coach.Http.GetAsync("/users/me/subscription", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return (await response.Content.ReadFromJsonAsync<GetMySubscriptionResponse>(JsonOptions, Ct))!;
    }

    [Fact]
    public async Task Get_PlanSetByFormUp_IsFlagged_WhileItApplies()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(factory);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(
            factory, coach, plan, givenFreeAt: DateTimeOffset.UtcNow.AddDays(-1), freeUntil: DateTimeOffset.UtcNow.AddDays(10));

        (await GetAsync(coach)).IsSetByFormUp.Should().BeTrue();
    }

    [Fact]
    public async Task Get_PaidPlanOrEndedGift_IsNotFlagged()
    {
        var paying = await TestActors.Trainer(factory).CreateAsync(Ct);
        var ended = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(factory);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, paying, plan);
        await CoachSubscriptionBuilder.SubscribeWithEndedGiftAsync(factory, ended, plan);

        (await GetAsync(paying)).IsSetByFormUp.Should().BeFalse();
        (await GetAsync(ended)).IsSetByFormUp.Should().BeFalse("the gift no longer applies");
    }

    [Fact]
    public async Task Get_CoachWithoutSubscription_ShowsTheFreePlanWithoutAStatus()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).CreateAsync(Ct);

        var block = await GetAsync(coach);

        block.PlanCode.Should().Be("free");
        block.Status.Should().BeNull();
        block.ActiveClientCount.Should().Be(1);
        block.ClientLimit.Should().Be(2);
        block.SecondRole.Should().Be(SecondRoleState.Included);
        block.PriceLines.Should().ContainSingle(line => line.Kind == SubscriptionPriceLineKind.Plan && line.AmountMinorUnits == 0);
        block.Features.CanCreatePlans.Should().BeTrue();
        block.Features.CanSendQuestionnaires.Should().BeFalse();
    }

    [Fact]
    public async Task Get_CoachOnAPaidPlanWithTheAddOn_ShowsBothPriceLinesAndThePlanLimit()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, "start", paysSecondRoleAddOn: true);

        var block = await GetAsync(coach);

        block.PlanCode.Should().Be("start");
        block.Status.Should().Be(SubscriptionStatus.Active);
        block.ClientLimit.Should().Be(10);
        block.SecondRole.Should().Be(SecondRoleState.PaidAddOn);
        block.PriceLines.Select(line => line.Kind).Should()
            .Equal(SubscriptionPriceLineKind.Plan, SubscriptionPriceLineKind.SecondRoleAddOn);
        block.PriceLines.Should().OnlyContain(line => line.Currency == "CZK" && line.AmountMinorUnits > 0);
    }

    [Fact]
    public async Task Get_CoachOnAPaidPlanWithoutTheAddOn_ShowsTheSecondRoleAsNotAvailable()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, "start");

        var block = await GetAsync(coach);

        block.SecondRole.Should().Be(SecondRoleState.NotAvailable);
        block.PriceLines.Should().ContainSingle();
    }

    [Fact]
    public async Task Get_CanceledSubscription_ReadsAsTheFreePlanWithTheLapsedPlanReportedSeparately()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, "studio", SubscriptionStatus.Canceled);
        var endedAt = DateTimeOffset.UtcNow.AddDays(-3);
        await SetPeriodEndAsync(coach, endedAt);
        foreach (var daysAgo in new[] { 30, 20, 10 })
        {
            var client = await TestActors.Client(factory).CreateAsync(Ct);
            await TestActors.Link(factory, coach, client).WithActivatedAtUtc(DateTime.UtcNow.AddDays(-daysAgo)).CreateAsync(Ct);
        }

        var block = await GetAsync(coach);

        block.IsLapsed.Should().BeTrue();
        block.PlanCode.Should().Be("free");
        block.Status.Should().Be(SubscriptionStatus.Canceled);
        block.LapsedPlanNameEn.Should().NotBeNullOrEmpty().And.NotBe(block.NameEn);
        block.LapsedPlanNameCs.Should().NotBeNullOrEmpty();
        block.LapsedPlanNameDe.Should().NotBeNullOrEmpty();
        block.EndedAt.Should().BeCloseTo(endedAt, TimeSpan.FromSeconds(1));
        block.ClientLimit.Should().Be(2);
        block.ActiveClientCount.Should().Be(2, "only the kept clients count against the Free plan");
        block.OverCapClientCount.Should().Be(1);
        block.Features.CanSendQuestionnaires.Should().BeFalse("a canceled subscription falls back to the Free flags");
        block.PriceLines.Should().ContainSingle(line => line.AmountMinorUnits == 0);
    }

    [Fact]
    public async Task Get_PastDueSubscription_KeepsThePaidPlanAndIsNotLapsed()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, "studio", SubscriptionStatus.PastDue);

        var block = await GetAsync(coach);

        block.IsLapsed.Should().BeFalse();
        block.PlanCode.Should().Be("studio");
        block.LapsedPlanNameEn.Should().BeNull();
        block.OverCapClientCount.Should().Be(0);
        block.FreeSlotCount.Should().Be(0);
    }

    [Fact]
    public async Task Get_RestrictedClientsAndAnEmptyKeptSlot_ReportsTheFreeSlot()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, "studio", SubscriptionStatus.Canceled);
        var states = new[] { ConnectionState.FreePlanKept, ConnectionState.FreePlanReleased, ConnectionState.FreePlanReleased };
        foreach (var state in states)
        {
            var client = await TestActors.Client(factory).CreateAsync(Ct);
            await TestActors.Link(factory, coach, client).WithState(state).CreateAsync(Ct);
        }

        var block = await GetAsync(coach);

        block.ClientLimit.Should().Be(2);
        block.OverCapClientCount.Should().Be(2);
        block.FreeSlotCount.Should().Be(1);
    }

    [Fact]
    public async Task Get_RestrictedClientsAndEveryKeptSlotTaken_ReportsNoFreeSlot()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, "studio", SubscriptionStatus.Canceled);
        foreach (var daysAgo in new[] { 30, 20, 10 })
        {
            var client = await TestActors.Client(factory).CreateAsync(Ct);
            await TestActors.Link(factory, coach, client).WithActivatedAtUtc(DateTime.UtcNow.AddDays(-daysAgo)).CreateAsync(Ct);
        }

        var block = await GetAsync(coach);

        block.OverCapClientCount.Should().Be(1);
        block.FreeSlotCount.Should().Be(0);
    }

    [Fact]
    public async Task Get_CoachOnAnUnlimitedPlanOrWithinTheCap_ReportsNoFreeSlot()
    {
        var unlimited = await TestActors.Trainer(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, unlimited, "studio");
        var withinCap = await TestActors.Trainer(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, withinCap, client).CreateAsync(Ct);

        (await GetAsync(unlimited)).FreeSlotCount.Should().Be(0);
        (await GetAsync(withinCap)).FreeSlotCount.Should().Be(0);
    }

    private async Task SetPeriodEndAsync(Actor coach, DateTimeOffset periodEnd)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = await db.CoachSubscriptions.SingleAsync(cs => cs.ProfessionalProfileId == coach.ProfileId, Ct);
        row.CurrentPeriodEndsAt = periodEnd;
        await db.SaveChangesAsync(Ct);
    }
}
