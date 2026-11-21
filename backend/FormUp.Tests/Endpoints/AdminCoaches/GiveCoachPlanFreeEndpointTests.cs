using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Features.AdminCoaches.GiveCoachPlanFree;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Services.Billing;
using FormUp.Tests.Builders;
using FormUp.Tests.Endpoints.Billing;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.AdminCoaches;

/// <summary>
/// Integration tests for <c>POST /admin/coaches/{CoachId}/give-free</c> (<c>GiveCoachPlanFreeEndpoint</c>).
/// </summary>
[Collection(TestCollection.Shard2)]
public class GiveCoachPlanFreeEndpointTests(FitnessApiFactory factory) : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Body(string plan, DateTimeOffset? freeUntil, string reason = "beta tester") =>
        new { PlanCode = plan, FreeUntil = freeUntil, Reason = reason };

    private async Task<CoachSubscription> ReadSubscriptionAsync(long profileId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.CoachSubscriptions.AsNoTracking()
            .Include(cs => cs.SubscriptionPlan)
            .Include(cs => cs.SubscriptionPlanPrice)
            .SingleAsync(cs => cs.ProfessionalProfileId == profileId, Ct);
    }

    private async Task<List<AuditLog>> AuditRowsAsync(Guid coachId, string action)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.AuditLogs.AsNoTracking().Where(a => a.EntityId == coachId && a.Action == action).ToListAsync(Ct);
    }

    [Fact]
    public async Task Give_WithAnEnd_PutsTheCoachOnTheOpenPriceRowAsAGiftAndAuditsIt()
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(factory);
        var freeUntil = DateTimeOffset.UtcNow.AddDays(45);

        var response = await admin.Http.PostAsJsonAsync($"/admin/coaches/{coach.UserId}/give-free", Body(plan, freeUntil), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var row = await ReadSubscriptionAsync(coach.ProfileId);
        row.SubscriptionPlan.Code.Should().Be(plan);
        row.SubscriptionPlanPrice.ValidTo.Should().BeNull();
        row.Status.Should().Be(SubscriptionStatus.Active);
        row.GivenFreeAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        row.FreeUntil.Should().BeCloseTo(freeUntil, TimeSpan.FromSeconds(1));
        row.PendingSubscriptionPlanPriceId.Should().BeNull();

        var audit = (await AuditRowsAsync(coach.UserId, "AdminGiveCoachPlanFree")).Should().ContainSingle().Subject;
        audit.UserId.Should().Be(admin.UserId);
        audit.EntityType.Should().Be(nameof(ApplicationUser));
        using var values = JsonDocument.Parse(audit.NewValues!);
        values.RootElement.GetProperty("planCode").GetString().Should().Be(plan);
        values.RootElement.GetProperty("reason").GetString().Should().Be("beta tester");
    }

    [Fact]
    public async Task Give_WithoutAnEnd_IsAGiftWithNoEnd_AndReplacesAnEarlierPaidRowAndItsPendingPrice()
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var paid = await PaidPlanBuilder.CreateAsync(factory);
        var gift = await PaidPlanBuilder.CreateAsync(factory, priceMinorUnits: 50000);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, paid, SubscriptionStatus.PastDue);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var subscription = await db.CoachSubscriptions.SingleAsync(cs => cs.ProfessionalProfileId == coach.ProfileId, Ct);
            subscription.PendingSubscriptionPlanPriceId = subscription.SubscriptionPlanPriceId;
            await db.SaveChangesAsync(Ct);
        }

        var response = await admin.Http.PostAsJsonAsync($"/admin/coaches/{coach.UserId}/give-free", Body(gift, null), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var row = await ReadSubscriptionAsync(coach.ProfileId);
        row.SubscriptionPlan.Code.Should().Be(gift);
        row.Status.Should().Be(SubscriptionStatus.Active, "a past-due status does not carry over to the gift");
        row.GivenFreeAt.Should().NotBeNull();
        row.FreeUntil.Should().BeNull();
        row.PendingSubscriptionPlanPriceId.Should().BeNull();
    }

    [Fact]
    public async Task Give_ToACoachWhoLapsed_ResetsTheKeptAndReleasedClientsToConnected()
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(factory);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, plan, SubscriptionStatus.Canceled);
        var kept = await TestActors.Client(factory).CreateAsync(Ct);
        var released = await TestActors.Client(factory).CreateAsync(Ct);
        var keptLink = await TestActors.Link(factory, coach, kept).WithState(ConnectionState.FreePlanKept).CreateAsync(Ct);
        var releasedLink = await TestActors.Link(factory, coach, released).WithState(ConnectionState.FreePlanReleased).CreateAsync(Ct);

        var response = await admin.Http.PostAsJsonAsync($"/admin/coaches/{coach.UserId}/give-free", Body(plan, null), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var states = await db.ClientProfessionalLinks.AsNoTracking()
            .Where(link => link.Id == keptLink || link.Id == releasedLink)
            .Select(link => link.ConnectionState)
            .ToListAsync(Ct);
        states.Should().OnlyContain(state => state == ConnectionState.Connected);
    }

    private async Task<string> SubscribeWithStripeAsync(
        Actor coach, string paidPlan, SubscriptionStatus status = SubscriptionStatus.Active, bool paysAddOn = false)
    {
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(
            factory, coach, paidPlan, status, paysSecondRoleAddOn: paysAddOn, currentPeriodEndsAt: DateTimeOffset.UtcNow.AddDays(10));
        var subscriptionId = $"sub_{Guid.NewGuid():N}";
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.CoachSubscriptions.Where(cs => cs.ProfessionalProfileId == coach.ProfileId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(cs => cs.ExternalSubscriptionId, subscriptionId)
                .SetProperty(cs => cs.SecondRoleAddOnEndsAt, paysAddOn ? DateTimeOffset.UtcNow.AddDays(10) : null), Ct);

        return subscriptionId;
    }

    [Theory]
    [InlineData(SubscriptionStatus.Active)]
    [InlineData(SubscriptionStatus.PastDue)]
    [InlineData(SubscriptionStatus.Incomplete)]
    public async Task Give_ToACoachWithAStripeSubscription_CancelsItAndRefundsThenSavesTheGift(SubscriptionStatus status)
    {
        var fake = BillingTestData.ResetProvider(factory);
        fake.CancelNowResult = new CancelRefundResult(12300, "czk");
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(factory);
        var subscriptionId = await SubscribeWithStripeAsync(coach, plan, status);

        var response = await admin.Http.PostAsJsonAsync($"/admin/coaches/{coach.UserId}/give-free", Body(plan, null), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.CancelledNow.Should().ContainSingle().Which.Should().Be(subscriptionId);
        var row = await ReadSubscriptionAsync(coach.ProfileId);
        row.GivenFreeAt.Should().NotBeNull();
        row.Status.Should().Be(SubscriptionStatus.Active);
        row.ExternalSubscriptionId.Should().BeNull("the subscription is gone, so a later End plan cannot touch Stripe");
        row.CurrentPeriodEndsAt.Should().BeNull();

        var audit = (await AuditRowsAsync(coach.UserId, "AdminGiveCoachPlanFree")).Should().ContainSingle().Subject;
        using var values = JsonDocument.Parse(audit.NewValues!);
        values.RootElement.GetProperty("providerSubscriptionCancelled").GetBoolean().Should().BeTrue();
        values.RootElement.GetProperty("refundedMinorUnits").GetInt64().Should().Be(12300);
    }

    [Theory]
    [InlineData("czk", "CZK")]
    [InlineData("eur", "EUR")]
    public async Task Give_WhenStripeRefunds_ReturnsTheAmountAndUpperCasedCurrency(string stripeCurrency, string expected)
    {
        var fake = BillingTestData.ResetProvider(factory);
        fake.CancelNowResult = new CancelRefundResult(65673, stripeCurrency);
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(factory);
        await SubscribeWithStripeAsync(coach, plan);

        var response = await admin.Http.PostAsJsonAsync($"/admin/coaches/{coach.UserId}/give-free", Body(plan, null), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<GiveCoachPlanFreeResponse>(Ct))!;
        body.RefundedMinorUnits.Should().Be(65673);
        body.RefundCurrency.Should().Be(expected);
    }

    [Fact]
    public async Task Give_WhenNothingIsRefunded_ReturnsNullRefundFields()
    {
        var fake = BillingTestData.ResetProvider(factory);
        fake.CancelNowResult = CancelRefundResult.None;
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(factory);
        await SubscribeWithStripeAsync(coach, plan);

        var response = await admin.Http.PostAsJsonAsync($"/admin/coaches/{coach.UserId}/give-free", Body(plan, null), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<GiveCoachPlanFreeResponse>(Ct))!;
        body.RefundedMinorUnits.Should().BeNull();
        body.RefundCurrency.Should().BeNull();
    }

    [Fact]
    public async Task Give_WhenTheDeletedWebhookLandsDuringTheStripeCancel_StillEndsActiveOnTheGivenPlan()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(factory);
        var gift = await PaidPlanBuilder.CreateAsync(factory, priceMinorUnits: 50000);
        await SubscribeWithStripeAsync(coach, plan, paysAddOn: true);
        var webhookAt = DateTimeOffset.UtcNow.AddSeconds(5);
        webhookAt = new DateTimeOffset(webhookAt.Ticks - (webhookAt.Ticks % TimeSpan.TicksPerSecond), webhookAt.Offset);

        // The deleted webhook for a row with no gift in force commits while the endpoint waits for Stripe.
        fake.CancelNowHook = _ => BillingTestData.UpdateRowAsync(factory, coach.ProfileId, row =>
        {
            row.Status = SubscriptionStatus.Canceled;
            row.ExternalSubscriptionId = null;
            row.NextPaymentAttemptAt = null;
            row.CancelAtPeriodEnd = false;
            row.RenewalCancelledByAdminAt = null;
            row.PendingSubscriptionPlanPriceId = null;
            row.SecondRoleAddOnEndsAt = null;
            row.LastExternalEventAt = webhookAt;
        });

        var response = await admin.Http.PostAsJsonAsync($"/admin/coaches/{coach.UserId}/give-free", Body(gift, null), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var row = await ReadSubscriptionAsync(coach.ProfileId);
        row.Status.Should().Be(SubscriptionStatus.Active, "the given plan must win over the webhook's Canceled");
        row.SubscriptionPlan.Code.Should().Be(gift);
        row.GivenFreeAt.Should().NotBeNull();
        row.ExternalSubscriptionId.Should().BeNull();
        row.PaysSecondRoleAddOn.Should().BeFalse();
        row.LastExternalEventAt.Should().BeOnOrAfter(webhookAt, "the event time never moves backwards");
    }

    [Fact]
    public async Task Give_WhenStripeFails_Returns502_AndSavesNothing()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(factory);
        var gift = await PaidPlanBuilder.CreateAsync(factory, priceMinorUnits: 50000);
        var subscriptionId = await SubscribeWithStripeAsync(coach, plan);
        fake.Fail = true;

        var response = await admin.Http.PostAsJsonAsync($"/admin/coaches/{coach.UserId}/give-free", Body(gift, null), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        var row = await ReadSubscriptionAsync(coach.ProfileId);
        row.SubscriptionPlan.Code.Should().Be(plan);
        row.GivenFreeAt.Should().BeNull();
        row.ExternalSubscriptionId.Should().Be(subscriptionId, "a retry must still find the subscription to refund");
        (await AuditRowsAsync(coach.UserId, "AdminGiveCoachPlanFree")).Should().BeEmpty();
    }

    [Fact]
    public async Task Give_WhenStripeIsNotConfiguredAndTheCoachHasASubscription_Returns503_AndSavesNothing()
    {
        var fake = BillingTestData.ResetProvider(factory);
        fake.IsConfigured = false;
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(factory);
        await SubscribeWithStripeAsync(coach, plan);

        var response = await admin.Http.PostAsJsonAsync($"/admin/coaches/{coach.UserId}/give-free", Body(plan, null), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("BILLING_NOT_CONFIGURED");
        fake.CancelledNow.Should().BeEmpty();
        (await ReadSubscriptionAsync(coach.ProfileId)).GivenFreeAt.Should().BeNull();
    }

    [Fact]
    public async Task Give_ToACoachWithoutAStripeSubscription_MakesNoStripeCall_EvenWithoutStripeKeys()
    {
        var fake = BillingTestData.ResetProvider(factory);
        fake.IsConfigured = false;
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(factory);

        var response = await admin.Http.PostAsJsonAsync($"/admin/coaches/{coach.UserId}/give-free", Body(plan, null), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.CancelledNow.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Give_ToAnAddOnPayer_EndsTheAddOnWithTheSubscription_AndRemovesNoRole(bool bothRolesIncluded)
    {
        var fake = BillingTestData.ResetProvider(factory);
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var paid = await PaidPlanBuilder.CreateAsync(factory, addOnMinorUnits: 9900, bothRolesIncluded: false);
        var gift = await PaidPlanBuilder.CreateAsync(factory, bothRolesIncluded: bothRolesIncluded);
        await SubscribeWithStripeAsync(coach, paid, paysAddOn: true);

        var response = await admin.Http.PostAsJsonAsync($"/admin/coaches/{coach.UserId}/give-free", Body(gift, null), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.CancelledNow.Should().ContainSingle();
        var row = await ReadSubscriptionAsync(coach.ProfileId);
        row.PaysSecondRoleAddOn.Should().BeFalse();
        row.SecondRoleAddOnEndsAt.Should().BeNull();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var profile = await db.ProfessionalProfiles.AsNoTracking().SingleAsync(p => p.Id == coach.ProfileId, Ct);
        profile.TrainerRoleRemovedAt.Should().BeNull();
        profile.NutritionistRoleRemovedAt.Should().BeNull();

        var audit = (await AuditRowsAsync(coach.UserId, "AdminGiveCoachPlanFree")).Should().ContainSingle().Subject;
        audit.NewValues.Should().Contain("\"secondRoleAddOnEnded\":true");
    }

    public void Dispose() => BillingTestData.ResetProvider(factory);

    [Fact]
    public async Task Give_ReasonWithQuotesAndInjectedJson_IsStoredAsDataAndTheAuditRowStaysWithinItsColumn()
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(factory);
        var forged = "x\",\"planCode\":\"forged";
        var longReason = new string('"', GiveCoachPlanFreeValidator.MaxReasonLength);

        (await admin.Http.PostAsJsonAsync($"/admin/coaches/{coach.UserId}/give-free", Body(plan, null, forged), Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.Http.PostAsJsonAsync($"/admin/coaches/{coach.UserId}/give-free", Body(plan, null, longReason), Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var rows = await AuditRowsAsync(coach.UserId, "AdminGiveCoachPlanFree");
        rows.Should().HaveCount(2);

        foreach (var row in rows)
        {
            row.NewValues!.Length.Should().BeLessThanOrEqualTo(500);
            using var values = JsonDocument.Parse(row.NewValues);
            values.RootElement.GetProperty("planCode").GetString().Should().Be(plan, "the reason cannot overwrite a field");
        }

        using var first = JsonDocument.Parse(rows.Single(row => row.NewValues!.Contains("forged")).NewValues!);
        first.RootElement.GetProperty("reason").GetString().Should().Be(forged);
    }

    private FakeRealtimeNotifier Notifier => factory.Services.GetRequiredService<FakeRealtimeNotifier>();

    private async Task<List<Notification>> NotificationsAsync(Guid coachId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.Notifications.AsNoTracking()
            .Where(n => n.RecipientUserId == coachId && n.Type == NotificationType.CoachPlanChanged)
            .ToListAsync(Ct);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Give_NotifiesOnlyThatCoach_WithTheChangedEvent(bool dated)
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(factory);

        var response = await admin.Http.PostAsJsonAsync(
            $"/admin/coaches/{coach.UserId}/give-free", Body(plan, dated ? DateTimeOffset.UtcNow.AddDays(45) : null), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var row = (await NotificationsAsync(coach.UserId)).Should().ContainSingle().Subject;

        if (dated)
        {
            row.Body.Should().StartWith("Until ");
        }
        else
        {
            row.Body.Should().BeEmpty();
        }

        var calls = Notifier.Calls.Where(c => c.UserId == coach.UserId).ToList();
        calls.Select(c => c.EventType).Should().Equal("newnotification", "subscriptionchanged");
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(calls[1].Payload));
        doc.RootElement.GetProperty("action").GetString().Should().Be("changed");
        doc.RootElement.GetProperty("until").ValueKind.Should().Be(dated ? JsonValueKind.String : JsonValueKind.Null);
        doc.RootElement.GetProperty("planNameCs").GetString().Should().NotBeNullOrEmpty();
        doc.RootElement.GetProperty("planNameDe").GetString().Should().NotBeNullOrEmpty();
        Notifier.Calls.Where(c => c.UserId == admin.UserId).Should().BeEmpty();
    }

    [Fact]
    public async Task Give_WhenTheHubThrows_StillReturns200_AndTheChangePersists()
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(factory);
        Notifier.SimulateThrowOnNextCall();

        var response = await admin.Http.PostAsJsonAsync($"/admin/coaches/{coach.UserId}/give-free", Body(plan, null), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadSubscriptionAsync(coach.ProfileId)).SubscriptionPlan.Code.Should().Be(plan);
    }

    [Fact]
    public async Task Give_Rejected_SendsNothing()
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var other = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(factory);
        var unknown = Guid.NewGuid();

        (await admin.Http.PostAsJsonAsync($"/admin/coaches/{unknown}/give-free", Body(plan, null), Ct))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await other.Http.PostAsJsonAsync($"/admin/coaches/{coach.UserId}/give-free", Body(plan, null), Ct))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await NotificationsAsync(coach.UserId)).Should().BeEmpty();
        Notifier.Calls.Where(c => c.UserId == coach.UserId || c.UserId == unknown).Should().BeEmpty();
    }

    [Fact]
    public async Task Give_UnknownCoach_Returns404()
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(factory);

        var response = await admin.Http.PostAsJsonAsync($"/admin/coaches/{Guid.NewGuid()}/give-free", Body(plan, null), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("PROFESSIONAL_NOT_FOUND");
    }

    [Fact]
    public async Task Give_UnknownPlan_Returns404()
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);

        var response = await admin.Http.PostAsJsonAsync($"/admin/coaches/{coach.UserId}/give-free", Body("no-such-plan", null), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain("SUBSCRIPTION_PLAN_NOT_FOUND");
    }

    [Fact]
    public async Task Give_WithoutAReason_Returns400()
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(factory);

        var response = await admin.Http.PostAsJsonAsync($"/admin/coaches/{coach.UserId}/give-free", Body(plan, null, string.Empty), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
