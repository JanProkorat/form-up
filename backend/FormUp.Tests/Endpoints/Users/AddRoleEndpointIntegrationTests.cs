using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
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
public class AddRoleEndpointIntegrationTests(FitnessApiFactory factory)
{
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

    // ── Local response DTO (per slice rules — no cross-feature imports) ──────

    private record AddRoleResult(string AddedRole, string AccessToken, string RefreshToken, DateTime ExpiresAt);
}
