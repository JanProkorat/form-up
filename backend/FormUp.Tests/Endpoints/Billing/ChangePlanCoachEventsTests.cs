using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Services.Billing;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Billing;

/// <summary>
/// The coach events <c>POST /billing/change-plan</c> writes: an Upgraded row only for an upgrade that applies now.
/// </summary>
[Collection(TestCollection.Name)]
public class ChangePlanCoachEventsTests(FitnessApiFactory factory) : IDisposable
{
    private const string Route = "/billing/change-plan";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Leaves the shared fake provider in its defaults for the next test class.</summary>
    public void Dispose() => BillingTestData.ResetProvider(factory);

    private static object Body(string planCode) => new { PlanCode = planCode, AttemptId = Guid.NewGuid() };

    private async Task<List<CoachEvent>> EventsOfAsync(Actor coach)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.CoachEvents.AsNoTracking().Where(e => e.CoachUserId == coach.UserId).ToListAsync(Ct);
    }

    private async Task<(Actor Coach, string Current, string Target)> ArrangeAsync(long currentPrice, long targetPrice)
    {
        BillingTestData.ResetProvider(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (current, _) = await BillingTestData.StripePlanAsync(factory, price: currentPrice);
        var (target, _) = await BillingTestData.StripePlanAsync(factory, price: targetPrice);
        await BillingTestData.SubscribeLiveAsync(factory, coach, current);

        return (coach, current, target);
    }

    [Fact]
    public async Task ChangePlan_UpgradeThatAppliesNow_WritesOneUpgraded_WithTheRevenueEffect()
    {
        var (coach, current, target) = await ArrangeAsync(29900, 59900);

        var response = await coach.Http.PostAsJsonAsync(Route, Body(target), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var upgraded = (await EventsOfAsync(coach)).Should().ContainSingle().Subject;
        upgraded.EventType.Should().Be(CoachEventType.Upgraded);
        upgraded.PreviousPlanCode.Should().Be(current);
        upgraded.PlanCode.Should().Be(target);
        upgraded.FullMonthlyRevenueEffectMinorUnits.Should().Be(30000);
    }

    [Fact]
    public async Task ChangePlan_UpgradeAwaitingPayment_WritesNoEvent()
    {
        var (coach, _, target) = await ArrangeAsync(29900, 59900);
        BillingTestData.ResetProvider(factory).UpgradeOutcome = PlanUpgradeOutcome.AwaitingPayment("https://invoice.stripe.test/i/in_1");

        var response = await coach.Http.PostAsJsonAsync(Route, Body(target), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await EventsOfAsync(coach)).Should().BeEmpty("the webhook that applies the plan writes the event");
    }

    [Fact]
    public async Task ChangePlan_ScheduledDowngrade_WritesNoEvent()
    {
        var (coach, _, target) = await ArrangeAsync(59900, 29900);

        var response = await coach.Http.PostAsJsonAsync(Route, Body(target), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await EventsOfAsync(coach)).Should().BeEmpty("the renewal webhook that switches the price writes the Downgraded row");
    }

    [Fact]
    public async Task ChangePlan_WhenStripeFails_WritesNoEvent()
    {
        var (coach, _, target) = await ArrangeAsync(29900, 59900);
        BillingTestData.ResetProvider(factory).FailPlanChanges = true;

        var response = await coach.Http.PostAsJsonAsync(Route, Body(target), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        (await EventsOfAsync(coach)).Should().BeEmpty();
    }
}
