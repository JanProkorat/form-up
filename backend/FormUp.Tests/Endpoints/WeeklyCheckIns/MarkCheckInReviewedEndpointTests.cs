using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.WeeklyCheckIns;

/// <summary>
/// Integration tests for POST /trainer/weekly-check-ins/{id}/mark-reviewed, which stamps the check-in's
/// form response. Uses Testcontainers PostgreSQL (Docker required).
/// </summary>
[Collection(TestCollection.Shard3)]
public class MarkCheckInReviewedEndpointTests(FitnessApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<DateTime?> ReviewedAtAsync(Guid responsePublicId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.QuestionnaireResponses
            .Where(r => r.PublicId == responsePublicId)
            .Select(r => r.ReviewedByCoachAtUtc)
            .SingleAsync(Ct);
    }

    private static Task<HttpResponseMessage> MarkAsync(Actor coach, Guid id) =>
        coach.Http.PostAsJsonAsync($"/trainer/weekly-check-ins/{id}/mark-reviewed", new { }, Ct);

    [Fact]
    public async Task MarkReviewed_OwnCheckIn_Returns200_SetsReviewedAt_Broadcasts()
    {
        var scenario = await CheckInFixtures.CreateScenarioAsync(factory);
        var id = await CheckInFixtures.AddCheckInAsync(
            factory, scenario, CheckInFixtures.Monday(0), DateTime.UtcNow.AddDays(1),
            QuestionnaireResponseStatus.Submitted, DateTime.UtcNow.AddHours(-1));
        var notifier = factory.Services.GetRequiredService<FakeRealtimeNotifier>();
        notifier.Reset();

        var response = await MarkAsync(scenario.Coach, id);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReviewedAtAsync(id)).Should().NotBeNull();
        var updatedCalls = notifier.Calls.Where(c => c.EventType == "weeklycheckinupdated").ToList();
        updatedCalls.Should().Contain(c => c.UserId == scenario.Coach.UserId);
        updatedCalls.Should().Contain(c => c.UserId == scenario.Client.UserId);
    }

    [Fact]
    public async Task MarkReviewed_CalledTwice_KeepsTheFirstTimestamp()
    {
        var scenario = await CheckInFixtures.CreateScenarioAsync(factory);
        var id = await CheckInFixtures.AddCheckInAsync(
            factory, scenario, CheckInFixtures.Monday(0), DateTime.UtcNow.AddDays(1),
            QuestionnaireResponseStatus.Submitted, DateTime.UtcNow.AddHours(-1));

        await MarkAsync(scenario.Coach, id);
        var first = await ReviewedAtAsync(id);
        var second = await MarkAsync(scenario.Coach, id);

        second.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReviewedAtAsync(id)).Should().Be(first);
    }

    [Fact]
    public async Task MarkReviewed_UnansweredPastDueCheckIn_IsAllowed()
    {
        var scenario = await CheckInFixtures.CreateScenarioAsync(factory);
        var id = await CheckInFixtures.AddCheckInAsync(
            factory, scenario, CheckInFixtures.Monday(1), DateTime.UtcNow.AddDays(-3));

        var response = await MarkAsync(scenario.Coach, id);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReviewedAtAsync(id)).Should().NotBeNull();
    }

    [Fact]
    public async Task MarkReviewed_AnotherCoachsCheckIn_Returns404()
    {
        var scenario = await CheckInFixtures.CreateScenarioAsync(factory);
        var id = await CheckInFixtures.AddCheckInAsync(
            factory, scenario, CheckInFixtures.Monday(0), DateTime.UtcNow.AddDays(1));
        var other = await TestActors.Trainer(factory).CreateAsync(Ct);

        var response = await MarkAsync(other, id);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReviewedAtAsync(id)).Should().BeNull();
    }

    [Fact]
    public async Task MarkReviewed_UnknownIdOrNonCheckInResponse_Returns404()
    {
        var scenario = await CheckInFixtures.CreateScenarioAsync(factory);
        Guid onboardingId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var onboarding = EntityBuilder.QuestionnaireResponse
                .ForQuestionnaire(scenario.QuestionnaireId)
                .OnLink(scenario.LinkId)
                .FromClient(scenario.Client.UserId)
                .FromProfessional(scenario.Coach.UserId)
                .WithStatus(QuestionnaireResponseStatus.Submitted, DateTime.UtcNow)
                .Build();
            db.QuestionnaireResponses.Add(onboarding);
            await db.SaveChangesAsync(Ct);
            onboardingId = onboarding.PublicId;
        }

        (await MarkAsync(scenario.Coach, Guid.NewGuid())).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await MarkAsync(scenario.Coach, onboardingId)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
