using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Domain.Enums;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;

namespace FormUp.Tests.Endpoints.WeeklyCheckIns;

/// <summary>
/// GET /trainer/weekly-check-ins/{id}: the summary of one check-in, addressed by its form response id.
/// </summary>
[Collection(TestCollection.Shard3)]
public class GetCheckInDetailEndpointTests(FitnessApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GetDetail_OwnSubmittedCheckIn_ReturnsRespondedSummaryWithoutLegacyAnswers()
    {
        var scenario = await CheckInFixtures.CreateScenarioAsync(factory);
        var id = await CheckInFixtures.AddCheckInAsync(
            factory, scenario, CheckInFixtures.Monday(0), DateTime.UtcNow.AddDays(1),
            QuestionnaireResponseStatus.Submitted, DateTime.UtcNow.AddHours(-1));

        var response = await scenario.Coach.Http.GetAsync($"/trainer/weekly-check-ins/{id}", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<DetailBody>(cancellationToken: Ct);
        body!.Id.Should().Be(id);
        body.ClientUserId.Should().Be(scenario.Client.UserId);
        body.Status.Should().Be("Responded");
        body.Profession.Should().Be("Training");
        body.HungerScore.Should().BeNull();
        body.Flags.Should().BeEmpty();
    }

    [Fact]
    public async Task GetDetail_AnotherCoachsCheckIn_Returns404()
    {
        var scenario = await CheckInFixtures.CreateScenarioAsync(factory);
        var id = await CheckInFixtures.AddCheckInAsync(
            factory, scenario, CheckInFixtures.Monday(0), DateTime.UtcNow.AddDays(1));
        var other = await TestActors.Trainer(factory).CreateAsync(Ct);

        var response = await other.Http.GetAsync($"/trainer/weekly-check-ins/{id}", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private sealed record DetailBody(Guid Id, Guid ClientUserId, string Status, string Profession, int? HungerScore, List<string> Flags);
}
