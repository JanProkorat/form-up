using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Domain.Constants;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;

namespace FormUp.Tests.Endpoints.Automations;

/// <summary>
/// A coach whose plan lacks automations is refused on every coach automation endpoint, reads included; the rules they
/// already have stay stored.
/// </summary>
[Collection(TestCollection.Name)]
public class AutomationsEntitlementTests(FitnessApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(Actor Coach, Actor Client)> LinkedPairAsync(bool canUseAutomations)
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).CreateAsync(Ct);
        await CoachSubscriptionBuilder.MoveToPlanAsync(factory, coach, plan => plan.CanUseAutomations = canUseAutomations);

        return (coach, client);
    }

    [Fact]
    public async Task CoachAutomationEndpoints_PlanWithoutAutomations_Return403SubscriptionRequired()
    {
        var (coach, client) = await LinkedPairAsync(canUseAutomations: false);
        var rule = await AutomationTestData.InsertRuleAsync(factory, coach);
        var clientRoute = $"/trainer/clients/{client.PublicId}/automations";

        var responses = new Dictionary<string, HttpResponseMessage>
        {
            ["GET /trainer/automations/rules"] = await coach.Http.GetAsync("/trainer/automations/rules", Ct),
            ["GET /trainer/automations/rules/{id}"] = await coach.Http.GetAsync($"/trainer/automations/rules/{rule.PublicId}", Ct),
            ["POST /trainer/automations/rules"] = await coach.Http.PostAsJsonAsync(
                "/trainer/automations/rules", AutomationTestData.ScheduleRule(), Ct),
            ["DELETE /trainer/automations/rules/{id}"] = await coach.Http.DeleteAsync($"/trainer/automations/rules/{rule.PublicId}", Ct),
            ["GET /trainer/clients/{id}/automations"] = await coach.Http.GetAsync(clientRoute, Ct),
            ["DELETE override"] = await coach.Http.DeleteAsync($"{clientRoute}/{rule.PublicId}/override", Ct),
        };

        foreach (var (call, response) in responses)
        {
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden, call);
            (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.SubscriptionRequired, call);
        }
    }

    [Fact]
    public async Task CoachAutomationEndpoints_PlanWithAutomations_ReachTheEndpoint()
    {
        var (coach, client) = await LinkedPairAsync(canUseAutomations: true);

        var rules = await coach.Http.GetAsync("/trainer/automations/rules", Ct);
        var forClient = await coach.Http.GetAsync($"/trainer/clients/{client.PublicId}/automations", Ct);

        rules.StatusCode.Should().Be(HttpStatusCode.OK);
        forClient.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
