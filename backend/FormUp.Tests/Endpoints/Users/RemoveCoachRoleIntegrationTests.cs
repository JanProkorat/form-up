using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Endpoints.Billing;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Users;

/// <summary>
/// Integration tests for <c>DELETE /users/me/roles/{Role}</c>.
/// </summary>
[Collection(TestCollection.Shard3)]
public class RemoveCoachRoleIntegrationTests(FitnessApiFactory factory) : IDisposable
{
    /// <summary>Leaves the shared fake provider in its defaults for the next test class.</summary>
    public void Dispose() => BillingTestData.ResetProvider(factory);

    private record RoleSummary(string Role, int ClientCount, int SharedWithOtherRoleCount);

    private record RemoveResult(string RemovedRole, List<RoleSummary> Roles);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Remove_DualRoleTrainer_MarksRemoved_KeepsIdentityRole_AndLeavesLinkFlags()
    {
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        var linkId = await TestActors.Link(factory, coach, client).CreateAsync(Ct);

        var response = await coach.Http.DeleteAsync("/users/me/roles/Trainer", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var profile = await db.ProfessionalProfiles.AsNoTracking().FirstAsync(p => p.UserId == coach.UserId, Ct);
        profile.TrainerRoleRemovedAt.Should().NotBeNull();
        profile.NutritionistRoleRemovedAt.Should().BeNull();

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByIdAsync(coach.UserId.ToString());
        (await userManager.GetRolesAsync(user!)).Should().Contain(AppRoles.Trainer);

        var link = await db.ClientProfessionalLinks.AsNoTracking().FirstAsync(l => l.Id == linkId, Ct);
        link.ConnectionState.Should().Be(ConnectionState.Connected);
        link.CanViewTrainingPlans.Should().BeTrue();
        link.CanViewNutritionPlans.Should().BeTrue();
    }

    [Fact]
    public async Task Remove_ReturnsCountsAsTheyStoodBeforeRemoval()
    {
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        var trainingOnly = await TestActors.Client(factory).CreateAsync(Ct);
        var both = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, trainingOnly).CanViewNutritionPlans(false).CreateAsync(Ct);
        await TestActors.Link(factory, coach, both).CreateAsync(Ct);

        var response = await coach.Http.DeleteAsync("/users/me/roles/Trainer", Ct);

        var body = await response.Content.ReadFromJsonAsync<RemoveResult>(cancellationToken: Ct);
        body!.RemovedRole.Should().Be(AppRoles.Trainer);
        body.Roles.Single(r => r.Role == AppRoles.Trainer).Should().Be(new RoleSummary(AppRoles.Trainer, 2, 1));
        body.Roles.Single(r => r.Role == AppRoles.Nutritionist).Should().Be(new RoleSummary(AppRoles.Nutritionist, 1, 1));
    }

    [Fact]
    public async Task Remove_NotifiesOnlyClientsWhoseActiveLinkGrantsTheRemovedDiscipline()
    {
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        var trainingClient = await TestActors.Client(factory).CreateAsync(Ct);
        var nutritionOnlyClient = await TestActors.Client(factory).CreateAsync(Ct);
        var endedLinkClient = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, trainingClient).CreateAsync(Ct);
        await TestActors.Link(factory, coach, nutritionOnlyClient).CanViewTrainingPlans(false).CreateAsync(Ct);
        await TestActors.Link(factory, coach, endedLinkClient).Inactive().CreateAsync(Ct);

        var response = await coach.Http.DeleteAsync("/users/me/roles/Trainer", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await CountRoleRemovedNotificationsAsync(trainingClient.UserId)).Should().Be(1);
        (await CountRoleRemovedNotificationsAsync(nutritionOnlyClient.UserId)).Should().Be(0);
        (await CountRoleRemovedNotificationsAsync(endedLinkClient.UserId)).Should().Be(0);
    }

    private async Task<int> CountRoleRemovedNotificationsAsync(Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Notifications.AsNoTracking()
            .CountAsync(n => n.RecipientUserId == userId && n.Type == NotificationType.CoachRoleRemoved, Ct);
    }

    [Fact]
    public async Task Remove_OnlyActiveCoachRole_Returns400OnlyCoachRole()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);

        var response = await coach.Http.DeleteAsync("/users/me/roles/Trainer", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.OnlyCoachRole);
    }

    [Fact]
    public async Task Remove_OnlyCoachRoleWhileAlsoClient_StillReturns400OnlyCoachRole()
    {
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Client).CreateAsync(Ct);

        var response = await coach.Http.DeleteAsync("/users/me/roles/Trainer", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.OnlyCoachRole);
    }

    [Fact]
    public async Task Remove_RoleNotHeld_Returns400RoleNotAssigned()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);

        var response = await coach.Http.DeleteAsync("/users/me/roles/Nutritionist", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.RoleNotAssigned);
    }

    [Fact]
    public async Task Remove_AlreadyRemovedRole_Returns400RoleNotAssigned()
    {
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        (await coach.Http.DeleteAsync("/users/me/roles/Trainer", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await coach.Http.DeleteAsync("/users/me/roles/Trainer", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.RoleNotAssigned);
    }

    [Fact]
    public async Task Remove_NonCoachRole_Returns400()
    {
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);

        var response = await coach.Http.DeleteAsync("/users/me/roles/Admin", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private sealed record PayingCoach(Actor Coach, string PlanCode, string PriceId, string SubscriptionId, DateTimeOffset PeriodEnd);

    /// <summary>A two-role coach on a live paid plan that bills the second-role add-on.</summary>
    private async Task<PayingCoach> ArrangePayingAddOnAsync(
        bool bothRolesIncluded = false, bool paysAddOn = true, bool cancelAtPeriodEnd = false)
    {
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        var (plan, priceId) = await BillingTestData.StripePlanAsync(
            factory, price: 29900, addOn: bothRolesIncluded ? null : 9900, bothRolesIncluded: bothRolesIncluded);
        var periodEnd = DateTimeOffset.UtcNow.AddDays(12);
        var subscriptionId = await BillingTestData.SubscribeLiveAsync(
            factory, coach, plan, paysSecondRoleAddOn: paysAddOn, cancelAtPeriodEnd: cancelAtPeriodEnd, currentPeriodEndsAt: periodEnd);

        return new PayingCoach(coach, plan, priceId, subscriptionId, periodEnd);
    }

    private async Task<DateTime?> TrainerRemovedAtAsync(Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.ProfessionalProfiles.AsNoTracking()
            .Where(p => p.UserId == userId)
            .Select(p => p.TrainerRoleRemovedAt)
            .SingleAsync(Ct);
    }

    [Fact]
    public async Task Remove_WhilePayingTheAddOn_SchedulesItsEndAtRenewal_AndRecordsTheDate()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var arranged = await ArrangePayingAddOnAsync();

        var response = await arranged.Coach.Http.DeleteAsync("/users/me/roles/Trainer", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.PlanChanges.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new FakeSubscriptionService.ChangePlanCall(
                arranged.SubscriptionId, arranged.PlanCode, arranged.PriceId, AtNextRenewal: true, AddOnAmountMinorUnits: null));
        var row = await BillingTestData.ReadRowAsync(factory, arranged.Coach.ProfileId);
        row.SecondRoleAddOnEndsAt.Should().BeCloseTo(arranged.PeriodEnd, TimeSpan.FromMilliseconds(1));
        row.PaysSecondRoleAddOn.Should().BeTrue("the add-on is billed until the renewal; the webhook clears it then");
        (await TrainerRemovedAtAsync(arranged.Coach.UserId)).Should().NotBeNull();
    }

    [Fact]
    public async Task Remove_WithAScheduledDowngrade_SchedulesTheAddOnEndOnThePendingPlan_AndKeepsThePendingPrice()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var arranged = await ArrangePayingAddOnAsync();
        var (downgrade, downgradePriceId) = await BillingTestData.StripePlanAsync(factory, price: 19900, addOn: 4900, bothRolesIncluded: false);
        await BillingTestData.SetPendingPlanAsync(factory, arranged.Coach.ProfileId, downgrade);
        var pendingBefore = (await BillingTestData.ReadRowAsync(factory, arranged.Coach.ProfileId)).PendingSubscriptionPlanPriceId;

        var response = await arranged.Coach.Http.DeleteAsync("/users/me/roles/Trainer", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.PlanChanges.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new FakeSubscriptionService.ChangePlanCall(
                arranged.SubscriptionId, downgrade, downgradePriceId, AtNextRenewal: true, AddOnAmountMinorUnits: null));
        var row = await BillingTestData.ReadRowAsync(factory, arranged.Coach.ProfileId);
        row.PendingSubscriptionPlanPriceId.Should().Be(pendingBefore);
        row.SecondRoleAddOnEndsAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Remove_OnAPlanThatIncludesBothRoles_CallsNoProvider_AndSchedulesNothing()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var arranged = await ArrangePayingAddOnAsync(bothRolesIncluded: true);

        var response = await arranged.Coach.Http.DeleteAsync("/users/me/roles/Trainer", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.PlanChanges.Should().BeEmpty();
        (await BillingTestData.ReadRowAsync(factory, arranged.Coach.ProfileId)).SecondRoleAddOnEndsAt.Should().BeNull();
    }

    [Fact]
    public async Task Remove_WithoutAPaidAddOn_CallsNoProvider_AndSchedulesNothing()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var arranged = await ArrangePayingAddOnAsync(paysAddOn: false);

        var response = await arranged.Coach.Http.DeleteAsync("/users/me/roles/Trainer", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.PlanChanges.Should().BeEmpty();
        (await BillingTestData.ReadRowAsync(factory, arranged.Coach.ProfileId)).SecondRoleAddOnEndsAt.Should().BeNull();
    }

    [Fact]
    public async Task Remove_WhileTheSubscriptionEndsAnyway_CallsNoProvider_AndSchedulesNothing()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var arranged = await ArrangePayingAddOnAsync(cancelAtPeriodEnd: true);

        var response = await arranged.Coach.Http.DeleteAsync("/users/me/roles/Trainer", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.PlanChanges.Should().BeEmpty();
        (await BillingTestData.ReadRowAsync(factory, arranged.Coach.ProfileId)).SecondRoleAddOnEndsAt.Should().BeNull();
    }

    [Fact]
    public async Task Remove_WhenTheProviderFails_Returns502_AndChangesNothing()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var arranged = await ArrangePayingAddOnAsync();
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, arranged.Coach, client).CreateAsync(Ct);
        fake.FailPlanChanges = true;

        var response = await arranged.Coach.Http.DeleteAsync("/users/me/roles/Trainer", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.BillingProviderError);
        (await TrainerRemovedAtAsync(arranged.Coach.UserId)).Should().BeNull();
        (await BillingTestData.ReadRowAsync(factory, arranged.Coach.ProfileId)).SecondRoleAddOnEndsAt.Should().BeNull();
        (await CountRoleRemovedNotificationsAsync(client.UserId)).Should().Be(0);
    }

    [Fact]
    public async Task Remove_WhenBillingIsNotConfigured_Returns503_AndKeepsTheRole()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var arranged = await ArrangePayingAddOnAsync();
        fake.IsConfigured = false;

        var response = await arranged.Coach.Http.DeleteAsync("/users/me/roles/Trainer", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.BillingNotConfigured);
        (await TrainerRemovedAtAsync(arranged.Coach.UserId)).Should().BeNull();
    }
}
