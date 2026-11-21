using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace FormUp.Tests.Endpoints.Authorization;

/// <summary>
/// The trainer plan-detail route folded session executions in on a client-only filter, with no plan
/// or session term. A client legitimately holds more than one training plan — sequential
/// non-overlapping ones, and plans from more than one professional — so the response carried the
/// set-level results of sessions belonging to somebody else's plan, together with that plan's
/// planned reps, weights and RPE values.
/// </summary>
/// <remarks>
/// These are integration tests against real Mongo rather than unit tests, and deliberately so: the
/// unit fixtures for this endpoint mock <c>IMongoCollection</c> with
/// <c>Arg.Any&lt;FilterDefinition&lt;T&gt;&gt;()</c> and return a fixed list, so the mock never
/// evaluates a filter. A unit test for a filter-scoping fix would therefore pass identically before
/// and after the change — it would assert nothing.
/// </remarks>
[Collection(TestCollection.Shard2)]
public class CrossPlanExecutionScopeTests(FitnessApiFactory factory)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static string UniqueEmail(string tag) => $"{Guid.NewGuid():N}@cross-plan-{tag}.com";

    // #1104: actors are built directly via TestActors, bypassing the /auth/register +
    // /auth/login HTTP round trip. Signatures/return shapes are unchanged.

    private async Task<(HttpClient Http, long ProfessionalProfileId, Guid ProfessionalUserId)> RegisterTrainerAsync(
        string tag)
    {
        var actor = await TestActors.Trainer(factory)
            .WithEmail(UniqueEmail(tag))
            .CreateAsync(TestContext.Current.CancellationToken);

        return (actor.Http, actor.ProfileId, actor.UserId);
    }

    private async Task<(long ClientProfileId, Guid ClientUserId)> RegisterClientAsync(string tag)
    {
        var actor = await TestActors.Client(factory)
            .WithEmail(UniqueEmail(tag))
            .CreateAsync(TestContext.Current.CancellationToken);

        return (actor.ProfileId, actor.UserId);
    }

    private async Task LinkAsync(long professionalProfileId, long clientProfileId) =>
        await TestActors.Link(factory, professionalProfileId, clientProfileId)
            .CreateAsync(TestContext.Current.CancellationToken);

    /// <summary>
    /// Seeds a plan with one published session and returns the plan and session ids.
    /// </summary>
    private async Task<(Guid PlanId, Guid SessionId)> SeedPlanAsync(Guid clientUserId, Guid trainerUserId)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        var planId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        await mongo.TrainingPlans.InsertOneAsync(new TrainingPlan
        {
            Id = ObjectId.GenerateNewId(),
            ExternalId = planId,
            ClientId = clientUserId,
            TrainerId = trainerUserId,
            Name = $"Plan {planId:N}",
            Status = TrainingPlanStatus.Active,
            StartDate = DateTime.UtcNow.Date,
            Weeks =
            [
                new TrainingWeek
                {
                    WeekNumber = 1,
                    Status = WeekStatus.Published,
                    Days = TrainingPlans.TrainingPlanTestHelpers.MaterializeDays(
                        (1, new TrainingSession
                        {
                            SessionId = sessionId,
                            Name = "Session",
                            Order = 1,
                            Workouts = []
                        }))
                }
            ],
            Version = 1,
            DateCreated = DateTime.UtcNow,
        }, cancellationToken: TestContext.Current.CancellationToken);

        return (planId, sessionId);
    }

    /// <summary>
    /// Seeds a performance-bearing execution against the given session, so it lands in BOTH the
    /// completions projection and the set-level performance projection.
    /// </summary>
    private async Task SeedExecutionAsync(Guid clientUserId, Guid planId, Guid sessionId)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        var now = DateTime.UtcNow;

        await mongo.SessionExecutions.InsertOneAsync(new SessionExecution
        {
            Id = ObjectId.GenerateNewId(),
            ExternalId = Guid.NewGuid(),
            ClientId = clientUserId,
            PlanId = planId,
            SessionId = sessionId,
            Date = SessionExecution.ToCompletionDateUtc(now),
            Status = SessionExecutionStatus.Completed,
            Performance = new SessionExecutionPerformance
            {
                StartedAt = now.AddMinutes(-30),
                CompletedAt = now,
                Workouts = []
            },
            DateCreated = now,
            Version = 1
        }, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GetTrainingPlan_DoesNotFoldInAnotherPlansExecutions()
    {
        var (trainerA, trainerAProfileId, trainerAUserId) = await RegisterTrainerAsync("incumbent");
        var (_, trainerBProfileId, trainerBUserId) = await RegisterTrainerAsync("other");
        var (clientProfileId, clientUserId) = await RegisterClientAsync("shared");

        await LinkAsync(trainerAProfileId, clientProfileId);
        await LinkAsync(trainerBProfileId, clientProfileId);

        var (planA, sessionA) = await SeedPlanAsync(clientUserId, trainerAUserId);
        var (planB, sessionB) = await SeedPlanAsync(clientUserId, trainerBUserId);

        // The client has worked through a session of each plan.
        await SeedExecutionAsync(clientUserId, planA, sessionA);
        await SeedExecutionAsync(clientUserId, planB, sessionB);

        var response = await trainerA.GetAsync($"/training/plans/{planA}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<PlanDetailResponse>(
            JsonOptions, TestContext.Current.CancellationToken);

        body!.SessionExecutions.Should().NotContain(
            e => e.SessionId == sessionB,
            "the set-level results of another professional's plan must not appear in this plan's detail");
        body.Completions.Should().NotContain(
            c => c.SessionId == sessionB,
            "nor may the completions projection emit the out-of-plan session");

        body.SessionExecutions.Should().Contain(
            e => e.SessionId == sessionA,
            "the plan's own execution must still be folded in — this is not 'return nothing'");
    }

    /// <summary>
    /// The same client, the same trainer, two sequential plans — which the plan-creation path
    /// explicitly permits. Scoping must be per plan, not per (client, trainer) pair.
    /// </summary>
    [Fact]
    public async Task GetTrainingPlan_DoesNotFoldInTheSameTrainersOtherPlan()
    {
        var (trainer, professionalProfileId, trainerUserId) = await RegisterTrainerAsync("sequential");
        var (clientProfileId, clientUserId) = await RegisterClientAsync("sequential");

        await LinkAsync(professionalProfileId, clientProfileId);

        var (currentPlan, currentSession) = await SeedPlanAsync(clientUserId, trainerUserId);
        var (previousPlan, previousSession) = await SeedPlanAsync(clientUserId, trainerUserId);

        await SeedExecutionAsync(clientUserId, currentPlan, currentSession);
        await SeedExecutionAsync(clientUserId, previousPlan, previousSession);

        var response = await trainer.GetAsync($"/training/plans/{currentPlan}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<PlanDetailResponse>(
            JsonOptions, TestContext.Current.CancellationToken);

        body!.SessionExecutions.Should().NotContain(
            e => e.SessionId == previousSession,
            "a previous plan's results belong to that plan's detail response, not this one's");
        body.SessionExecutions.Should().Contain(e => e.SessionId == currentSession);
    }

    // Minimal wire shapes — deserialising the production response types would drag their whole
    // dependency graph into the test for two fields.
    private sealed record PlanDetailResponse(
        List<ExecutionEntry> SessionExecutions,
        List<CompletionEntry> Completions);

    private sealed record ExecutionEntry(Guid SessionId);

    private sealed record CompletionEntry(Guid SessionId);
}
