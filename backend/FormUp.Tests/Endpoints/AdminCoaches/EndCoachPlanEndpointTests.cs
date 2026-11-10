using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.AdminCoaches.EndCoachPlan;
using FormUp.Application.Features.AdminCoaches.GetAdminCoach;
using FormUp.Application.Features.CoachSubscriptions.GetKeepClients;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.AdminCoaches;

/// <summary>
/// Integration tests for <c>POST /admin/coaches/{CoachId}/end-plan</c> (<c>EndCoachPlanEndpoint</c>).
/// </summary>
[Collection(TestCollection.Name)]
public class EndCoachPlanEndpointTests(FitnessApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<HttpResponseMessage> EndAsync(Actor caller, Guid coachId, string reason = "asked to stop") =>
        caller.Http.PostAsJsonAsync($"/admin/coaches/{coachId}/end-plan", new { Reason = reason }, Ct);

    private static async Task<GetAdminCoachResponse> GetCoachAsync(Actor admin, Guid coachId)
    {
        var response = await admin.Http.GetAsync($"/admin/coaches/{coachId}", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return (await response.Content.ReadFromJsonAsync<GetAdminCoachResponse>(JsonOptions, Ct))!;
    }

    private async Task<CoachSubscription> ReadSubscriptionAsync(long profileId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.CoachSubscriptions.AsNoTracking()
            .Include(cs => cs.SubscriptionPlan)
            .SingleAsync(cs => cs.ProfessionalProfileId == profileId, Ct);
    }

    private async Task<List<AuditLog>> AuditRowsAsync(Guid coachId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.AuditLogs.AsNoTracking()
            .Where(a => a.EntityId == coachId && a.Action == "AdminEndCoachPlan")
            .ToListAsync(Ct);
    }

    [Fact]
    public async Task End_PayingCoach_ResolvesToFree_KeepsThePlanCode_AndClearsTheScheduledState()
    {
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(factory);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(
            factory, coach, plan, cancelAtPeriodEnd: true, currentPeriodEndsAt: DateTimeOffset.UtcNow.AddDays(10));

        var response = await EndAsync(admin, coach.UserId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await response.Content.ReadFromJsonAsync<EndCoachPlanResponse>(JsonOptions, Ct))!;
        body.PlanCode.Should().Be(plan);
        body.WasGivenFree.Should().BeFalse();

        var row = await ReadSubscriptionAsync(coach.ProfileId);
        row.SubscriptionPlan.Code.Should().Be(plan, "the paid plan code stays so the coach reads as lapsed");
        row.Status.Should().Be(SubscriptionStatus.Canceled);
        row.CancelAtPeriodEnd.Should().BeFalse();
        row.PendingSubscriptionPlanPriceId.Should().BeNull();
        row.GivenFreeAt.Should().BeNull();
        row.FreeUntil.Should().BeNull();

        var detail = await GetCoachAsync(admin, coach.UserId);
        detail.Status.Should().Be(AdminCoachStatus.Free);
        detail.Plan.Code.Should().Be("free");
        detail.CanEndPlanNow.Should().BeFalse();
        detail.CancelAtPeriodEnd.Should().BeFalse();
    }

    [Fact]
    public async Task End_PayingCoachWithAPendingPriceChange_ClearsIt()
    {
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(factory);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, plan, SubscriptionStatus.PastDue);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var subscription = await db.CoachSubscriptions.SingleAsync(cs => cs.ProfessionalProfileId == coach.ProfileId, Ct);
            subscription.PendingSubscriptionPlanPriceId = subscription.SubscriptionPlanPriceId;
            await db.SaveChangesAsync(Ct);
        }

        (await EndAsync(admin, coach.UserId)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await ReadSubscriptionAsync(coach.ProfileId)).PendingSubscriptionPlanPriceId.Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task End_GiftWithNoEndOrWithADate_ResolvesToFree(bool dated)
    {
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(factory);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(
            factory,
            coach,
            plan,
            givenFreeAt: DateTimeOffset.UtcNow.AddDays(-3),
            freeUntil: dated ? DateTimeOffset.UtcNow.AddDays(20) : null);

        (await GetCoachAsync(admin, coach.UserId)).CanEndPlanNow.Should().BeTrue();

        var response = await EndAsync(admin, coach.UserId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<EndCoachPlanResponse>(JsonOptions, Ct))!.WasGivenFree.Should().BeTrue();

        var row = await ReadSubscriptionAsync(coach.ProfileId);
        row.GivenFreeAt.Should().BeNull();
        row.FreeUntil.Should().BeNull();
        row.Status.Should().Be(SubscriptionStatus.Canceled);

        var detail = await GetCoachAsync(admin, coach.UserId);
        detail.Status.Should().Be(AdminCoachStatus.Free);
        detail.Plan.Code.Should().Be("free");
    }

    [Fact]
    public async Task End_CoachOverTheFreeCap_ShowsLapsedWithClientsOverTheLimit_AndResetsAnEarlierChoice()
    {
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(factory);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, plan);

        var linkIds = new List<long>();

        foreach (var daysAgo in new[] { 30, 20, 10 })
        {
            var client = await TestActors.Client(factory).CreateAsync(Ct);
            linkIds.Add(await TestActors.Link(factory, coach, client)
                .WithActivatedAtUtc(DateTime.UtcNow.AddDays(-daysAgo))
                .CreateAsync(Ct));
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var stale = await db.ClientProfessionalLinks.SingleAsync(link => link.Id == linkIds[0], Ct);
            stale.ConnectionState = ConnectionState.FreePlanKept;
            await db.SaveChangesAsync(Ct);
        }

        (await EndAsync(admin, coach.UserId)).StatusCode.Should().Be(HttpStatusCode.OK);

        var keepClients = await coach.Http.GetAsync("/users/me/subscription/keep-clients", Ct);

        keepClients.StatusCode.Should().Be(HttpStatusCode.OK, "the coach reads as lapsed");
        var body = (await keepClients.Content.ReadFromJsonAsync<GetKeepClientsResponse>(JsonOptions, Ct))!;
        body.Cap.Should().Be(2);
        body.Clients.Should().HaveCount(3);
        body.Clients.Count(client => !client.IsKept).Should().Be(1, "one client is over the Free limit");

        using var verify = factory.Services.CreateScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await verifyDb.ClientProfessionalLinks.AsNoTracking().Where(link => link.Id == linkIds[0]).Select(link => link.ConnectionState).SingleAsync(Ct))
            .Should().Be(ConnectionState.Connected, "a stale kept/released choice must not carry over");
    }

    [Fact]
    public async Task End_Gift_DoesNotLeaveTheCoachWaitingForAPlanChoice()
    {
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(factory);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(
            factory, coach, plan, givenFreeAt: DateTimeOffset.UtcNow.AddDays(-3), freeUntil: DateTimeOffset.UtcNow.AddDays(20));

        (await EndAsync(admin, coach.UserId)).StatusCode.Should().Be(HttpStatusCode.OK);

        var write = await coach.Http.PostAsJsonAsync("/training/plans", new { }, Ct);

        (await write.Content.ReadAsStringAsync(Ct)).Should().NotContain(ErrorCodes.PlanChoiceRequired);
    }

    [Fact]
    public async Task End_WritesOneHistoryEntryAndOneAuditRowWithReasonPlanAndGiftFlag()
    {
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);
        var admin = await TestActors.Admin(factory).WithName("Ada", "Admin").CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(factory);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, plan, givenFreeAt: DateTimeOffset.UtcNow.AddDays(-1));

        (await EndAsync(admin, coach.UserId, "  trial abuse  ")).StatusCode.Should().Be(HttpStatusCode.OK);

        var detail = await GetCoachAsync(admin, coach.UserId);
        var entry = detail.History.Should().ContainSingle(e => e.Kind == AdminCoachHistoryKind.PlanEnded).Subject;
        entry.ByCoach.Should().BeFalse();
        entry.ActorName.Should().Be("Ada Admin");
        entry.Reason.Should().Be("trial abuse");
        entry.PlanCode.Should().Be(plan);

        var audit = (await AuditRowsAsync(coach.UserId)).Should().ContainSingle().Subject;
        audit.UserId.Should().Be(admin.UserId);
        audit.EntityType.Should().Be(nameof(ApplicationUser));
        using var values = JsonDocument.Parse(audit.NewValues!);
        values.RootElement.GetProperty("reason").GetString().Should().Be("trial abuse");
        values.RootElement.GetProperty("planCode").GetString().Should().Be(plan);
        values.RootElement.GetProperty("wasGivenFree").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task End_CoachWithNoSubscriptionRow_Returns409()
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);

        var response = await EndAsync(admin, coach.UserId);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.NoPlanToEnd);
        (await AuditRowsAsync(coach.UserId)).Should().BeEmpty();
    }

    [Fact]
    public async Task End_CoachOnTheFreePlan_Returns409()
    {
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, "free");

        var response = await EndAsync(admin, coach.UserId);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.NoPlanToEnd);
    }

    [Fact]
    public async Task End_CoachWhosePaidPlanIsAlreadyCanceled_Returns409()
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(factory);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, plan, SubscriptionStatus.Canceled);

        var response = await EndAsync(admin, coach.UserId);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.NoPlanToEnd);
    }

    [Fact]
    public async Task End_CoachWhoseGiftExpired_Returns409_AndLeavesThePlanChoiceStateAlone()
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeWithEndedGiftAsync(factory, coach, await PaidPlanBuilder.CreateAsync(factory));

        var response = await EndAsync(admin, coach.UserId);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.NoPlanToEnd);
        (await ReadSubscriptionAsync(coach.ProfileId)).GivenFreeAt.Should().NotBeNull("the coach still has to choose a plan");
    }

    [Fact]
    public async Task End_UnknownCoach_Returns404()
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);

        var response = await EndAsync(admin, Guid.NewGuid());

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.ProfessionalNotFound);
    }

    [Fact]
    public async Task End_ByANonAdmin_Returns403_AndChangesNothing()
    {
        var trainer = await TestActors.Trainer(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(factory);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, plan);

        var response = await EndAsync(trainer, coach.UserId);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadSubscriptionAsync(coach.ProfileId)).Status.Should().Be(SubscriptionStatus.Active);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task End_WithoutAReason_Returns400(string reason)
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(factory);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, plan);

        var response = await EndAsync(admin, coach.UserId, reason);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadSubscriptionAsync(coach.ProfileId)).Status.Should().Be(SubscriptionStatus.Active);
    }
}
