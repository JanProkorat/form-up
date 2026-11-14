using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Domain.Constants;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;

namespace FormUp.Tests.Endpoints.ClientMeasurements;

/// <summary>
/// A coach whose plan lacks weight and measurements cannot read or add a client's measurements; the client's own
/// measurement endpoints are not touched by the coach's plan.
/// </summary>
[Collection(TestCollection.Name)]
public class ClientMeasurementsEntitlementTests(FitnessApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(Actor Coach, Actor Client)> LinkedPairAsync(bool canUseWeightAndMeasurements)
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).CreateAsync(Ct);
        await CoachSubscriptionBuilder.MoveToPlanAsync(
            factory, coach, plan => plan.CanUseWeightAndMeasurements = canUseWeightAndMeasurements);

        return (coach, client);
    }

    [Fact]
    public async Task CoachMeasurementEndpoints_PlanWithoutMeasurements_Return403SubscriptionRequired()
    {
        var (coach, client) = await LinkedPairAsync(canUseWeightAndMeasurements: false);
        var route = $"/trainer/clients/{client.PublicId}/measurements";

        var read = await coach.Http.GetAsync(route, Ct);
        var add = await coach.Http.PostAsJsonAsync(route, new { MeasuredAt = DateTime.UtcNow, WeightKg = 80m }, Ct);

        foreach (var response in new[] { read, add })
        {
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await response.Content.ReadAsStringAsync(Ct)).Should().Contain(ErrorCodes.SubscriptionRequired);
        }
    }

    [Fact]
    public async Task CoachMeasurementEndpoints_PlanWithMeasurements_ReachTheEndpoint()
    {
        var (coach, client) = await LinkedPairAsync(canUseWeightAndMeasurements: true);

        var read = await coach.Http.GetAsync($"/trainer/clients/{client.PublicId}/measurements", Ct);

        read.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ClientMeasurementEndpoints_CoachPlanWithoutMeasurements_StayOpen()
    {
        var (_, client) = await LinkedPairAsync(canUseWeightAndMeasurements: false);

        var add = await client.Http.PostAsJsonAsync("/client/measurements", new { MeasuredAt = DateTime.UtcNow, WeightKg = 80m }, Ct);
        var read = await client.Http.GetAsync("/client/measurements", Ct);

        add.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Created);
        read.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
