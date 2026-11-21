using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Domain.Enums;
using FormUp.Tests.Builders;
using FluentAssertions;
using FormUp.Tests.Infrastructure;

namespace FormUp.Tests.Endpoints.WeeklyCheckIns;

/// <summary>
/// Integration tests for GET /client/weekly-check-ins/history.
/// Uses Testcontainers PostgreSQL (Docker required).
/// </summary>
[Collection(TestCollection.Shard2)]
public class GetMyCheckInHistoryEndpointTests(FitnessApiFactory factory)
{
    private const string Route = "/client/weekly-check-ins/history";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GetHistory_NoCheckIns_ReturnsEmptyListAndZeroTotal()
    {
        var client = await TestActors.Client(factory).CreateAsync(Ct);

        var response = await client.Http.GetAsync(Route, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("X-Total-Count").Single().Should().Be("0");
        var body = await response.Content.ReadFromJsonAsync<HistoryBody>(cancellationToken: Ct);
        body!.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task GetHistory_ReturnsOnlyCallersOwnCheckIns_AllStatuses_WithoutAnswers()
    {
        var trainer = await TestActors.Trainer(factory).WithName("Tom", "Coach").CreateAsync(Ct);
        var scenario = await CheckInFixtures.CreateScenarioAsync(factory, coach: trainer);
        var dueAt = DateTime.UtcNow.AddDays(-12);
        var reviewedAt = DateTime.UtcNow.AddDays(-11);

        var answeredId = await CheckInFixtures.AddCheckInAsync(
            factory, scenario, CheckInFixtures.Monday(2), dueAt,
            QuestionnaireResponseStatus.Submitted, DateTime.UtcNow.AddDays(-13), reviewedAt);
        var expiredId = await CheckInFixtures.AddCheckInAsync(
            factory, scenario, CheckInFixtures.Monday(1), DateTime.UtcNow.AddDays(-5));
        var pendingId = await CheckInFixtures.AddCheckInAsync(
            factory, scenario, CheckInFixtures.Monday(0), DateTime.UtcNow.AddDays(2));
        var otherClient = await CheckInFixtures.CreateScenarioAsync(factory, coach: trainer);
        await CheckInFixtures.AddCheckInAsync(factory, otherClient, CheckInFixtures.Monday(2), dueAt);

        var response = await scenario.Client.Http.GetAsync(Route, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("X-Total-Count").Single().Should().Be("3");
        var body = await response.Content.ReadFromJsonAsync<HistoryBody>(cancellationToken: Ct);
        body!.Items.Select(i => i.Id).Should().Equal(pendingId, expiredId, answeredId);
        body.Items[0].Status.Should().Be("Pending");
        body.Items[1].Status.Should().Be("Expired");
        body.Items[2].Status.Should().Be("Reviewed");
        body.Items[2].DueAt.Should().BeCloseTo(dueAt, TimeSpan.FromSeconds(1));
        body.Items[2].ReviewedAt.Should().BeCloseTo(reviewedAt, TimeSpan.FromSeconds(1));
        body.Items[1].ReviewedAt.Should().BeNull();
        body.Items[2].ProfessionalName.Should().Be("Tom Coach");
        body.Items[2].Profession.Should().Be("Training");
        body.Items[2].RespondedAt.Should().NotBeNull();
        body.Items[2].DismissedAt.Should().BeNull();

        var raw = await response.Content.ReadAsStringAsync(Ct);
        raw.Should().NotContainEquivalentOf("flags").And.NotContainEquivalentOf("hungerScore");
    }

    [Fact]
    public async Task GetHistory_Paged_ReturnsRequestedSliceAndFullTotal()
    {
        var scenario = await CheckInFixtures.CreateScenarioAsync(factory);
        var ids = new List<Guid>();
        foreach (var weeksAgo in Enumerable.Range(0, 5))
        {
            ids.Add(await CheckInFixtures.AddCheckInAsync(
                factory, scenario, CheckInFixtures.Monday(weeksAgo), DateTime.UtcNow.AddDays(-7 * weeksAgo)));
        }

        var response = await scenario.Client.Http.GetAsync($"{Route}?page=2&pageSize=2", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("X-Total-Count").Single().Should().Be("5");
        var body = await response.Content.ReadFromJsonAsync<HistoryBody>(cancellationToken: Ct);
        body!.Items.Select(i => i.Id).Should().Equal(ids[2], ids[3]);
    }

    [Fact]
    public async Task GetHistory_InvalidPageSize_Returns400()
    {
        var client = await TestActors.Client(factory).CreateAsync(Ct);

        var response = await client.Http.GetAsync($"{Route}?pageSize=101", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private record HistoryBody(List<HistoryItem> Items);

    private record HistoryItem(
        Guid Id,
        string ProfessionalName,
        string Profession,
        string Status,
        DateTime? RespondedAt,
        DateTime? DismissedAt,
        DateTime? DueAt,
        DateTime? ReviewedAt);
}
