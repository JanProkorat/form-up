using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.AdminCoaches.DisableCoachAccountByAdmin;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Endpoints.Billing;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.AdminCoaches;

/// <summary>
/// Integration tests for <c>POST /admin/coaches/{CoachId}/disable</c> (<c>DisableCoachAccountByAdminEndpoint</c>), which
/// runs the same logic as the coach's own Disable button.
/// </summary>
[Collection(TestCollection.Name)]
public class DisableCoachAccountByAdminEndpointTests(FitnessApiFactory factory) : IDisposable
{
    /// <summary>Leaves the shared fake provider in its defaults for the next test class.</summary>
    public void Dispose() => BillingTestData.ResetProvider(factory);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<HttpResponseMessage> DisableAsync(Actor admin, Guid coachId, string reason = "abuse report") =>
        admin.Http.PostAsJsonAsync($"/admin/coaches/{coachId}/disable", new { Reason = reason }, Ct);

    private async Task<List<AuditLog>> AuditRowsAsync(Guid coachId, string action)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.AuditLogs.AsNoTracking().Where(a => a.EntityId == coachId && a.Action == action).ToListAsync(Ct);
    }

    private async Task<ProfessionalProfile> ReadProfileAsync(Guid coachId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.ProfessionalProfiles.AsNoTracking().SingleAsync(p => p.UserId == coachId, Ct);
    }

    [Fact]
    public async Task Disable_CoachWithNoSubscription_RemovesTheRolesAtOnceAndAuditsTheAdminAndReason()
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).CreateAsync(Ct);

        var response = await DisableAsync(admin, coach.UserId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<DisableCoachAccountByAdminResponse>(Ct))!;
        body.CoachId.Should().Be(coach.UserId);
        body.RolesRemoved.Should().BeEquivalentTo([AppRoles.Trainer, AppRoles.Nutritionist]);
        var profile = await ReadProfileAsync(coach.UserId);
        profile.TrainerRoleRemovedAt.Should().NotBeNull();
        profile.NutritionistRoleRemovedAt.Should().NotBeNull();

        var audit = (await AuditRowsAsync(coach.UserId, "AdminDisableCoachAccount")).Should().ContainSingle().Subject;
        audit.UserId.Should().Be(admin.UserId);
        using var values = JsonDocument.Parse(audit.NewValues!);
        values.RootElement.GetProperty("reason").GetString().Should().Be("abuse report");
        (await AuditRowsAsync(coach.UserId, "DisableCoachAccount")).Should().BeEmpty("the coach did not do this themselves");
    }

    [Fact]
    public async Task Disable_CoachWithAPaidPeriod_KeepsTheRolesUntilItsEnd_AndRepeatingWritesNoSecondRow()
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(factory);
        var periodEnd = DateTimeOffset.UtcNow.AddDays(14);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, plan, currentPeriodEndsAt: periodEnd);

        var first = await (await DisableAsync(admin, coach.UserId)).Content.ReadFromJsonAsync<DisableCoachAccountByAdminResponse>(Ct);
        var secondResponse = await DisableAsync(admin, coach.UserId, "again");
        var second = await secondResponse.Content.ReadFromJsonAsync<DisableCoachAccountByAdminResponse>(Ct);

        first!.ActiveUntil.Should().BeCloseTo(periodEnd.UtcDateTime, TimeSpan.FromSeconds(1));
        first.RolesRemoved.Should().BeEmpty();
        secondResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        second!.ActiveUntil.Should().Be(first.ActiveUntil);
        (await ReadProfileAsync(coach.UserId)).TrainerRoleRemovedAt.Should().BeNull();
        (await AuditRowsAsync(coach.UserId, "AdminDisableCoachAccount")).Should().ContainSingle();
    }

    [Fact]
    public async Task Disable_EveryCoachRoleAlreadyRemoved_Returns400NoActiveCoachRole()
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).WithRemovedCoachRoles(UserRole.Trainer).CreateAsync(Ct);

        var response = await DisableAsync(admin, coach.UserId);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.NoActiveCoachRole);
        (await AuditRowsAsync(coach.UserId, "AdminDisableCoachAccount")).Should().BeEmpty();
    }

    [Fact]
    public async Task Disable_ByTheCoachThemselves_StillWritesTheOriginalAuditRow()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);

        (await coach.Http.PostAsync("/users/me/coach-account/disable", null, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var audit = (await AuditRowsAsync(coach.UserId, "DisableCoachAccount")).Should().ContainSingle().Subject;
        audit.UserId.Should().Be(coach.UserId);
        using var values = JsonDocument.Parse(audit.NewValues!);
        values.RootElement.EnumerateObject().Select(property => property.Name).Should().Equal("activeUntil");
    }

    [Fact]
    public async Task Disable_LiveStripeSubscription_CancelsAtTheProviderOnce_AndAuditsTheAdmin()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await BillingTestData.StripePlanAsync(factory);
        var subscriptionId = await BillingTestData.SubscribeLiveAsync(factory, coach, plan);

        var response = await DisableAsync(admin, coach.UserId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.CancelledAtPeriodEnd.Should().ContainSingle().Which.Should().Be(subscriptionId);
        (await BillingTestData.ReadRowAsync(factory, coach.ProfileId)).CancelAtPeriodEnd.Should().BeTrue();
        (await AuditRowsAsync(coach.UserId, "AdminDisableCoachAccount")).Should().ContainSingle();
    }

    [Fact]
    public async Task Disable_LiveStripeSubscription_WhenTheProviderFails_Returns502AndSavesNothing()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await BillingTestData.StripePlanAsync(factory);
        await BillingTestData.SubscribeLiveAsync(factory, coach, plan);
        fake.Fail = true;

        var response = await DisableAsync(admin, coach.UserId);

        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.BillingProviderError);
        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await ReadProfileAsync(coach.UserId)).CoachAccountActiveUntil.Should().BeNull();
        (await BillingTestData.ReadRowAsync(factory, coach.ProfileId)).CancelAtPeriodEnd.Should().BeFalse();
        (await AuditRowsAsync(coach.UserId, "AdminDisableCoachAccount")).Should().BeEmpty();
    }

    [Fact]
    public async Task Disable_LiveStripeSubscription_WhenBillingIsNotConfigured_Returns503AndSavesNothing()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await BillingTestData.StripePlanAsync(factory);
        await BillingTestData.SubscribeLiveAsync(factory, coach, plan);
        fake.IsConfigured = false;

        var response = await DisableAsync(admin, coach.UserId);

        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.BillingNotConfigured);
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        fake.CancelledAtPeriodEnd.Should().BeEmpty();
        (await ReadProfileAsync(coach.UserId)).CoachAccountActiveUntil.Should().BeNull();
        (await AuditRowsAsync(coach.UserId, "AdminDisableCoachAccount")).Should().BeEmpty();
    }

    [Fact]
    public async Task Disable_UnknownCoach_Returns404()
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);

        var response = await DisableAsync(admin, Guid.NewGuid());

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Disable_WithoutAReason_Returns400()
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);

        var response = await DisableAsync(admin, coach.UserId, string.Empty);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadProfileAsync(coach.UserId)).CoachAccountActiveUntil.Should().BeNull();
    }
}
