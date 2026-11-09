using System.Net;
using System.Net.Http.Json;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Builders;
using FormUp.Tests.Endpoints.ClientTraining;
using FormUp.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;

namespace FormUp.Tests.Endpoints.TrainingPlanTemplates;

/// <summary>
/// Testcontainers integration tests for <c>POST /training/plan-templates/{templateId}/instantiate</c>
/// (#862) — the risk-centre endpoint of this issue: fresh <c>SessionId</c>/<c>WorkoutId</c>/
/// <c>ExerciseId</c> minting, the cloning ban on <see cref="TrainingSession.AllExercises"/>, the
/// coach-client-link 404 (never 403, carrying <c>TRAINING_PLAN_TEMPLATE_NOT_FOUND</c>), and
/// replication of the plan-creation start-date/overlap rules.
/// </summary>
[Collection(TestCollection.Name)]
public class InstantiateTemplateEndpointTests(FitnessApiFactory factory)
{
    private static string UniqueEmail(string tag) => $"{Guid.NewGuid():N}@instantiate-training-{tag}.com";

    private async Task<(HttpClient Client, Guid UserId)> RegisterTrainerAsync(string tag)
    {
        var client = factory.CreateClient();
        var email = UniqueEmail(tag);
        await TestHelpers.RegisterAsync(client, email, "TestPass1!", "Test", "Trainer", "Trainer");
        var (token, _) = await TestHelpers.LoginAsync(client, email, "TestPass1!");
        TestHelpers.SetBearerToken(client, token);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await db.Users.FirstAsync(u => u.Email == email, TestContext.Current.CancellationToken);

        return (client, user.Id);
    }

    private async Task<(Guid ClientPublicId, long ClientProfileId, Guid ClientUserId)> RegisterClientAsync(string tag)
    {
        var client = factory.CreateClient();
        var email = UniqueEmail(tag);
        await TestHelpers.RegisterAsync(client, email, "TestPass1!", "Test", "Client", "Client");
        await TestHelpers.LoginAsync(client, email, "TestPass1!");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await db.Users.FirstAsync(u => u.Email == email, TestContext.Current.CancellationToken);
        var profile = await db.ClientProfiles.FirstAsync(
            cp => cp.UserId == user.Id, TestContext.Current.CancellationToken);

        return (profile.PublicId, profile.Id, user.Id);
    }

    private async Task<long> GetProfessionalProfileIdAsync(Guid trainerUserId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var profile = await db.ProfessionalProfiles.FirstAsync(
            p => p.UserId == trainerUserId, TestContext.Current.CancellationToken);
        return profile.Id;
    }

    private Task LinkAsync(long trainerProfileId, long clientProfileId) =>
        LinkAsync(trainerProfileId, clientProfileId, canViewTrainingPlans: true, canViewNutritionPlans: true);

    private async Task LinkAsync(
        long trainerProfileId, long clientProfileId, bool canViewTrainingPlans, bool canViewNutritionPlans)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.ClientProfessionalLinks.Add(new ClientProfessionalLink
        {
            PublicId = Guid.NewGuid(),
            ProfessionalProfileId = trainerProfileId,
            ClientProfileId = clientProfileId,
            ProfessionalRole = UserRole.Trainer,
            ConnectionState = ConnectionState.Connected,
            CanViewTrainingPlans = canViewTrainingPlans,
            CanViewNutritionPlans = canViewNutritionPlans,
            DateCreated = DateTime.UtcNow
        });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task SeedTemplateAsync(TrainingPlanTemplate template)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        await mongo.TrainingPlanTemplates.InsertOneAsync(
            template, cancellationToken: TestContext.Current.CancellationToken);
    }

    private async Task<TrainingPlan> FetchPlanAsync(Guid externalId)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
        return await mongo.TrainingPlans
            .Find(p => p.ExternalId == externalId)
            .FirstAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Builds a template whose single session carries BOTH a workout-nested exercise and a
    /// standalone exercise — the exact shape needed to pin the cloning ban on
    /// <see cref="TrainingSession.AllExercises"/> (if a clone path ever read that computed
    /// property instead of <see cref="TrainingSession.StandaloneExercises"/>, the workout's
    /// exercise would be duplicated into the standalone list).
    /// </summary>
    private static TrainingPlanTemplate BuildTemplateWithSessionContent(
        Guid ownerId, int weekCount = 1, PrimaryGoal? goal = null)
    {
        var session = new TrainingSession
        {
            SessionId = Guid.NewGuid(),
            Name = "Push Day",
            Order = 1,
            // Non-Standard format at session level, with a populated WodConfig — pins that
            // instantiate copies formats and format configs verbatim (#862 review MINOR: no
            // prior test exercised a non-Standard format).
            Format = WorkoutFormat.EMOM,
            FormatConfig = new WodConfig { IntervalSeconds = 60, TotalRounds = 10 },
            Workouts =
            [
                new TrainingWorkout
                {
                    WorkoutId = Guid.NewGuid(),
                    Order = 0,
                    Name = "Main",
                    Exercises = [new SessionExercise { ExerciseId = Guid.NewGuid(), ExerciseExternalId = Guid.NewGuid(), ExerciseName = "Bench Press", Order = 1 }]
                }
            ],
            StandaloneExercises = [new SessionExercise { ExerciseId = Guid.NewGuid(), ExerciseExternalId = Guid.NewGuid(), ExerciseName = "Plank", Order = 2 }]
        };

        var weeks = Enumerable.Range(1, weekCount).Select(weekNumber => new TrainingTemplateWeek
        {
            WeekNumber = weekNumber,
            Days = Enumerable.Range(1, 7).Select(dayOfWeek => new TrainingDay
            {
                DayOfWeek = dayOfWeek,
                Sessions = dayOfWeek == 1 ? [session] : []
            }).ToList()
        }).ToList();

        return new TrainingPlanTemplateBuilder()
            .WithOwnerId(ownerId)
            .WithGoal(goal)
            .WithVisibility(LibraryVisibility.Private)
            .WithWeeks(weeks)
            .Build();
    }

    // ── the risk-centre AC: fresh id minting + the AllExercises cloning ban ──

    [Fact]
    public async Task Instantiate_ValidRequest_CreatesDraftPlanWithFreshIdsAndNoDuplicatedExercises()
    {
        var (trainer, trainerId) = await RegisterTrainerAsync("ids");
        var (clientPublicId, clientProfileId, clientUserId) = await RegisterClientAsync("ids");
        var professionalProfileId = await GetProfessionalProfileIdAsync(trainerId);
        await LinkAsync(professionalProfileId, clientProfileId);

        var template = BuildTemplateWithSessionContent(trainerId, weekCount: 2);
        await SeedTemplateAsync(template);

        var templateSessionIds = template.Weeks.SelectMany(w => w.Days).SelectMany(d => d.Sessions).Select(s => s.SessionId).ToHashSet();
        var templateWorkoutIds = template.Weeks.SelectMany(w => w.Days).SelectMany(d => d.Sessions).SelectMany(s => s.Workouts).Select(w => w.WorkoutId).ToHashSet();
        var templateExerciseIds = template.Weeks.SelectMany(w => w.Days).SelectMany(d => d.Sessions).SelectMany(s => s.AllExercises).Select(e => e.ExerciseId).ToHashSet();

        var response = await trainer.PostAsJsonAsync(
            $"/training/plan-templates/{template.ExternalId}/instantiate",
            new { ClientId = clientPublicId, Name = "Instantiated Plan" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<InstantiateResponseDto>(
            cancellationToken: TestContext.Current.CancellationToken);
        body.Should().NotBeNull();
        body!.Status.Should().Be("Draft");

        var plan = await FetchPlanAsync(body.PlanId);
        plan.ClientId.Should().Be(clientUserId);
        plan.Status.Should().Be(TrainingPlanStatus.Draft);
        plan.Weeks.Should().OnlyContain(w => w.Status == WeekStatus.Draft);

        var planSessions = plan.Weeks.SelectMany(w => w.Days).SelectMany(d => d.Sessions).ToList();
        var planSessionIds = planSessions.Select(s => s.SessionId).ToList();
        planSessionIds.Should().NotBeEmpty();
        planSessionIds.Should().OnlyContain(id => !templateSessionIds.Contains(id),
            "no SessionId in the instantiated plan may appear anywhere in the source template");

        var planWorkoutIds = planSessions.SelectMany(s => s.Workouts).Select(w => w.WorkoutId).ToList();
        planWorkoutIds.Should().OnlyContain(id => !templateWorkoutIds.Contains(id),
            "no WorkoutId in the instantiated plan may appear anywhere in the source template");

        var planExerciseIds = planSessions.SelectMany(s => s.AllExercises).Select(e => e.ExerciseId).ToList();
        planExerciseIds.Should().OnlyContain(id => !templateExerciseIds.Contains(id),
            "no ExerciseId in the instantiated plan may appear anywhere in the source template");

        // The cloning ban: each cloned session's StandaloneExercises count must match the
        // source's exactly (1) — never inflated by the workout's nested exercise via the
        // computed AllExercises view.
        planSessions.Should().OnlyContain(s => s.StandaloneExercises.Count == 1,
            "cloning must copy StandaloneExercises only, never the computed AllExercises view");
        planSessions.Should().OnlyContain(s => s.Workouts.Count == 1 && s.Workouts[0].Exercises.Count == 1,
            "the workout's own nested exercise must be cloned once, independently of the standalone list");

        // The non-Standard format and its WodConfig copy through verbatim alongside the fresh
        // ids asserted above.
        planSessions.Should().OnlyContain(s => s.Format == WorkoutFormat.EMOM,
            "session Format copies through from the template");
        planSessions.Should().OnlyContain(s =>
            s.FormatConfig != null && s.FormatConfig.IntervalSeconds == 60 && s.FormatConfig.TotalRounds == 10,
            "session FormatConfig copies through from the template verbatim");
    }

    [Fact]
    public async Task Instantiate_SameTemplateForDifferentClients_ProducesIndependentPlansWithDisjointIds()
    {
        var (trainer, trainerId) = await RegisterTrainerAsync("independent");
        var (clientAPublicId, clientAProfileId, _) = await RegisterClientAsync("independent-a");
        var (clientBPublicId, clientBProfileId, _) = await RegisterClientAsync("independent-b");
        var professionalProfileId = await GetProfessionalProfileIdAsync(trainerId);
        await LinkAsync(professionalProfileId, clientAProfileId);
        await LinkAsync(professionalProfileId, clientBProfileId);

        var template = BuildTemplateWithSessionContent(trainerId);
        await SeedTemplateAsync(template);

        var responseA = await trainer.PostAsJsonAsync(
            $"/training/plan-templates/{template.ExternalId}/instantiate",
            new { ClientId = clientAPublicId, Name = "Plan A" });
        var responseB = await trainer.PostAsJsonAsync(
            $"/training/plan-templates/{template.ExternalId}/instantiate",
            new { ClientId = clientBPublicId, Name = "Plan B" });

        responseA.StatusCode.Should().Be(HttpStatusCode.Created);
        responseB.StatusCode.Should().Be(HttpStatusCode.Created);

        var bodyA = await responseA.Content.ReadFromJsonAsync<InstantiateResponseDto>(
            cancellationToken: TestContext.Current.CancellationToken);
        var bodyB = await responseB.Content.ReadFromJsonAsync<InstantiateResponseDto>(
            cancellationToken: TestContext.Current.CancellationToken);

        var planA = await FetchPlanAsync(bodyA!.PlanId);
        var planB = await FetchPlanAsync(bodyB!.PlanId);

        var sessionsA = planA.Weeks.SelectMany(w => w.Days).SelectMany(d => d.Sessions).ToList();
        var sessionsB = planB.Weeks.SelectMany(w => w.Days).SelectMany(d => d.Sessions).ToList();

        // Completion state keys on SessionId/WorkoutId/ExerciseId (#857 rekeyed exercise
        // completion onto the per-instance SessionExercise.ExerciseId) — a shared id in any of
        // these three families would let a checkbox toggle on one client's plan silently flip
        // the equivalent checkbox on another client's plan. Assert full-set disjointness for all
        // three, not a single spot-checked id or a bare count comparison.
        var sessionIdsA = sessionsA.Select(s => s.SessionId).ToHashSet();
        var sessionIdsB = sessionsB.Select(s => s.SessionId).ToHashSet();
        sessionIdsA.Should().NotIntersectWith(sessionIdsB, "two independent instantiations must never share a SessionId");

        var workoutIdsA = sessionsA.SelectMany(s => s.Workouts).Select(w => w.WorkoutId).ToHashSet();
        var workoutIdsB = sessionsB.SelectMany(s => s.Workouts).Select(w => w.WorkoutId).ToHashSet();
        workoutIdsA.Should().NotIntersectWith(workoutIdsB, "two independent instantiations must never share a WorkoutId");

        var exerciseIdsA = sessionsA.SelectMany(s => s.AllExercises).Select(e => e.ExerciseId).ToHashSet();
        var exerciseIdsB = sessionsB.SelectMany(s => s.AllExercises).Select(e => e.ExerciseId).ToHashSet();
        exerciseIdsA.Should().NotIntersectWith(exerciseIdsB,
            "two independent instantiations must never share an ExerciseId — exercise completion (#857) keys on this id, so a collision would bleed completion state across clients");
    }

    /// <summary>
    /// Completes the disjoint-ids proof above with a live-write check: recording a completion
    /// against an exercise instance in client A's instantiated plan must create no
    /// <see cref="SessionExecution"/> record — and therefore no completion state at all — for
    /// client B's independently instantiated copy of the same template.
    /// </summary>
    [Fact]
    public async Task Instantiate_SameTemplateForDifferentClients_CompletingExerciseInFirstPlanLeavesSecondPlanUntouched()
    {
        var (trainer, trainerId) = await RegisterTrainerAsync("isolation");
        var (clientAPublicId, clientAProfileId, clientAUserId) = await RegisterClientAsync("isolation-a");
        var (clientBPublicId, clientBProfileId, clientBUserId) = await RegisterClientAsync("isolation-b");
        var professionalProfileId = await GetProfessionalProfileIdAsync(trainerId);
        await LinkAsync(professionalProfileId, clientAProfileId);
        await LinkAsync(professionalProfileId, clientBProfileId);

        var template = BuildTemplateWithSessionContent(trainerId);
        await SeedTemplateAsync(template);

        var responseA = await trainer.PostAsJsonAsync(
            $"/training/plan-templates/{template.ExternalId}/instantiate",
            new { ClientId = clientAPublicId, Name = "Plan A" });
        var responseB = await trainer.PostAsJsonAsync(
            $"/training/plan-templates/{template.ExternalId}/instantiate",
            new { ClientId = clientBPublicId, Name = "Plan B" });

        var bodyA = await responseA.Content.ReadFromJsonAsync<InstantiateResponseDto>(
            cancellationToken: TestContext.Current.CancellationToken);
        var bodyB = await responseB.Content.ReadFromJsonAsync<InstantiateResponseDto>(
            cancellationToken: TestContext.Current.CancellationToken);

        var planA = await FetchPlanAsync(bodyA!.PlanId);
        var planB = await FetchPlanAsync(bodyB!.PlanId);
        planB.Should().NotBeNull();

        var sessionA = planA.Weeks.SelectMany(w => w.Days).SelectMany(d => d.Sessions).First();
        var exerciseInstanceA = sessionA.AllExercises.First();

        var completion = TrainingCompletionTestHelpers.CreateCompletion(
            clientId: clientAUserId,
            sessionId: sessionA.SessionId,
            date: DateTime.UtcNow.Date,
            completedExerciseIds: [exerciseInstanceA.ExerciseId]);

        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
            await mongo.SessionExecutions.InsertOneAsync(
                completion, cancellationToken: TestContext.Current.CancellationToken);
        }

        using var verifyScope = factory.Services.CreateScope();
        var verifyMongo = verifyScope.ServiceProvider.GetRequiredService<IMongoContext>();
        var clientBHasAnyExecution = await verifyMongo.SessionExecutions
            .Find(x => x.ClientId == clientBUserId)
            .AnyAsync(TestContext.Current.CancellationToken);

        clientBHasAnyExecution.Should().BeFalse(
            "completing an exercise in client A's instantiated plan must never create or affect a SessionExecution for client B's independent copy of the same template");
    }

    // ── coach-client link ─────────────────────────────────────────────────────

    [Fact]
    public async Task Instantiate_UnlinkedClient_Returns404WithTemplateNotFoundCode()
    {
        var (trainer, trainerId) = await RegisterTrainerAsync("unlinked");
        var (clientPublicId, _, _) = await RegisterClientAsync("unlinked");
        // Deliberately no link created.

        var template = BuildTemplateWithSessionContent(trainerId);
        await SeedTemplateAsync(template);

        var response = await trainer.PostAsJsonAsync(
            $"/training/plan-templates/{template.ExternalId}/instantiate",
            new { ClientId = clientPublicId, Name = "Plan" });

        response.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "an unlinked client must 404, never 403 — a 403 would confirm the client exists to an unlinked coach");
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.Should().Contain("TRAINING_PLAN_TEMPLATE_NOT_FOUND",
            "the unlinked-client 404 routes through the shared library helper, carrying the same code as a missing template");
    }

    /// <summary>
    /// Mirror-site regression guard: this is a training route and must require
    /// <c>CanViewTrainingPlans</c> specifically. An active link that grants only the nutrition
    /// domain must still be denied — if the guard were ever widened to a bare "link exists"
    /// check, this test would regress to 201.
    /// </summary>
    [Fact]
    public async Task Instantiate_LinkGrantsOnlyNutrition_Returns404()
    {
        var (trainer, trainerId) = await RegisterTrainerAsync("nutrition-only");
        var (clientPublicId, clientProfileId, _) = await RegisterClientAsync("nutrition-only");
        var professionalProfileId = await GetProfessionalProfileIdAsync(trainerId);
        await LinkAsync(professionalProfileId, clientProfileId, canViewTrainingPlans: false, canViewNutritionPlans: true);

        var template = BuildTemplateWithSessionContent(trainerId);
        await SeedTemplateAsync(template);

        var response = await trainer.PostAsJsonAsync(
            $"/training/plan-templates/{template.ExternalId}/instantiate",
            new { ClientId = clientPublicId, Name = "Plan" });

        response.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "a link that grants only the nutrition domain must not admit a training-plan instantiation");
    }

    // ── template guard: read-guarded, not write-guarded ──────────────────────

    [Fact]
    public async Task Instantiate_OtherOwnersPublicTemplate_Returns201()
    {
        var otherOwnerId = Guid.NewGuid();
        var (trainer, trainerId) = await RegisterTrainerAsync("public-instantiate");
        var (clientPublicId, clientProfileId, _) = await RegisterClientAsync("public-instantiate");
        var professionalProfileId = await GetProfessionalProfileIdAsync(trainerId);
        await LinkAsync(professionalProfileId, clientProfileId);

        var template = BuildTemplateWithSessionContent(otherOwnerId);
        template.Visibility = LibraryVisibility.Public;
        await SeedTemplateAsync(template);

        var response = await trainer.PostAsJsonAsync(
            $"/training/plan-templates/{template.ExternalId}/instantiate",
            new { ClientId = clientPublicId, Name = "Plan From Public Template" });

        response.StatusCode.Should().Be(
            HttpStatusCode.Created,
            "instantiate is read-guarded — another owner's Public template must stay instantiable");
    }

    [Fact]
    public async Task Instantiate_OtherOwnersPrivateTemplate_Returns404()
    {
        var otherOwnerId = Guid.NewGuid();
        var (trainer, trainerId) = await RegisterTrainerAsync("private-instantiate");
        var (clientPublicId, clientProfileId, _) = await RegisterClientAsync("private-instantiate");
        var professionalProfileId = await GetProfessionalProfileIdAsync(trainerId);
        await LinkAsync(professionalProfileId, clientProfileId);

        var template = BuildTemplateWithSessionContent(otherOwnerId);
        await SeedTemplateAsync(template);

        var response = await trainer.PostAsJsonAsync(
            $"/training/plan-templates/{template.ExternalId}/instantiate",
            new { ClientId = clientPublicId, Name = "Plan" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── overlap + field mapping ───────────────────────────────────────────────

    /// <summary>
    /// The start date must be the next Monday, not <c>DateTime.UtcNow.Date</c> — see the
    /// nutrition-side sibling's identical remark for why a date-dependent test that goes green
    /// once a week is worse than one that fails consistently.
    /// </summary>
    [Fact]
    public async Task Instantiate_OverlappingWindow_Returns409PlanOverlap()
    {
        var today = DateTime.UtcNow.Date;
        var daysUntilMonday = ((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7;
        var nextMonday = today.AddDays(daysUntilMonday == 0 ? 7 : daysUntilMonday);

        var (trainer, trainerId) = await RegisterTrainerAsync("overlap");
        var (clientPublicId, clientProfileId, clientUserId) = await RegisterClientAsync("overlap");
        var professionalProfileId = await GetProfessionalProfileIdAsync(trainerId);
        await LinkAsync(professionalProfileId, clientProfileId);

        var template = BuildTemplateWithSessionContent(trainerId, weekCount: 4);
        await SeedTemplateAsync(template);

        using (var scope = factory.Services.CreateScope())
        {
            var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();
            await mongo.TrainingPlans.InsertOneAsync(new TrainingPlan
            {
                ExternalId = Guid.NewGuid(),
                ClientId = clientUserId,
                TrainerId = trainerId,
                Name = "Existing Plan",
                Status = TrainingPlanStatus.Active,
                StartDate = nextMonday,
                Version = 1,
                DateCreated = DateTime.UtcNow,
                Weeks = Enumerable.Range(1, 4).Select(w => new TrainingWeek { WeekNumber = w }).ToList()
            }, cancellationToken: TestContext.Current.CancellationToken);
        }

        // Same start date and week count as the seeded plan, so the windows overlap exactly —
        // the overlap is the property under test, not an incidental near-miss.
        var response = await trainer.PostAsJsonAsync(
            $"/training/plan-templates/{template.ExternalId}/instantiate",
            new { ClientId = clientPublicId, Name = "Overlapping Plan", StartDate = nextMonday });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var responseBody = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        responseBody.Should().Contain("PLAN_OVERLAP");
    }

    [Fact]
    public async Task Instantiate_FieldMapping_CopiesGoal()
    {
        var (trainer, trainerId) = await RegisterTrainerAsync("field-mapping");
        var (clientPublicId, clientProfileId, _) = await RegisterClientAsync("field-mapping");
        var professionalProfileId = await GetProfessionalProfileIdAsync(trainerId);
        await LinkAsync(professionalProfileId, clientProfileId);

        var template = BuildTemplateWithSessionContent(trainerId, goal: PrimaryGoal.GainMuscle);
        await SeedTemplateAsync(template);

        var response = await trainer.PostAsJsonAsync(
            $"/training/plan-templates/{template.ExternalId}/instantiate",
            new { ClientId = clientPublicId, Name = "Mapped Plan" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<InstantiateResponseDto>(
            cancellationToken: TestContext.Current.CancellationToken);

        var plan = await FetchPlanAsync(body!.PlanId);
        plan.Goal.Should().Be(PrimaryGoal.GainMuscle, "Goal copies through from the template");
        plan.TargetWeightKg.Should().BeNull("TargetWeightKg is client-only and not set by instantiate");
    }

    private sealed class InstantiateResponseDto
    {
        public Guid PlanId { get; set; }
        public string Status { get; set; } = string.Empty;
    }
}
