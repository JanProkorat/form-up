using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Endpoints.Billing;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Users;

/// <summary>
/// Integration tests for <c>POST /users/me/roles</c> using a real PostgreSQL instance
/// (Testcontainers). Covers the defense-in-depth allow-list introduced in issue #308:
/// <list type="bullet">
///   <item>AC2 — Trainer-token cannot self-promote to Admin (400).</item>
///   <item>AC3 — Nutritionist-token cannot add Client role (400).</item>
///   <item>Positive path — Trainer may add Nutritionist (200 + fresh tokens).</item>
/// </list>
/// </summary>
[Collection(TestCollection.Name)]
public class AddRoleEndpointIntegrationTests(FitnessApiFactory factory) : IDisposable
{
    /// <summary>Leaves the shared fake provider in its defaults for the next test class.</summary>
    public void Dispose() => BillingTestData.ResetProvider(factory);

    private static string UniqueEmail() => $"{Guid.NewGuid():N}@addrole-test.com";
    private const string TestPassword = "TestPass1!";

    // ── AC2: Trainer cannot add Admin role ───────────────────────────────────

    [Fact]
    public async Task Post_RolesMe_AsTrainer_WithAdminRole_Returns400()
    {
        var client = factory.CreateClient();
        var email = UniqueEmail();

        await TestHelpers.RegisterAsync(client, email, TestPassword, "Alice", "Trainer", "Trainer");
        var (token, _) = await TestHelpers.LoginAsync(client, email, TestPassword);
        TestHelpers.SetBearerToken(client, token);

        var resp = await client.PostAsJsonAsync(
            "/users/me/roles",
            new { Role = "Admin" },
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── AC3: Nutritionist cannot add Client role ─────────────────────────────

    [Fact]
    public async Task Post_RolesMe_AsNutritionist_WithClientRole_Returns400()
    {
        var client = factory.CreateClient();
        var email = UniqueEmail();

        await TestHelpers.RegisterAsync(client, email, TestPassword, "Bob", "Nutri", "Nutritionist");
        var (token, _) = await TestHelpers.LoginAsync(client, email, TestPassword);
        TestHelpers.SetBearerToken(client, token);

        var resp = await client.PostAsJsonAsync(
            "/users/me/roles",
            new { Role = "Client" },
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── Positive path: Trainer may add Nutritionist ──────────────────────────

    [Fact]
    public async Task Post_RolesMe_AsTrainer_WithNutritionist_Returns200()
    {
        var client = factory.CreateClient();
        var email = UniqueEmail();

        await TestHelpers.RegisterAsync(client, email, TestPassword, "Carol", "Trainer", "Trainer");
        var (token, _) = await TestHelpers.LoginAsync(client, email, TestPassword);
        TestHelpers.SetBearerToken(client, token);

        var resp = await client.PostAsJsonAsync(
            "/users/me/roles",
            new { Role = "Nutritionist" },
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<AddRoleResult>(
            cancellationToken: TestContext.Current.CancellationToken);

        body.Should().NotBeNull();
        body!.AddedRole.Should().Be("Nutritionist");
        body.AccessToken.Should().NotBeNullOrEmpty();
    }

    // ── Add-back: a held-but-removed role is restored ────────────────────────

    [Fact]
    public async Task Post_RolesMe_AddingBackRemovedRole_Returns200_AndClearsMarker()
    {
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist)
            .CreateAsync(TestContext.Current.CancellationToken);
        (await coach.Http.DeleteAsync("/users/me/roles/Trainer", TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var resp = await coach.Http.PostAsJsonAsync(
            "/users/me/roles",
            new { Role = "Trainer" },
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<AddRoleResult>(
            cancellationToken: TestContext.Current.CancellationToken);
        body!.AddedRole.Should().Be("Trainer");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var profile = await db.ProfessionalProfiles.AsNoTracking().FirstAsync(
            p => p.UserId == coach.UserId, TestContext.Current.CancellationToken);
        profile.TrainerRoleRemovedAt.Should().BeNull();
    }

    [Fact]
    public async Task Post_RolesMe_AddingActiveRole_Returns400RoleAlreadyAssigned()
    {
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist)
            .CreateAsync(TestContext.Current.CancellationToken);

        var resp = await coach.Http.PostAsJsonAsync(
            "/users/me/roles",
            new { Role = "Trainer" },
            TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain("ROLE_ALREADY_ASSIGNED");
    }

    [Fact]
    public async Task Post_RolesMe_AfterCoachAccountEnded_RestoresRole_AndClearsDisable()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(TestContext.Current.CancellationToken);
        (await coach.Http.PostAsync("/users/me/coach-account/disable", null, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var resp = await coach.Http.PostAsJsonAsync(
            "/users/me/roles", new { Role = "Trainer" }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var profile = await db.ProfessionalProfiles.AsNoTracking().FirstAsync(
            p => p.UserId == coach.UserId, TestContext.Current.CancellationToken);
        profile.TrainerRoleRemovedAt.Should().BeNull();
        profile.CoachAccountActiveUntil.Should().BeNull();
    }

    [Fact]
    public async Task Post_RolesMe_AddingOtherRoleWhileDisablePending_ClearsPendingDisableAndCancelFlag()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(TestContext.Current.CancellationToken);
        await DisableCoachAccountIntegrationTests.SeedSubscriptionAsync(
            factory, coach.ProfileId, SubscriptionStatus.Active, DateTimeOffset.UtcNow.AddDays(10),
            paysSecondRoleAddOn: true);
        (await coach.Http.PostAsync("/users/me/coach-account/disable", null, TestContext.Current.CancellationToken))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var resp = await coach.Http.PostAsJsonAsync(
            "/users/me/roles", new { Role = "Nutritionist" }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.ProfessionalProfiles.AsNoTracking().Where(p => p.UserId == coach.UserId)
            .Select(p => p.CoachAccountActiveUntil).FirstAsync(TestContext.Current.CancellationToken)).Should().BeNull();
        (await db.CoachSubscriptions.AsNoTracking().Where(cs => cs.ProfessionalProfileId == coach.ProfileId)
            .Select(cs => cs.CancelAtPeriodEnd).FirstAsync(TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    // ── Second-role rule ─────────────────────────────────────────────────────

    [Fact]
    public async Task Post_RolesMe_PlanWithoutBothRolesAndNoAddOn_Returns403SubscriptionRequired()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(TestContext.Current.CancellationToken);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, "start");

        var resp = await coach.Http.PostAsJsonAsync(
            "/users/me/roles", new { Role = "Nutritionist" }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain(FormUp.Application.Domain.Constants.ErrorCodes.SubscriptionRequired);
    }

    [Fact]
    public async Task Post_RolesMe_PlanWithoutBothRolesButPaidAddOn_AddsTheRole()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(TestContext.Current.CancellationToken);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, "start", paysSecondRoleAddOn: true);

        var resp = await coach.Http.PostAsJsonAsync(
            "/users/me/roles", new { Role = "Nutritionist" }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Post_RolesMe_RestoringARoleWhileTheOtherIsActiveOnAPlanWithoutAddOn_Returns403SubscriptionRequired()
    {
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist)
            .WithRemovedCoachRoles(UserRole.Nutritionist)
            .CreateAsync(TestContext.Current.CancellationToken);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, "start");

        var resp = await coach.Http.PostAsJsonAsync(
            "/users/me/roles", new { Role = "Nutritionist" }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain(FormUp.Application.Domain.Constants.ErrorCodes.SubscriptionRequired);
    }

    [Fact]
    public async Task Post_RolesMe_AddOnStillPurchasable_Returns403SubscriptionRequired()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(TestContext.Current.CancellationToken);
        var (plan, _) = await BillingTestData.StripePlanAsync(factory, addOn: 9900, bothRolesIncluded: false);
        await BillingTestData.SubscribeLiveAsync(factory, coach, plan);

        var resp = await coach.Http.PostAsJsonAsync(
            "/users/me/roles", new { Role = "Nutritionist" }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain(FormUp.Application.Domain.Constants.ErrorCodes.SubscriptionRequired);
    }

    [Fact]
    public async Task Post_RolesMe_RestoringARoleWhileTheAddOnIsPurchasable_Returns403SubscriptionRequired()
    {
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist)
            .WithRemovedCoachRoles(UserRole.Nutritionist)
            .CreateAsync(TestContext.Current.CancellationToken);
        var (plan, _) = await BillingTestData.StripePlanAsync(factory, addOn: 9900, bothRolesIncluded: false);
        await BillingTestData.SubscribeLiveAsync(factory, coach, plan);

        var resp = await coach.Http.PostAsJsonAsync(
            "/users/me/roles", new { Role = "Nutritionist" }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await resp.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Contain(FormUp.Application.Domain.Constants.ErrorCodes.SubscriptionRequired);
    }

    [Fact]
    public async Task Post_RolesMe_RestoringTheOnlyActiveRoleOnAPlanWithoutAddOn_IsAllowed()
    {
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist)
            .WithRemovedCoachRoles(UserRole.Trainer, UserRole.Nutritionist)
            .CreateAsync(TestContext.Current.CancellationToken);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, "start");

        var resp = await coach.Http.PostAsJsonAsync(
            "/users/me/roles", new { Role = "Trainer" }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Post_RolesMe_CoachOnTheFreePlan_AddsTheRoleBecauseFreeIncludesBothRoles()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(TestContext.Current.CancellationToken);

        var resp = await coach.Http.PostAsJsonAsync(
            "/users/me/roles", new { Role = "Nutritionist" }, TestContext.Current.CancellationToken);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Restoring a role whose add-on removal was scheduled ──────────────────

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A two-role coach paying the add-on who has removed the Trainer role, so its end is scheduled.</summary>
    private async Task<(Actor Coach, string SubscriptionId)> ArrangeScheduledRemovalAsync()
    {
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        var (plan, _) = await BillingTestData.StripePlanAsync(factory, price: 29900, addOn: 9900, bothRolesIncluded: false);
        var subscriptionId = await BillingTestData.SubscribeLiveAsync(
            factory, coach, plan, paysSecondRoleAddOn: true, currentPeriodEndsAt: DateTimeOffset.UtcNow.AddDays(12));
        (await coach.Http.DeleteAsync("/users/me/roles/Trainer", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        return (coach, subscriptionId);
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
    public async Task Post_RolesMe_RestoringBeforeTheRenewal_DropsTheScheduledEnd_WithoutACharge()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var (coach, subscriptionId) = await ArrangeScheduledRemovalAsync();

        var resp = await coach.Http.PostAsJsonAsync("/users/me/roles", new { Role = "Trainer" }, Ct);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.ClearedScheduledChanges.Should().Equal(subscriptionId);
        fake.PlanChanges.Should().ContainSingle("only the removal scheduled a change; the restore adds none");
        fake.Charges.Should().BeEmpty();
        (await BillingTestData.ReadRowAsync(factory, coach.ProfileId)).SecondRoleAddOnEndsAt.Should().BeNull();
        (await TrainerRemovedAtAsync(coach.UserId)).Should().BeNull();
    }

    [Fact]
    public async Task Post_RolesMe_RestoringWithAScheduledDowngrade_SchedulesThePendingPlanWithItsAddOn()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        var (plan, _) = await BillingTestData.StripePlanAsync(factory, price: 29900, addOn: 9900, bothRolesIncluded: false);
        var (downgrade, downgradePriceId) = await BillingTestData.StripePlanAsync(factory, price: 19900, addOn: 4900, bothRolesIncluded: false);
        var subscriptionId = await BillingTestData.SubscribeLiveAsync(
            factory, coach, plan, paysSecondRoleAddOn: true, currentPeriodEndsAt: DateTimeOffset.UtcNow.AddDays(12));
        await BillingTestData.SetPendingPlanAsync(factory, coach.ProfileId, downgrade);
        (await coach.Http.DeleteAsync("/users/me/roles/Trainer", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var resp = await coach.Http.PostAsJsonAsync("/users/me/roles", new { Role = "Trainer" }, Ct);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.ClearedScheduledChanges.Should().BeEmpty();
        fake.PlanChanges.Should().HaveCount(2);
        fake.PlanChanges[1].Should().BeEquivalentTo(new FakeSubscriptionService.ChangePlanCall(
            subscriptionId, downgrade, downgradePriceId, AtNextRenewal: true, AddOnAmountMinorUnits: 4900));
        fake.Charges.Should().BeEmpty();
        var row = await BillingTestData.ReadRowAsync(factory, coach.ProfileId);
        row.SecondRoleAddOnEndsAt.Should().BeNull();
        row.PendingSubscriptionPlanPriceId.Should().NotBeNull();
    }

    /// <summary>Removes the Trainer role of a coach paying the add-on, with a downgrade to the given plan scheduled.</summary>
    private async Task<(Actor Coach, string DowngradeCode)> ArrangeRemovalThenDowngradeAsync(long? downgradeAddOn, bool downgradeBothRoles)
    {
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);
        var (plan, _) = await BillingTestData.StripePlanAsync(factory, price: 29900, addOn: 9900, bothRolesIncluded: false);
        var (downgrade, _) = await BillingTestData.StripePlanAsync(
            factory, price: 19900, addOn: downgradeAddOn, bothRolesIncluded: downgradeBothRoles);
        await BillingTestData.SubscribeLiveAsync(
            factory, coach, plan, paysSecondRoleAddOn: true, currentPeriodEndsAt: DateTimeOffset.UtcNow.AddDays(12));
        (await coach.Http.DeleteAsync("/users/me/roles/Trainer", Ct)).StatusCode.Should().Be(HttpStatusCode.OK);
        await BillingTestData.SetPendingPlanAsync(factory, coach.ProfileId, downgrade);

        return (coach, downgrade);
    }

    [Fact]
    public async Task Post_RolesMe_RestoringWhileTheScheduledPlanOffersNoSecondRole_Returns409_AndChangesNothing()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var (coach, _) = await ArrangeRemovalThenDowngradeAsync(downgradeAddOn: null, downgradeBothRoles: false);
        fake.PlanChanges.Clear();

        var resp = await coach.Http.PostAsJsonAsync("/users/me/roles", new { Role = "Trainer" }, Ct);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await resp.Content.ReadAsStringAsync(Ct)).Should().Contain(FormUp.Application.Domain.Constants.ErrorCodes.SecondRoleNotOnScheduledPlan);
        fake.PlanChanges.Should().BeEmpty();
        fake.ClearedScheduledChanges.Should().BeEmpty();
        (await TrainerRemovedAtAsync(coach.UserId)).Should().NotBeNull();
        (await BillingTestData.ReadRowAsync(factory, coach.ProfileId)).SecondRoleAddOnEndsAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Post_RolesMe_RestoringWhileTheScheduledPlanIncludesBothRoles_Works_AndCarriesNoAddOn()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var (coach, downgrade) = await ArrangeRemovalThenDowngradeAsync(downgradeAddOn: null, downgradeBothRoles: true);
        fake.PlanChanges.Clear();

        var resp = await coach.Http.PostAsJsonAsync("/users/me/roles", new { Role = "Trainer" }, Ct);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.PlanChanges.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new { PlanCode = downgrade, AtNextRenewal = true, AddOnAmountMinorUnits = (long?)null });
        (await TrainerRemovedAtAsync(coach.UserId)).Should().BeNull();
    }

    [Fact]
    public async Task Post_RolesMe_RestoringWhileTheScheduledPlanHasAnAddOnPrice_Works_AndCarriesTheAddOn()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var (coach, downgrade) = await ArrangeRemovalThenDowngradeAsync(downgradeAddOn: 4900, downgradeBothRoles: false);
        fake.PlanChanges.Clear();

        var resp = await coach.Http.PostAsJsonAsync("/users/me/roles", new { Role = "Trainer" }, Ct);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.PlanChanges.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new { PlanCode = downgrade, AtNextRenewal = true, AddOnAmountMinorUnits = (long?)4900 });
        (await BillingTestData.ReadRowAsync(factory, coach.ProfileId)).SecondRoleAddOnEndsAt.Should().BeNull();
    }

    [Fact]
    public async Task Post_RolesMe_RestoringWhenTheProviderFails_Returns502_AndTheRoleStaysRemoved()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var (coach, _) = await ArrangeScheduledRemovalAsync();
        fake.FailClearScheduledChange = true;

        var resp = await coach.Http.PostAsJsonAsync("/users/me/roles", new { Role = "Trainer" }, Ct);

        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await resp.Content.ReadAsStringAsync(Ct)).Should().Contain(FormUp.Application.Domain.Constants.ErrorCodes.BillingProviderError);
        (await TrainerRemovedAtAsync(coach.UserId)).Should().NotBeNull();
        (await BillingTestData.ReadRowAsync(factory, coach.ProfileId)).SecondRoleAddOnEndsAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Post_RolesMe_RestoringWhenBillingIsNotConfigured_Returns503_AndTheRoleStaysRemoved()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var (coach, _) = await ArrangeScheduledRemovalAsync();
        fake.IsConfigured = false;

        var resp = await coach.Http.PostAsJsonAsync("/users/me/roles", new { Role = "Trainer" }, Ct);

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await resp.Content.ReadAsStringAsync(Ct)).Should().Contain(FormUp.Application.Domain.Constants.ErrorCodes.BillingNotConfigured);
        (await TrainerRemovedAtAsync(coach.UserId)).Should().NotBeNull();
    }

    [Fact]
    public async Task Post_RolesMe_RestoringAfterTheScheduledEndDate_Returns403_BecauseTheAddOnMustBeBoughtAgain()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var (coach, _) = await ArrangeScheduledRemovalAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.CoachSubscriptions
                .Where(cs => cs.ProfessionalProfileId == coach.ProfileId)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(cs => cs.SecondRoleAddOnEndsAt, DateTimeOffset.UtcNow.AddHours(-1)), Ct);
        }

        var resp = await coach.Http.PostAsJsonAsync("/users/me/roles", new { Role = "Trainer" }, Ct);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await resp.Content.ReadAsStringAsync(Ct)).Should().Contain(FormUp.Application.Domain.Constants.ErrorCodes.SubscriptionRequired);
        fake.ClearedScheduledChanges.Should().BeEmpty();
    }

    // ── Local response DTO (per slice rules — no cross-feature imports) ──────

    private record AddRoleResult(string AddedRole, string AccessToken, string RefreshToken, DateTime ExpiresAt);
}
