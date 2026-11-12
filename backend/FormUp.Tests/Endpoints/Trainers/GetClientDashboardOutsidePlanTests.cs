using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using FormUp.Application.Features.Trainers.GetClientDashboard;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;

namespace FormUp.Tests.Endpoints.Trainers;

/// <summary>
/// Integration tests for <c>IsOutsidePlan</c> on <c>GET /trainer/clients/{clientId}</c>.
/// </summary>
[Collection(TestCollection.Name)]
public class GetClientDashboardOutsidePlanTests(FitnessApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<bool> IsOutsidePlanAsync(Actor coach, Actor client)
    {
        var response = await coach.Http.GetAsync($"/trainer/clients/{client.PublicId}", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return (await response.Content.ReadFromJsonAsync<GetClientDashboardResponse>(JsonOptions, Ct))!.IsOutsidePlan;
    }

    [Fact]
    public async Task Get_ClientOverTheCap_IsOutsideThePlanWhileAKeptClientIsNot()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, "free");
        var clients = new List<Actor>();

        // The Free plan keeps two clients; the longest-linked ones are kept.
        foreach (var daysAgo in new[] { 30, 20, 10 })
        {
            var client = await TestActors.Client(factory).CreateAsync(Ct);
            await TestActors.Link(factory, coach, client)
                .WithActivatedAtUtc(DateTime.UtcNow.AddDays(-daysAgo))
                .CreateAsync(Ct);
            clients.Add(client);
        }

        (await IsOutsidePlanAsync(coach, clients[0])).Should().BeFalse();
        (await IsOutsidePlanAsync(coach, clients[2])).Should().BeTrue();
    }

    [Fact]
    public async Task Get_CoachWithinTheCap_IsNeverOutsideThePlan()
    {
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, "free");
        var client = await TestActors.Client(factory).CreateAsync(Ct);
        await TestActors.Link(factory, coach, client).CreateAsync(Ct);

        (await IsOutsidePlanAsync(coach, client)).Should().BeFalse();
    }
}
