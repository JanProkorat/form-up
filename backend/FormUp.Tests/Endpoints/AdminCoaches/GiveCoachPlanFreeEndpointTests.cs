using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.AdminCoaches.GiveCoachPlanFree;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.AdminCoaches;

/// <summary>
/// Integration tests for <c>POST /admin/coaches/{CoachId}/give-free</c> (<c>GiveCoachPlanFreeEndpoint</c>).
/// </summary>
[Collection(TestCollection.Name)]
public class GiveCoachPlanFreeEndpointTests(FitnessApiFactory factory)
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
