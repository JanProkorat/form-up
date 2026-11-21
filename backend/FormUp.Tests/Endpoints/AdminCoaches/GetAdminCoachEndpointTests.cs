using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.AdminCoaches.GetAdminCoach;
using FormUp.Application.Features.AdminCoaches.Shared;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.AdminCoaches;

/// <summary>
/// Integration tests for <c>GET /admin/coaches/{CoachId}</c> (<c>GetAdminCoachEndpoint</c>).
/// </summary>
[Collection(TestCollection.Shard2)]
public class GetAdminCoachEndpointTests(FitnessApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<GetAdminCoachResponse> GetAsync(Actor admin, Guid coachId)
    {
        var response = await admin.Http.GetAsync($"/admin/coaches/{coachId}", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return (await response.Content.ReadFromJsonAsync<GetAdminCoachResponse>(JsonOptions, Ct))!;
    }

    [Fact]
    public async Task Get_UnknownCoach_Returns404()
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);

        var response = await admin.Http.GetAsync($"/admin/coaches/{Guid.NewGuid()}", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.ProfessionalNotFound);
    }

    [Fact]
    public async Task Get_PayingCoach_ReturnsThePlanCardWithTheirOwnPriceAndTheClientsBar()
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(
            factory, "EUR", priceMinorUnits: 2900, addOnMinorUnits: 900, bothRolesIncluded: false, maxActiveClients: 10);
        var periodEnd = DateTimeOffset.UtcNow.AddDays(9);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(
            factory, coach, plan, SubscriptionStatus.PastDue, paysSecondRoleAddOn: true, currentPeriodEndsAt: periodEnd);

        foreach (var _ in Enumerable.Range(0, 3))
        {
            var client = await TestActors.Client(factory).CreateAsync(Ct);
            await TestActors.Link(factory, coach, client).CreateAsync(Ct);
        }

        var detail = await GetAsync(admin, coach.UserId);

        detail.CoachId.Should().Be(coach.UserId);
        detail.Email.Should().Be(coach.Email);
        detail.Roles.Should().Equal("Trainer");
        detail.Plan.Code.Should().Be(plan);
        detail.Status.Should().Be(AdminCoachStatus.PaymentFailed);
        detail.PriceMinorUnits.Should().Be(3800, "the plan price plus the add-on the coach pays");
        detail.Currency.Should().Be("EUR");
        detail.BillingInterval.Should().Be(BillingInterval.Monthly);
        detail.CurrentPeriodEndsAt.Should().BeCloseTo(periodEnd, TimeSpan.FromSeconds(1));
        detail.ClientsUsed.Should().Be(3);
        detail.ClientsMax.Should().Be(10);
        detail.FailureDetail.Should().BeNull("there is no payment provider to say why");
        detail.History.Should().ContainSingle(entry => entry.Kind == AdminCoachHistoryKind.Joined);
    }

    [Fact]
    public async Task Get_CoachOnTheAnnualPrice_ReportsAnnualIntervalAndTheirYearlyPrice()
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(
            factory, "EUR", priceMinorUnits: 2900, addOnMinorUnits: 900, bothRolesIncluded: false);
        await SubscriptionPlanPriceBuilder.AddOpenAnnualPriceAsync(factory, plan, 29000, 9000);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(
            factory, coach, plan, paysSecondRoleAddOn: true, interval: BillingInterval.Annual);

        var detail = await GetAsync(admin, coach.UserId);

        detail.BillingInterval.Should().Be(BillingInterval.Annual);
        detail.PriceMinorUnits.Should().Be(38000, "the annual plan price plus the annual add-on the coach pays");
    }

    [Theory]
    [InlineData("paying", true)]
    [InlineData("failed", true)]
    [InlineData("gift", true)]
    [InlineData("canceled", false)]
    [InlineData("free", false)]
    [InlineData("none", false)]
    public async Task Get_ReportsWhetherThePlanCanBeEndedNow_AndTheFreePlanCap(string state, bool canEnd)
    {
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(factory);

        switch (state)
        {
            case "paying":
                await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, plan);
                break;
            case "failed":
                await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, plan, SubscriptionStatus.PastDue);
                break;
            case "gift":
                await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, plan, givenFreeAt: DateTimeOffset.UtcNow);
                break;
            case "canceled":
                await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, plan, SubscriptionStatus.Canceled);
                break;
            case "free":
                await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, "free");
                break;
        }

        var detail = await GetAsync(admin, coach.UserId);

        detail.CanEndPlanNow.Should().Be(canEnd);
        detail.FreePlanMaxActiveClients.Should().Be(2);
    }

    [Theory]
    [InlineData(SubscriptionStatus.Active, true, true)]
    [InlineData(SubscriptionStatus.Incomplete, true, true)]
    [InlineData(SubscriptionStatus.Canceled, true, false)]
    [InlineData(SubscriptionStatus.Active, false, false)]
    public async Task Get_ReportsACancellableStripeSubscription_AndTheAddOn(SubscriptionStatus status, bool withStripeId, bool cancellable)
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(factory);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, plan, status, paysSecondRoleAddOn: true);

        if (withStripeId)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.CoachSubscriptions.Where(cs => cs.ProfessionalProfileId == coach.ProfileId)
                .ExecuteUpdateAsync(s => s.SetProperty(cs => cs.ExternalSubscriptionId, $"sub_{Guid.NewGuid():N}"), Ct);
        }

        var detail = await GetAsync(admin, coach.UserId);

        detail.HasCancellableSubscription.Should().Be(cancellable);
    }

    [Fact]
    public async Task Get_AfterAdminAndCoachActions_ListsThemNewestFirstWithTheActorAndReason()
    {
        var admin = await TestActors.Admin(factory).WithName("Ada", "Admin").CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var freeUntil = DateTimeOffset.UtcNow.AddDays(30);

        (await admin.Http.PostAsJsonAsync(
            $"/admin/coaches/{coach.UserId}/give-free", new { PlanCode = plan, FreeUntil = freeUntil, Reason = "beta tester" }, Ct))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await coach.Http.PostAsync("/users/me/coach-account/disable", null, Ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var detail = await GetAsync(admin, coach.UserId);

        detail.Status.Should().Be(AdminCoachStatus.GivenFree);
        detail.Roles.Should().NotBeEmpty("the account stays active until the plan set by Form Up ends (#2177)");
        detail.History.Select(entry => entry.Kind).Should().Equal(
            AdminCoachHistoryKind.AccountDisabled, AdminCoachHistoryKind.GivenFree, AdminCoachHistoryKind.Joined);

        var disabled = detail.History[0];
        disabled.ByCoach.Should().BeTrue();
        disabled.ActorName.Should().BeNull();

        var gift = detail.History[1];
        gift.ByCoach.Should().BeFalse();
        gift.ActorName.Should().Be("Ada Admin");
        gift.Reason.Should().Be("beta tester");
        gift.PlanCode.Should().Be(plan);
        gift.Until.Should().BeCloseTo(freeUntil, TimeSpan.FromSeconds(1));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Get_ReturnsTheStripeCustomerId_OrNullWithoutOne(bool hasCustomer)
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(factory);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, plan);
        var customerId = $"cus_{Guid.NewGuid():N}";

        if (hasCustomer)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.CoachSubscriptions.Where(cs => cs.ProfessionalProfileId == coach.ProfileId)
                .ExecuteUpdateAsync(s => s.SetProperty(cs => cs.ExternalCustomerId, customerId), Ct);
        }

        var detail = await GetAsync(admin, coach.UserId);

        detail.StripeCustomerId.Should().Be(hasCustomer ? customerId : null);
    }

    [Fact]
    public async Task Get_CoachWithNoSubscriptionRow_ReturnsNullStripeCustomerId()
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);

        var detail = await GetAsync(admin, coach.UserId);

        detail.StripeCustomerId.Should().BeNull();
    }

    [Fact]
    public async Task Get_ListsThePaidPlanEventsAndTheCoachsFreePlanChoice_OnceEach()
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var other = await TestActors.Trainer(factory).CreateAsync(Ct);
        var now = DateTimeOffset.UtcNow;

        CoachEvent Event(Guid coachId, CoachEventType type, int daysAgo, string? planCode) => new()
        {
            CoachUserId = coachId,
            EventType = type,
            OccurredAt = now.AddDays(-daysAgo),
            IdempotencyKey = $"test:{Guid.NewGuid():N}",
            PlanCode = planCode,
        };

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.CoachEvents.AddRange(
                Event(coach.UserId, CoachEventType.StartedPaidPlan, 50, "pro"),
                Event(coach.UserId, CoachEventType.Upgraded, 40, "max"),
                Event(coach.UserId, CoachEventType.Downgraded, 30, "pro"),
                Event(coach.UserId, CoachEventType.Cancelled, 20, null),
                // Never shown: not a plan-lifecycle event of the history, or another coach's.
                Event(coach.UserId, CoachEventType.ChosePlan, 10, "free"),
                Event(coach.UserId, CoachEventType.PaymentFailed, 9, "pro"),
                Event(other.UserId, CoachEventType.StartedPaidPlan, 8, "pro"));
            db.AuditLogs.Add(new AuditLog
            {
                UserId = coach.UserId,
                Action = "ChooseFreePlan",
                EntityType = nameof(ApplicationUser),
                EntityId = coach.UserId,
                NewValues = "{\"planCode\":\"free\",\"keptClientCount\":0,\"releasedClientCount\":0}",
            });
            await db.SaveChangesAsync(Ct);
        }

        var detail = await GetAsync(admin, coach.UserId);

        // The coach just joined, so Joined sorts among the newest; the rest keep their dates' order.
        detail.History.Should().ContainSingle(entry => entry.Kind == AdminCoachHistoryKind.Joined);
        detail.History.Where(entry => entry.Kind != AdminCoachHistoryKind.Joined).Select(entry => entry.Kind).Should().Equal(
            AdminCoachHistoryKind.ChoseFreePlan,
            AdminCoachHistoryKind.PaidPlanEnded,
            AdminCoachHistoryKind.PaidPlanDowngraded,
            AdminCoachHistoryKind.PaidPlanUpgraded,
            AdminCoachHistoryKind.PaidPlanStarted);
        detail.History.Where(entry => entry.Kind != AdminCoachHistoryKind.Joined)
            .Should().OnlyContain(entry => entry.ActorName == null);
        detail.History.Where(entry => entry.Kind != AdminCoachHistoryKind.Joined && entry.Kind != AdminCoachHistoryKind.PaidPlanEnded)
            .Should().OnlyContain(entry => entry.ByCoach);
        detail.History.Single(entry => entry.Kind == AdminCoachHistoryKind.PaidPlanEnded).ByCoach.Should().BeFalse();
        detail.History.Single(entry => entry.Kind == AdminCoachHistoryKind.PaidPlanUpgraded).PlanCode.Should().Be("max");
        detail.History.Single(entry => entry.Kind == AdminCoachHistoryKind.ChoseFreePlan).PlanCode.Should().Be("free");
    }

    private async Task SeedAdminAuditAndCancelEventsAsync(
        Actor admin, Actor coach, string action, bool providerCancelled, params TimeSpan[] eventOffsets)
    {
        var auditAt = DateTime.UtcNow.AddMinutes(-30);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var audit = new AuditLog
        {
            UserId = admin.UserId,
            Action = action,
            EntityType = nameof(ApplicationUser),
            EntityId = coach.UserId,
            NewValues = $"{{\"planCode\":\"pro\",\"providerSubscriptionCancelled\":{(providerCancelled ? "true" : "false")}}}",
        };
        db.AuditLogs.Add(audit);
        await db.SaveChangesAsync(Ct);

        // DateCreated is stamped on insert, so move the row to the instant the test needs.
        var auditId = audit.Id;
        await db.AuditLogs.Where(log => log.Id == auditId)
            .ExecuteUpdateAsync(s => s.SetProperty(log => log.DateCreated, auditAt), Ct);

        db.CoachEvents.AddRange(eventOffsets.Select(offset => new CoachEvent
        {
            CoachUserId = coach.UserId,
            EventType = CoachEventType.Cancelled,
            OccurredAt = new DateTimeOffset(auditAt, TimeSpan.Zero) + offset,
            IdempotencyKey = $"test:{Guid.NewGuid():N}",
        }));
        await db.SaveChangesAsync(Ct);
    }

    [Theory]
    [InlineData(AdminCoachAuditActions.EndPlan, 5, AdminCoachHistoryKind.PlanEnded)]
    [InlineData(AdminCoachAuditActions.EndPlan, -5, AdminCoachHistoryKind.PlanEnded)]
    [InlineData(AdminCoachAuditActions.GivePlanFree, 5, AdminCoachHistoryKind.GivenFree)]
    public async Task Get_AdminCancelAtStripeAndCancelledEventWithinTheWindow_ShowsOnlyTheAuditRow(
        string action, int eventOffsetSeconds, AdminCoachHistoryKind auditKind)
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await SeedAdminAuditAndCancelEventsAsync(admin, coach, action, true, TimeSpan.FromSeconds(eventOffsetSeconds));

        var detail = await GetAsync(admin, coach.UserId);

        detail.History.Select(entry => entry.Kind).Should().NotContain(AdminCoachHistoryKind.PaidPlanEnded);
        detail.History.Should().ContainSingle(entry => entry.Kind == auditKind);
    }

    [Theory]
    [InlineData(600, true)]
    [InlineData(5, false)]
    public async Task Get_CancelledEventOutsideTheWindowOrWithoutAProviderCancel_ShowsBothRows(
        int eventOffsetSeconds, bool providerCancelled)
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await SeedAdminAuditAndCancelEventsAsync(
            admin, coach, AdminCoachAuditActions.EndPlan, providerCancelled, TimeSpan.FromSeconds(eventOffsetSeconds));

        var detail = await GetAsync(admin, coach.UserId);

        detail.History.Should().ContainSingle(entry => entry.Kind == AdminCoachHistoryKind.PlanEnded);
        detail.History.Should().ContainSingle(entry => entry.Kind == AdminCoachHistoryKind.PaidPlanEnded);
    }

    [Fact]
    public async Task Get_OneAdminCancelAndTwoCancelledEventsInTheWindow_HidesExactlyOneEvent()
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await SeedAdminAuditAndCancelEventsAsync(
            admin, coach, AdminCoachAuditActions.EndPlan, true, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30));

        var detail = await GetAsync(admin, coach.UserId);

        detail.History.Should().ContainSingle(entry => entry.Kind == AdminCoachHistoryKind.PlanEnded);
        detail.History.Should().ContainSingle(entry => entry.Kind == AdminCoachHistoryKind.PaidPlanEnded);
    }

    [Fact]
    public async Task Get_LoneCancelledEvent_IsNotAttributedToTheCoach_WhileAStartedEventIs()
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.CoachEvents.AddRange(
                new CoachEvent
                {
                    CoachUserId = coach.UserId,
                    EventType = CoachEventType.StartedPaidPlan,
                    OccurredAt = DateTimeOffset.UtcNow.AddDays(-2),
                    IdempotencyKey = $"test:{Guid.NewGuid():N}",
                    PlanCode = "pro",
                },
                new CoachEvent
                {
                    CoachUserId = coach.UserId,
                    EventType = CoachEventType.Cancelled,
                    OccurredAt = DateTimeOffset.UtcNow.AddDays(-1),
                    IdempotencyKey = $"test:{Guid.NewGuid():N}",
                });
            await db.SaveChangesAsync(Ct);
        }

        var detail = await GetAsync(admin, coach.UserId);

        var ended = detail.History.Single(entry => entry.Kind == AdminCoachHistoryKind.PaidPlanEnded);
        ended.ByCoach.Should().BeFalse();
        ended.ActorName.Should().BeNull();
        detail.History.Single(entry => entry.Kind == AdminCoachHistoryKind.PaidPlanStarted).ByCoach.Should().BeTrue();
    }

    [Fact]
    public async Task ListAndGet_NeverExposeAClientsDetails_OrAnotherEntitysAuditRows_OrIpAddressesAndRawValues()
    {
        var admin = await TestActors.Admin(factory).CreateAsync(Ct);
        var token = Guid.NewGuid().ToString("N")[..12];
        var coach = await TestActors.Trainer(factory).WithEmail($"{token}@coaches-fixture.com").CreateAsync(Ct);
        var client = await TestActors.Client(factory)
            .WithEmail($"client-{token}@clients-fixture.com")
            .WithName($"Zyxwq{token}", $"Plomb{token}")
            .CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).CreateAsync(Ct);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.AuditLogs.AddRange(
                // Filed under the coach's profile public id with the client as actor, like an accepted invite.
                new AuditLog
                {
                    UserId = client.UserId,
                    Action = "AddRole",
                    EntityType = nameof(ProfessionalProfile),
                    EntityId = coach.PublicId,
                    NewValues = $"{{\"addedRole\":\"Client\",\"clientEmail\":\"client-{token}@clients-fixture.com\"}}",
                },
                // Right entity, an action that is not part of the coach history.
                new AuditLog
                {
                    UserId = coach.UserId,
                    Action = "UpdateProfile",
                    EntityType = nameof(ApplicationUser),
                    EntityId = coach.UserId,
                    NewValues = "{\"leak\":\"NOT-IN-THE-ALLOW-LIST\"}",
                },
                // A history action carrying an IP address and old values that must stay inside the audit log.
                new AuditLog
                {
                    UserId = admin.UserId,
                    Action = "AdminGiveCoachPlanFree",
                    EntityType = nameof(ApplicationUser),
                    EntityId = coach.UserId,
                    IpAddress = "203.0.113.77",
                    OldValues = "{\"leak\":\"OLD-VALUES-LEAK\"}",
                    NewValues = "{\"planCode\":\"pro\",\"reason\":\"visible reason\"}",
                });
            await db.SaveChangesAsync(Ct);
        }

        var listJson = await (await admin.Http.GetAsync($"/admin/coaches?search={token}", Ct)).Content.ReadAsStringAsync(Ct);
        var detailResponse = await admin.Http.GetAsync($"/admin/coaches/{coach.UserId}", Ct);
        var detailJson = await detailResponse.Content.ReadAsStringAsync(Ct);
        var detail = JsonSerializer.Deserialize<GetAdminCoachResponse>(detailJson, JsonOptions)!;

        listJson.Should().Contain(coach.Email, "the coach's own details are shown");

        foreach (var json in new[] { listJson, detailJson })
        {
            json.Should().NotContain($"client-{token}");
            json.Should().NotContain($"Zyxwq{token}");
            json.Should().NotContain($"Plomb{token}");
            json.Should().NotContain(client.UserId.ToString());
            json.Should().NotContain(client.PublicId.ToString());
            json.Should().NotContain("203.0.113.77");
            json.Should().NotContain("OLD-VALUES-LEAK");
            json.Should().NotContain("NOT-IN-THE-ALLOW-LIST");
        }

        detail.History.Select(entry => entry.Kind).Should().Equal(AdminCoachHistoryKind.GivenFree, AdminCoachHistoryKind.Joined);
        detail.History[0].Reason.Should().Be("visible reason");
        detail.ClientsUsed.Should().Be(1, "only the count of clients is shown");
    }
}
