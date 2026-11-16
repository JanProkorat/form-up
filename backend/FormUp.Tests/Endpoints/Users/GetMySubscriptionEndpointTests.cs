using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.Users.GetMySubscription;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Services.Billing;
using FormUp.Tests.Builders;
using FormUp.Tests.Endpoints.Billing;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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
    public async Task Get_PastDueWithARetryDate_ReportsItWithTheBillingAccountAndTheTaxMode()
    {
        var fake = BillingTestData.ResetProvider(factory);
        fake.IsConfigured = false;
        var options = factory.Services.GetRequiredService<IOptions<BillingOptions>>().Value;
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(factory);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, plan, SubscriptionStatus.PastDue);
        var retryAt = DateTimeOffset.UtcNow.AddDays(3);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.CoachSubscriptions.Where(cs => cs.ProfessionalProfileId == coach.ProfileId)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(cs => cs.NextPaymentAttemptAt, retryAt).SetProperty(cs => cs.ExternalCustomerId, "cus_billing"),
                    Ct);
        }

        var original = options.TaxMode;

        try
        {
            options.TaxMode = BillingTaxMode.Vat21;

            var block = await GetAsync(coach);

            block.Status.Should().Be(SubscriptionStatus.PastDue);
            block.NextPaymentAttemptAt.Should().BeCloseTo(retryAt, TimeSpan.FromSeconds(1));
            block.HasBillingAccount.Should().BeTrue();
            block.TaxMode.Should().Be(BillingTaxMode.Vat21);
        }
        finally
        {
            options.TaxMode = original;
            BillingTestData.ResetProvider(factory);
        }
    }

    [Fact]
    public async Task Get_CoachWithoutABillingCustomer_HasNoBillingAccountAndNoRetryDate()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(factory);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, plan);

        var block = await GetAsync(coach);

        block.HasBillingAccount.Should().BeFalse();
        block.NextPaymentAttemptAt.Should().BeNull();
        block.TaxMode.Should().Be(BillingTaxMode.None);
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

    [Fact]
    public async Task Get_WithADowngradeScheduled_ReturnsTheNextPlanItsPriceAndDate()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (current, _) = await BillingTestData.StripePlanAsync(factory, price: 59900);
        var (next, _) = await BillingTestData.StripePlanAsync(factory, price: 29900);
        var periodEnd = DateTimeOffset.UtcNow.AddDays(12);
        await BillingTestData.SubscribeLiveAsync(factory, coach, current, currentPeriodEndsAt: periodEnd);
        await BillingTestData.SetPendingPlanAsync(factory, coach.ProfileId, next);

        var block = await GetAsync(coach);

        block.PlanCode.Should().Be(current);
        block.ScheduledPlan.Should().NotBeNull();
        var scheduled = block.ScheduledPlan!;
        scheduled.PlanCode.Should().Be(next);
        scheduled.StartsAt.Should().BeCloseTo(periodEnd, TimeSpan.FromSeconds(1));
        scheduled.PriceLines.Should().ContainSingle().Which.AmountMinorUnits.Should().Be(29900);
    }

    [Fact]
    public async Task Get_WithADowngradeScheduledToAPlanWithAnAddOn_AddsTheLineOnlyForATwoRoleCoach()
    {
        var twoRoles = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        var oneRole = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (current, _) = await BillingTestData.StripePlanAsync(factory, price: 59900, bothRolesIncluded: true);
        var (next, _) = await BillingTestData.StripePlanAsync(factory, price: 29900, addOn: 9900, bothRolesIncluded: false);

        foreach (var coach in new[] { twoRoles, oneRole })
        {
            await BillingTestData.SubscribeLiveAsync(factory, coach, current);
            await BillingTestData.SetPendingPlanAsync(factory, coach.ProfileId, next);
        }

        (await GetAsync(twoRoles)).ScheduledPlan!.PriceLines.Select(line => line.Kind)
            .Should().Equal(SubscriptionPriceLineKind.Plan, SubscriptionPriceLineKind.SecondRoleAddOn);
        (await GetAsync(oneRole)).ScheduledPlan!.PriceLines.Should().ContainSingle();
    }

    [Fact]
    public async Task Get_WithASwitchToFreeScheduled_ReturnsFreeAsTheNextPlan()
    {
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (current, _) = await BillingTestData.StripePlanAsync(factory, price: 29900);
        var periodEnd = DateTimeOffset.UtcNow.AddDays(9);
        await BillingTestData.SubscribeLiveAsync(factory, coach, current, cancelAtPeriodEnd: true, currentPeriodEndsAt: periodEnd);

        var scheduled = (await GetAsync(coach)).ScheduledPlan;

        scheduled.Should().NotBeNull();
        scheduled!.PlanCode.Should().Be("free");
        scheduled.StartsAt.Should().BeCloseTo(periodEnd, TimeSpan.FromSeconds(1));
        scheduled.PriceLines.Should().ContainSingle().Which.AmountMinorUnits.Should().Be(0);
    }

    [Fact]
    public async Task Get_WithAScheduledDowngradeOrOwnSwitchToFree_IsCancellable()
    {
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);
        var downgrading = await TestActors.Trainer(factory).CreateAsync(Ct);
        var leaving = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (current, _) = await BillingTestData.StripePlanAsync(factory, price: 59900);
        var (next, _) = await BillingTestData.StripePlanAsync(factory, price: 29900);
        await BillingTestData.SubscribeLiveAsync(factory, downgrading, current);
        await BillingTestData.SetPendingPlanAsync(factory, downgrading.ProfileId, next);
        await BillingTestData.SubscribeLiveAsync(factory, leaving, current, cancelAtPeriodEnd: true);

        (await GetAsync(downgrading)).ScheduledPlan!.Cancellable.Should().BeTrue();
        (await GetAsync(leaving)).ScheduledPlan!.Cancellable.Should().BeTrue();
    }

    [Fact]
    public async Task Get_WhenAnAdminCancelledTheRenewalOrTheAccountIsBeingDisabled_IsNotCancellable()
    {
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);
        var byAdmin = await TestActors.Trainer(factory).CreateAsync(Ct);
        var disabling = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (current, _) = await BillingTestData.StripePlanAsync(factory, price: 29900);
        await BillingTestData.SubscribeLiveAsync(factory, byAdmin, current, cancelAtPeriodEnd: true);
        await BillingTestData.UpdateRowAsync(
            factory, byAdmin.ProfileId, row => row.RenewalCancelledByAdminAt = DateTimeOffset.UtcNow);
        await BillingTestData.SubscribeLiveAsync(factory, disabling, current, cancelAtPeriodEnd: true);
        await BillingTestData.SetDisablingAsync(factory, disabling.ProfileId);

        (await GetAsync(byAdmin)).ScheduledPlan!.Cancellable.Should().BeFalse();
        (await GetAsync(disabling)).ScheduledPlan!.Cancellable.Should().BeFalse();
    }

    private async Task SetAddOnEndAsync(Actor coach, DateTimeOffset endsAt)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        await db.CoachSubscriptions
            .Where(cs => cs.ProfessionalProfileId == coach.ProfileId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(cs => cs.SecondRoleAddOnEndsAt, endsAt), Ct);
    }

    [Fact]
    public async Task Get_WithTheAddOnEndScheduled_ReportsTheDate_AndKeepsTheAddOnLineUntilThen()
    {
        var coach = await TestActors.Professional(factory, UserRole.Trainer).CreateAsync(Ct);
        var (plan, _) = await BillingTestData.StripePlanAsync(factory, price: 29900, addOn: 9900, bothRolesIncluded: false);
        var periodEnd = DateTimeOffset.UtcNow.AddDays(12);
        await BillingTestData.SubscribeLiveAsync(factory, coach, plan, paysSecondRoleAddOn: true, currentPeriodEndsAt: periodEnd);
        await SetAddOnEndAsync(coach, periodEnd);

        var block = await GetAsync(coach);

        block.SecondRoleAddOnEndsAt.Should().BeCloseTo(periodEnd, TimeSpan.FromSeconds(1));
        block.PriceLines.Select(line => line.Kind).Should().Equal(SubscriptionPriceLineKind.Plan, SubscriptionPriceLineKind.SecondRoleAddOn);
        block.ScheduledPlan.Should().BeNull("the plan itself does not change");
    }

    [Fact]
    public async Task Get_WithTheAddOnEndScheduledWhileTheSubscriptionEndsAnyway_ReportsNoAddOnEnd()
    {
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);
        var coach = await TestActors.Professional(factory, UserRole.Trainer).CreateAsync(Ct);
        var (plan, _) = await BillingTestData.StripePlanAsync(factory, price: 29900, addOn: 9900, bothRolesIncluded: false);
        var periodEnd = DateTimeOffset.UtcNow.AddDays(12);
        await BillingTestData.SubscribeLiveAsync(
            factory, coach, plan, paysSecondRoleAddOn: true, cancelAtPeriodEnd: true, currentPeriodEndsAt: periodEnd);
        await SetAddOnEndAsync(coach, periodEnd);

        (await GetAsync(coach)).SecondRoleAddOnEndsAt.Should().BeNull();
    }

    [Fact]
    public async Task Get_WithNothingScheduled_HasNoScheduledPlan()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (current, _) = await BillingTestData.StripePlanAsync(factory, price: 29900);
        await BillingTestData.SubscribeLiveAsync(factory, coach, current);

        (await GetAsync(coach)).ScheduledPlan.Should().BeNull();
    }

    [Fact]
    public async Task Get_AfterAnUpgradeDroppedTheSchedule_HasNoScheduledPlan()
    {
        BillingTestData.ResetProvider(factory);
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (current, _) = await BillingTestData.StripePlanAsync(factory, price: 29900);
        var (target, _) = await BillingTestData.StripePlanAsync(factory, price: 59900);
        await BillingTestData.SubscribeLiveAsync(factory, coach, current, cancelAtPeriodEnd: true);
        (await GetAsync(coach)).ScheduledPlan.Should().NotBeNull();

        var change = await coach.Http.PostAsJsonAsync("/billing/change-plan", new { PlanCode = target, AttemptId = Guid.NewGuid() }, Ct);

        change.StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetAsync(coach)).ScheduledPlan.Should().BeNull("the upgrade clears the pending switch to Free");
        BillingTestData.ResetProvider(factory);
    }

    [Fact]
    public async Task Get_OneRoleCoachOnALivePlanWithAnAddOnPrice_ShowsTheSecondRoleAsPurchasable()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await BillingTestData.StripePlanAsync(factory, addOn: 9900, bothRolesIncluded: false);
        await BillingTestData.SubscribeLiveAsync(factory, coach, plan);

        (await GetAsync(coach)).SecondRole.Should().Be(SecondRoleState.Purchasable);
    }

    [Fact]
    public async Task Get_AdminGiftInForce_ShowsTheSecondRoleAsNotAvailable()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await BillingTestData.StripePlanAsync(factory, addOn: 9900, bothRolesIncluded: false);
        await BillingTestData.SubscribeLiveAsync(
            factory, coach, plan, givenFreeAt: DateTimeOffset.UtcNow.AddDays(-1), freeUntil: DateTimeOffset.UtcNow.AddDays(10));

        (await GetAsync(coach)).SecondRole.Should().Be(SecondRoleState.NotAvailable);
    }

    [Fact]
    public async Task Get_PlanWithoutAnAddOnPrice_ShowsTheSecondRoleAsNotAvailable()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await BillingTestData.StripePlanAsync(factory, addOn: null, bothRolesIncluded: false);
        await BillingTestData.SubscribeLiveAsync(factory, coach, plan);

        (await GetAsync(coach)).SecondRole.Should().Be(SecondRoleState.NotAvailable);
    }

    [Fact]
    public async Task Get_PlanWithoutAProviderSubscription_ShowsTheSecondRoleAsNotAvailable()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await BillingTestData.StripePlanAsync(factory, addOn: 9900, bothRolesIncluded: false);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, plan);

        (await GetAsync(coach)).SecondRole.Should().Be(SecondRoleState.NotAvailable);
    }

    [Fact]
    public async Task Get_AddOnAlreadyPaid_ShowsTheSecondRoleAsPaidAddOn()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await BillingTestData.StripePlanAsync(factory, addOn: 9900, bothRolesIncluded: false);
        await BillingTestData.SubscribeLiveAsync(factory, coach, plan, paysSecondRoleAddOn: true);

        (await GetAsync(coach)).SecondRole.Should().Be(SecondRoleState.PaidAddOn);
    }

    [Fact]
    public async Task Get_PlanChangeScheduled_ShowsTheSecondRoleAsNotAvailable()
    {
        var pendingDowngrade = await TestActors.Trainer(factory).CreateAsync(Ct);
        var switchToFree = await TestActors.Trainer(factory).CreateAsync(Ct);
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);
        var (current, _) = await BillingTestData.StripePlanAsync(factory, price: 59900, addOn: 9900, bothRolesIncluded: false);
        var (next, _) = await BillingTestData.StripePlanAsync(factory, price: 29900, addOn: 9900, bothRolesIncluded: false);
        await BillingTestData.SubscribeLiveAsync(factory, pendingDowngrade, current);
        await BillingTestData.SetPendingPlanAsync(factory, pendingDowngrade.ProfileId, next);
        await BillingTestData.SubscribeLiveAsync(factory, switchToFree, current, cancelAtPeriodEnd: true);

        (await GetAsync(pendingDowngrade)).SecondRole.Should().Be(SecondRoleState.NotAvailable);
        (await GetAsync(switchToFree)).SecondRole.Should().Be(SecondRoleState.NotAvailable);
    }
}
