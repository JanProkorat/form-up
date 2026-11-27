using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Builders;
using FormUp.Tests.Endpoints.TrainingPlans;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace FormUp.Tests.Endpoints.Trainers;

/// <summary>
/// Real-Mongo tests for the dashboard's sessions-done-this-week figures. Integration rather than
/// unit on purpose: the mock Mongo ignores filters, so a unit test could not tell whether the
/// plan, session, client and date-window scoping is actually applied.
/// </summary>
[Collection(TestCollection.Shard3)]
public class GetClientDashboardSessionsThisWeekIntegrationTests(FitnessApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // The client has no time zone set, so the week is the UTC calendar week. Set per test by
    // CreateFixtureAsync, right before seeding, so it is never older than the test itself.
    private DateTime _weekStart;

    private DateTime WeekStart => _weekStart;

    // The factory has no pinned clock, so a test that straddles UTC midnight could seed one week
    // and request another. Wait out the seconds around midnight before taking the instant.
    private static async Task WaitOutMidnightBoundaryAsync()
    {
        var timeOfDay = DateTime.UtcNow.TimeOfDay;
        var margin = TimeSpan.FromSeconds(30);

        if (timeOfDay > TimeSpan.FromDays(1) - margin)
        {
            await Task.Delay(TimeSpan.FromDays(1) - timeOfDay + TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        }
    }

    private static DateTime MondayOfWeek(DateTime instant)
    {
        var date = DateOnly.FromDateTime(instant);
        var daysSinceMonday = ((int)date.DayOfWeek + 6) % 7;
        return date.AddDays(-daysSinceMonday).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
    }

    private static TrainingSession Session() => new()
    {
        SessionId = Guid.NewGuid(),
        Name = "Session",
        Order = 1,
        StandaloneExercises = Enumerable.Range(1, 2)
            .Select(i => new SessionExercise
            {
                ExerciseId = Guid.NewGuid(),
                ExerciseExternalId = Guid.NewGuid(),
                ExerciseName = $"Exercise {i}",
                Order = i
            })
            .ToList()
    };

    private sealed record Fixture(HttpClient Trainer, Guid TrainerUserId, Guid ClientPublicId, Guid ClientUserId);

    private async Task<Fixture> CreateFixtureAsync(bool canViewTrainingPlans = true)
    {
        var ct = TestContext.Current.CancellationToken;
        var tag = Guid.NewGuid().ToString("N");

        await WaitOutMidnightBoundaryAsync();
        _weekStart = MondayOfWeek(DateTime.UtcNow);

        var trainer = await TestActors.Professional(factory, UserRole.Trainer, UserRole.Nutritionist).WithEmail($"{tag}@sessions-week-t.com").CreateAsync(ct);
        var client = await TestActors.Client(factory).WithEmail($"{tag}@sessions-week-c.com").CreateAsync(ct);

        await TestActors.Link(factory, trainer.ProfileId, client.ProfileId)
            .CanViewTrainingPlans(canViewTrainingPlans)
            .CreateAsync(ct);

        return new Fixture(trainer.Http, trainer.UserId, client.PublicId, client.UserId);
    }

    private async Task<Guid> SeedPlanAsync(Fixture fixture, DateTime startDate, params TrainingWeek[] weeks) =>
        await SeedPlanAsync(fixture, fixture.TrainerUserId, startDate, weeks);

    private async Task<Guid> SeedPlanAsync(
        Fixture fixture, Guid authorUserId, DateTime startDate, params TrainingWeek[] weeks)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        var planId = Guid.NewGuid();
        await mongo.TrainingPlans.InsertOneAsync(new TrainingPlan
        {
            Id = ObjectId.GenerateNewId(),
            ExternalId = planId,
            ClientId = fixture.ClientUserId,
            TrainerId = authorUserId,
            Name = $"Plan {planId:N}",
            Status = TrainingPlanStatus.Active,
            StartDate = startDate,
            Weeks = weeks.ToList(),
            Version = 1,
            DateCreated = DateTime.UtcNow
        }, cancellationToken: TestContext.Current.CancellationToken);

        return planId;
    }

    private static TrainingWeek PublishedWeek(int number, params (int Day, TrainingSession Session)[] sessions) => new()
    {
        WeekNumber = number,
        Status = WeekStatus.Published,
        Days = TrainingPlanTestHelpers.MaterializeDays(sessions)
    };

    private async Task SeedExecutionAsync(SessionExecution execution)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        execution.Id = ObjectId.GenerateNewId();
        execution.ExternalId = Guid.NewGuid();
        execution.DateCreated = DateTime.UtcNow;
        execution.Version = 1;

        await mongo.SessionExecutions.InsertOneAsync(
            execution, cancellationToken: TestContext.Current.CancellationToken);
    }

    private static SessionExecution CheckboxExecution(
        Fixture fixture, Guid planId, TrainingSession session, DateTime date, bool allTicked = true) => new()
    {
        ClientId = fixture.ClientUserId,
        PlanId = planId,
        SessionId = session.SessionId,
        Date = date,
        Status = SessionExecutionStatus.Partial,
        CompletedExerciseInstanceIds = session.StandaloneExercises
            .Select(e => e.ExerciseId)
            .Take(allTicked ? session.StandaloneExercises.Count : 1)
            .ToList()
    };

    private static SessionExecution LiveFinishedExecution(
        Fixture fixture, Guid planId, TrainingSession session, DateTime date) => new()
    {
        ClientId = fixture.ClientUserId,
        PlanId = planId,
        SessionId = session.SessionId,
        Date = date,
        Status = SessionExecutionStatus.Completed,
        Performance = new SessionExecutionPerformance
        {
            StartedAt = date.AddHours(9),
            CompletedAt = date.AddHours(10),
            Workouts = []
        }
    };

    private async Task<(int? Completed, int? Planned)> GetCountsAsync(Fixture fixture)
    {
        var response = await fixture.Trainer.GetAsync(
            $"/trainer/clients/{fixture.ClientPublicId}", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<DashboardCounts>(
            JsonOptions, TestContext.Current.CancellationToken);

        return (body!.SessionsCompletedThisWeek, body.SessionsPlannedThisWeek);
    }

    private sealed record DashboardCounts(int? SessionsCompletedThisWeek, int? SessionsPlannedThisWeek);

    [Fact]
    public async Task GetDashboard_ActivePlanWithNoExecutions_ReturnsZeroOfPlanned()
    {
        var fixture = await CreateFixtureAsync();
        await SeedPlanAsync(fixture, WeekStart, PublishedWeek(1, (1, Session()), (3, Session())));

        var (completed, planned) = await GetCountsAsync(fixture);

        completed.Should().Be(0);
        planned.Should().Be(2);
    }

    [Fact]
    public async Task GetDashboard_OneSessionTickedFullyAndOneHalfway_CountsOnlyTheFullOne()
    {
        var fixture = await CreateFixtureAsync();
        var done = Session();
        var halfway = Session();
        var planId = await SeedPlanAsync(fixture, WeekStart, PublishedWeek(1, (1, done), (3, halfway)));

        await SeedExecutionAsync(CheckboxExecution(fixture, planId, done, WeekStart));
        await SeedExecutionAsync(CheckboxExecution(fixture, planId, halfway, WeekStart.AddDays(2), allTicked: false));

        var (completed, planned) = await GetCountsAsync(fixture);

        completed.Should().Be(1);
        planned.Should().Be(2);
    }

    [Fact]
    public async Task GetDashboard_EveryScheduledSessionComplete_MixingLiveFinishAndCheckbox_ReturnsAllOfPlanned()
    {
        var fixture = await CreateFixtureAsync();
        var live = Session();
        var ticked = Session();
        var planId = await SeedPlanAsync(fixture, WeekStart, PublishedWeek(1, (1, live), (3, ticked)));

        await SeedExecutionAsync(LiveFinishedExecution(fixture, planId, live, WeekStart));
        await SeedExecutionAsync(CheckboxExecution(fixture, planId, ticked, WeekStart.AddDays(2)));

        var (completed, planned) = await GetCountsAsync(fixture);

        completed.Should().Be(2);
        planned.Should().Be(2);
    }

    [Fact]
    public async Task GetDashboard_SameSessionExecutedOnTwoDays_CountsOnce()
    {
        var fixture = await CreateFixtureAsync();
        var repeated = Session();
        var planId = await SeedPlanAsync(fixture, WeekStart, PublishedWeek(1, (1, repeated), (3, Session())));

        await SeedExecutionAsync(CheckboxExecution(fixture, planId, repeated, WeekStart));
        await SeedExecutionAsync(CheckboxExecution(fixture, planId, repeated, WeekStart.AddDays(1)));

        var (completed, planned) = await GetCountsAsync(fixture);

        completed.Should().Be(1);
        planned.Should().Be(2);
    }

    [Fact]
    public async Task GetDashboard_LastWeekOtherPlanAdHocAndOtherClientExecutions_AreNotCounted()
    {
        var fixture = await CreateFixtureAsync();
        var otherClient = await CreateFixtureAsync();
        var thisWeekSession = Session();
        var lastWeekSession = Session();

        // Plan started a week ago: week 2 is current, week 1 is last week.
        var planId = await SeedPlanAsync(
            fixture,
            WeekStart.AddDays(-7),
            PublishedWeek(1, (1, lastWeekSession)),
            PublishedWeek(2, (1, thisWeekSession)));

        await SeedExecutionAsync(CheckboxExecution(fixture, planId, lastWeekSession, WeekStart.AddDays(-7)));

        var foreignPlanExecution = CheckboxExecution(fixture, Guid.NewGuid(), thisWeekSession, WeekStart);
        await SeedExecutionAsync(foreignPlanExecution);

        var adHoc = CheckboxExecution(fixture, planId, thisWeekSession, WeekStart.AddDays(1));
        adHoc.PlanId = null;
        adHoc.SessionId = null;
        await SeedExecutionAsync(adHoc);

        var otherClientExecution = CheckboxExecution(fixture, planId, thisWeekSession, WeekStart.AddDays(2));
        otherClientExecution.ClientId = otherClient.ClientUserId;
        await SeedExecutionAsync(otherClientExecution);

        var (completed, planned) = await GetCountsAsync(fixture);

        completed.Should().Be(0);
        planned.Should().Be(1);
    }

    [Fact]
    public async Task GetDashboard_NutritionOnlyLink_ReturnsNullCounts()
    {
        var fixture = await CreateFixtureAsync(canViewTrainingPlans: false);
        var session = Session();
        var planId = await SeedPlanAsync(fixture, WeekStart, PublishedWeek(1, (1, session)));
        await SeedExecutionAsync(CheckboxExecution(fixture, planId, session, WeekStart));

        var (completed, planned) = await GetCountsAsync(fixture);

        completed.Should().BeNull();
        planned.Should().BeNull();
    }

    [Fact]
    public async Task GetDashboard_ForeignAuthoredTrainingPlan_ReturnsNullCounts()
    {
        var fixture = await CreateFixtureAsync();
        var session = Session();
        var planId = await SeedPlanAsync(
            fixture, Guid.NewGuid(), WeekStart, PublishedWeek(1, (1, session)));
        await SeedExecutionAsync(CheckboxExecution(fixture, planId, session, WeekStart));

        var (completed, planned) = await GetCountsAsync(fixture);

        completed.Should().BeNull();
        planned.Should().BeNull();
    }

    [Fact]
    public async Task GetDashboard_NoActivePlan_ReturnsNullCounts()
    {
        var fixture = await CreateFixtureAsync();

        var (completed, planned) = await GetCountsAsync(fixture);

        completed.Should().BeNull();
        planned.Should().BeNull();
    }

    [Fact]
    public async Task GetDashboard_PlanNotStartedYet_ReturnsNullCounts()
    {
        var fixture = await CreateFixtureAsync();
        await SeedPlanAsync(fixture, WeekStart.AddDays(14), PublishedWeek(1, (1, Session())));

        var (completed, planned) = await GetCountsAsync(fixture);

        completed.Should().BeNull();
        planned.Should().BeNull();
    }

    [Fact]
    public async Task GetDashboard_CurrentWeekPastLastPublishedWeek_ReturnsNullCounts()
    {
        var fixture = await CreateFixtureAsync();

        // Three-week plan started two weeks ago: week 3 is current but only weeks 1-2 are published.
        await SeedPlanAsync(
            fixture,
            WeekStart.AddDays(-14),
            PublishedWeek(1, (1, Session())),
            PublishedWeek(2, (1, Session())),
            new TrainingWeek { WeekNumber = 3, Status = WeekStatus.Draft, Days = TrainingPlanTestHelpers.MaterializeDays() });

        var (completed, planned) = await GetCountsAsync(fixture);

        completed.Should().BeNull();
        planned.Should().BeNull();
    }
}
