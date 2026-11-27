using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.AdminCoaches.CancelCoachRenewal;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Endpoints.Billing;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.AdminCoaches;

/// <summary>
/// Integration tests for <c>POST /admin/coaches/{CoachId}/cancel-renewal</c> (<c>CancelCoachRenewalEndpoint</c>).
/// </summary>
[Collection(TestCollection.Shard4)]
public class CancelCoachRenewalEndpointTests(FitnessApiFactory factory) : IDisposable
{
    /// <summary>Leaves the shared fake provider in its defaults for the next test class.</summary>
    public void Dispose() => BillingTestData.ResetProvider(factory);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<HttpResponseMessage> CancelAsync(Actor admin, Guid coachId, string reason = "asked to stop") =>
        admin.Http.PostAsJsonAsync($"/admin/coaches/{coachId}/cancel-renewal", new { Reason = reason }, Ct);

    private async Task<int> AuditCountAsync(Guid coachId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.AuditLogs.AsNoTracking().CountAsync(a => a.EntityId == coachId && a.Action == "AdminCancelCoachRenewal", Ct);
    }

    private async Task<bool> CancelAtPeriodEndAsync(long profileId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.CoachSubscriptions.AsNoTracking()
            .Where(cs => cs.ProfessionalProfileId == profileId)
            .Select(cs => cs.CancelAtPeriodEnd)
            .SingleAsync(Ct);
    }

    [Fact]
    public async Task Cancel_PayingCoach_StopsTheRenewalAndWritesOneAuditRow_AndRepeatingWritesNoSecond()
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(factory);
        var periodEnd = DateTimeOffset.UtcNow.AddDays(20);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, plan, currentPeriodEndsAt: periodEnd);

        var first = await CancelAsync(admin, coach.UserId);
        var second = await CancelAsync(admin, coach.UserId, "again");

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstBody = (await first.Content.ReadFromJsonAsync<CancelCoachRenewalResponse>(Ct))!;
        firstBody.AlreadyCancelled.Should().BeFalse();
        firstBody.CurrentPeriodEndsAt.Should().BeCloseTo(periodEnd, TimeSpan.FromSeconds(1));
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        (await second.Content.ReadFromJsonAsync<CancelCoachRenewalResponse>(Ct))!.AlreadyCancelled.Should().BeTrue();
        (await CancelAtPeriodEndAsync(coach.ProfileId)).Should().BeTrue();
        (await AuditCountAsync(coach.UserId)).Should().Be(1, "the repeat is idempotent and leaves no second history entry");
    }

    private async Task<DateTimeOffset?> AdminMarkerAsync(long profileId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.CoachSubscriptions.AsNoTracking()
            .Where(cs => cs.ProfessionalProfileId == profileId)
            .Select(cs => cs.RenewalCancelledByAdminAt)
            .SingleAsync(Ct);
    }

    [Fact]
    public async Task Cancel_PayingCoach_SetsTheAdminMarker()
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(factory);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, plan);

        (await CancelAsync(admin, coach.UserId)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await AdminMarkerAsync(coach.ProfileId)).Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Cancel_WhenTheCoachAlreadyScheduledTheSwitchThemselves_StillLocksItWithTheMarker_AndWritesNoHistoryEntry()
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(factory);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, plan, cancelAtPeriodEnd: true);

        var response = await CancelAsync(admin, coach.UserId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<CancelCoachRenewalResponse>(Ct))!.AlreadyCancelled.Should().BeTrue();
        (await AdminMarkerAsync(coach.ProfileId)).Should().NotBeNull();
        (await AuditCountAsync(coach.UserId)).Should().Be(0);
    }

    [Fact]
    public async Task Cancel_LiveStripeSubscription_CancelsAtTheProviderOnce_AndClearsTheScheduledChange()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await BillingTestData.StripePlanAsync(factory);
        var subscriptionId = await BillingTestData.SubscribeLiveAsync(factory, coach, plan, paysSecondRoleAddOn: true);
        await BillingTestData.SetPendingPlanAsync(factory, coach.ProfileId, plan);
        await BillingTestData.UpdateRowAsync(factory, coach.ProfileId, row => row.SecondRoleAddOnEndsAt = DateTimeOffset.UtcNow.AddDays(3));

        var response = await CancelAsync(admin, coach.UserId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.CancelledAtPeriodEnd.Should().ContainSingle().Which.Should().Be(subscriptionId);
        var row = await BillingTestData.ReadRowAsync(factory, coach.ProfileId);
        row.CancelAtPeriodEnd.Should().BeTrue();
        row.RenewalCancelledByAdminAt.Should().NotBeNull();
        row.PendingSubscriptionPlanPriceId.Should().BeNull();
        row.SecondRoleAddOnEndsAt.Should().BeNull();
    }

    [Fact]
    public async Task Cancel_LiveStripeSubscription_WhenTheProviderFails_Returns502AndLeavesTheRowUnchanged()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await BillingTestData.StripePlanAsync(factory);
        await BillingTestData.SubscribeLiveAsync(factory, coach, plan);
        await BillingTestData.SetPendingPlanAsync(factory, coach.ProfileId, plan);
        fake.Fail = true;

        var response = await CancelAsync(admin, coach.UserId);

        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.BillingProviderError);
        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        var row = await BillingTestData.ReadRowAsync(factory, coach.ProfileId);
        row.CancelAtPeriodEnd.Should().BeFalse();
        row.RenewalCancelledByAdminAt.Should().BeNull();
        row.PendingSubscriptionPlanPriceId.Should().NotBeNull();
        (await AuditCountAsync(coach.UserId)).Should().Be(0);
        (await NotificationsAsync(coach.UserId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Cancel_LiveStripeSubscription_WhenBillingIsNotConfigured_Returns503AndLeavesTheRowUnchanged()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await BillingTestData.StripePlanAsync(factory);
        await BillingTestData.SubscribeLiveAsync(factory, coach, plan);
        fake.IsConfigured = false;

        var response = await CancelAsync(admin, coach.UserId);

        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.BillingNotConfigured);
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        fake.CancelledAtPeriodEnd.Should().BeEmpty();
        var row = await BillingTestData.ReadRowAsync(factory, coach.ProfileId);
        row.CancelAtPeriodEnd.Should().BeFalse();
        row.RenewalCancelledByAdminAt.Should().BeNull();
    }

    [Fact]
    public async Task Cancel_WithoutAProviderSubscription_SkipsTheProvider_EvenWhenNotConfigured()
    {
        var fake = BillingTestData.ResetProvider(factory);
        fake.IsConfigured = false;
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(factory);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, plan);

        var response = await CancelAsync(admin, coach.UserId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.CancelledAtPeriodEnd.Should().BeEmpty();
        (await CancelAtPeriodEndAsync(coach.ProfileId)).Should().BeTrue();
    }

    [Fact]
    public async Task Cancel_WhenTheCoachAlreadyCancelsAtTheProvider_DoesNotCallItAgain()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (plan, _) = await BillingTestData.StripePlanAsync(factory);
        await BillingTestData.SubscribeLiveAsync(factory, coach, plan, cancelAtPeriodEnd: true);

        var response = await CancelAsync(admin, coach.UserId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.CancelledAtPeriodEnd.Should().BeEmpty();
        (await AdminMarkerAsync(coach.ProfileId)).Should().NotBeNull();
    }

    [Fact]
    public async Task Cancel_NoSubscriptionRow_Returns409()
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);

        var response = await CancelAsync(admin, coach.UserId);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.NoRenewingSubscription);
        (await AuditCountAsync(coach.UserId)).Should().Be(0);
    }

    [Theory]
    [InlineData("free", SubscriptionStatus.Active, false)]
    [InlineData("paid", SubscriptionStatus.Canceled, false)]
    [InlineData("paid", SubscriptionStatus.Active, true)]
    public async Task Cancel_NothingRenews_Returns409(string plan, SubscriptionStatus status, bool gift)
    {
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var planCode = plan == "free" ? "free" : await PaidPlanBuilder.CreateAsync(factory);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(
            factory, coach, planCode, status, givenFreeAt: gift ? DateTimeOffset.UtcNow : null);

        var response = await CancelAsync(admin, coach.UserId);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict, "a Free plan, an ended subscription and a gift renew nothing");
        (await CancelAtPeriodEndAsync(coach.ProfileId)).Should().BeFalse();
    }

    [Fact]
    public async Task Cancel_UnknownCoach_Returns404()
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);

        var response = await CancelAsync(admin, Guid.NewGuid());

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private FakeRealtimeNotifier Notifier => factory.Services.GetRequiredService<FakeRealtimeNotifier>();

    private async Task<List<Notification>> NotificationsAsync(Guid coachId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.Notifications.AsNoTracking()
            .Where(n => n.RecipientUserId == coachId && n.Type == NotificationType.CoachPlanRenewalCancelled)
            .ToListAsync(Ct);
    }

    [Fact]
    public async Task Cancel_PayingCoach_NotifiesTheCoachOnce_AndARepeatSendsNothing()
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(factory);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(
            factory, coach, plan, currentPeriodEndsAt: DateTimeOffset.UtcNow.AddDays(20));

        (await CancelAsync(admin, coach.UserId)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await CancelAsync(admin, coach.UserId, "again")).StatusCode.Should().Be(HttpStatusCode.OK);

        var rows = await NotificationsAsync(coach.UserId);
        rows.Should().ContainSingle().Which.Body.Should().StartWith("It runs until ");
        var calls = Notifier.Calls.Where(c => c.UserId == coach.UserId).ToList();
        calls.Select(c => c.EventType).Should().Equal("newnotification", "subscriptionchanged");
        var json = JsonSerializer.Serialize(calls[1].Payload);
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("action").GetString().Should().Be("renewalcancelled");
        doc.RootElement.GetProperty("until").ValueKind.Should().Be(JsonValueKind.String);
        doc.RootElement.GetProperty("planNameEn").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Cancel_WithoutAPeriodEnd_UsesTheNoDateWording()
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(factory);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, plan);

        (await CancelAsync(admin, coach.UserId)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await NotificationsAsync(coach.UserId)).Should().ContainSingle().Which.Body.Should().BeEmpty();
        var update = Notifier.Calls.Single(c => c.UserId == coach.UserId && c.EventType == "subscriptionchanged");
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(update.Payload));
        doc.RootElement.GetProperty("until").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Cancel_WhenTheHubThrows_StillReturns200_AndKeepsTheCancellation()
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(factory);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(
            factory, coach, plan, currentPeriodEndsAt: DateTimeOffset.UtcNow.AddDays(20));
        Notifier.SimulateThrowOnNextCall();

        var response = await CancelAsync(admin, coach.UserId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await CancelAtPeriodEndAsync(coach.ProfileId)).Should().BeTrue();
        Notifier.Calls.Where(c => c.UserId == coach.UserId).Should().BeEmpty();
    }

    [Fact]
    public async Task Cancel_Rejected_SendsNothing()
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);

        (await CancelAsync(admin, coach.UserId)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        var unknown = Guid.NewGuid();
        (await CancelAsync(admin, unknown)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await NotificationsAsync(coach.UserId)).Should().BeEmpty();
        Notifier.Calls.Where(c => c.UserId == coach.UserId || c.UserId == unknown).Should().BeEmpty();
    }

    [Fact]
    public async Task Cancel_WithoutAReason_Returns400()
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);

        var response = await CancelAsync(admin, coach.UserId, string.Empty);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
