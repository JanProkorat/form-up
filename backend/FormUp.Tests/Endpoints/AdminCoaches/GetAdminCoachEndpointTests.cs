using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.AdminCoaches.GetAdminCoach;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.AdminCoaches;

/// <summary>
/// Integration tests for <c>GET /admin/coaches/{CoachId}</c> (<c>GetAdminCoachEndpoint</c>).
/// </summary>
[Collection(TestCollection.Name)]
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
        detail.Roles.Should().BeEmpty("the account ended at once");
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
