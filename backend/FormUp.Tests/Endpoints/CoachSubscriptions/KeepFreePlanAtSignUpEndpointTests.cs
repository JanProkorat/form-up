using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.CoachSubscriptions;

/// <summary>
/// Integration tests for <c>POST /users/me/subscription/keep-free</c>.
/// </summary>
[Collection(TestCollection.Shard3)]
public class KeepFreePlanAtSignUpEndpointTests(FitnessApiFactory factory)
{
    private const string Route = "/users/me/subscription/keep-free";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<List<CoachEvent>> EventsOfAsync(Actor coach)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.CoachEvents.AsNoTracking().Where(e => e.CoachUserId == coach.UserId).ToListAsync(Ct);
    }

    private async Task<int> SubscriptionRowsOfAsync(Actor coach)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.CoachSubscriptions.AsNoTracking().CountAsync(cs => cs.ProfessionalProfileId == coach.ProfileId, Ct);
    }

    [Fact]
    public async Task KeepFree_FreshCoachWithNoSubscription_Returns204AndRecordsOneFreeChoice()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);

        var response = await coach.Http.PostAsync(Route, null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var recorded = (await EventsOfAsync(coach)).Should().ContainSingle().Subject;
        recorded.EventType.Should().Be(CoachEventType.ChosePlan);
        recorded.PlanCode.Should().Be(SubscriptionPlanCodes.Free);
        recorded.IdempotencyKey.Should().Be($"signup-free:{coach.UserId}");
        recorded.FullMonthlyRevenueEffectMinorUnits.Should().Be(0);
        recorded.DiscountedMonthlyRevenueEffectMinorUnits.Should().Be(0);
        (await SubscriptionRowsOfAsync(coach)).Should().Be(0);
    }

    [Fact]
    public async Task KeepFree_CalledTwice_StaysAt204WithASingleEventRow()
    {
        var coach = await TestActors.Nutritionist(factory).CreateAsync(Ct);

        (await coach.Http.PostAsync(Route, null, Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await coach.Http.PostAsync(Route, null, Ct)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await EventsOfAsync(coach)).Should().ContainSingle();
    }

    [Fact]
    public async Task KeepFree_FreeRowFromAnAbandonedCheckout_RecordsOnce()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, SubscriptionPlanCodes.Free);

        var response = await coach.Http.PostAsync(Route, null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await EventsOfAsync(coach)).Should().ContainSingle().Which.PlanCode.Should().Be(SubscriptionPlanCodes.Free);
    }

    [Fact]
    public async Task KeepFree_CoachOnALivePaidPlan_Returns409AndRecordsNothing()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, "pro");

        var response = await coach.Http.PostAsync(Route, null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errorCode").GetString()
            .Should().Be(ErrorCodes.NotOnFreePlan);
        (await EventsOfAsync(coach)).Should().BeEmpty();
    }

    [Fact]
    public async Task KeepFree_CoachWithAGivenPlanStillRunning_Returns409AndRecordsNothing()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(
            factory,
            coach,
            "pro",
            freeUntil: DateTimeOffset.UtcNow.AddDays(10),
            givenFreeAt: DateTimeOffset.UtcNow.AddDays(-5));

        var response = await coach.Http.PostAsync(Route, null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await EventsOfAsync(coach)).Should().BeEmpty();
    }

    [Fact]
    public async Task KeepFree_CoachWhoseGivenPlanEnded_IsStoppedByTheWriteGateWithNoEvent()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeWithEndedGiftAsync(factory, coach);

        var response = await coach.Http.PostAsync(Route, null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errorCode").GetString()
            .Should().Be(ErrorCodes.PlanChoiceRequired);
        (await EventsOfAsync(coach)).Should().BeEmpty();
    }

    [Fact]
    public async Task KeepFree_CoachRoleWithoutAProfessionalProfile_Returns404()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.ProfessionalProfiles.Where(p => p.Id == coach.ProfileId).ExecuteDeleteAsync(Ct);
        }

        var response = await coach.Http.PostAsync(Route, null, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await EventsOfAsync(coach)).Should().BeEmpty();
    }
}
