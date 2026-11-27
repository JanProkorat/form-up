using System.Text.Json;
using System.Text.Json.Serialization;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;

namespace FormUp.Tests.Endpoints.Automations;

/// <summary>
/// Seeding and JSON helpers shared by the automation integration tests.
/// </summary>
internal static class AutomationTestData
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>A valid global schedule rule body; tests add or replace entries.</summary>
    public static Dictionary<string, object?> ScheduleRule(string name = "Progress photos") => new()
    {
        ["name"] = name,
        ["actionType"] = "RequestProgressPhotos",
        ["triggerKind"] = "Schedule",
        ["scheduleInterval"] = 4,
        ["scheduleUnit"] = "Weeks",
        ["localTime"] = "09:00:00",
    };

    /// <summary>Creates a tag owned by the coach and returns its public and internal ids.</summary>
    public static async Task<(Guid PublicId, long Id)> CreateTagAsync(FitnessApiFactory factory, Actor coach, string name = "VIP")
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var tag = new ClientTag
        {
            PublicId = Guid.NewGuid(),
            OwnerProfessionalProfileId = coach.ProfileId,
            Name = name,
            ColorHex = "#3b82f6",
        };
        db.ClientTags.Add(tag);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return (tag.PublicId, tag.Id);
    }

    /// <summary>Assigns a tag to a link.</summary>
    public static async Task AssignTagAsync(FitnessApiFactory factory, long tagId, long linkId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        db.ClientTagAssignments.Add(new ClientTagAssignment { ClientTagId = tagId, ClientProfessionalLinkId = linkId });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Creates a questionnaire owned by the coach and returns its public id.</summary>
    public static async Task<Guid> CreateQuestionnaireAsync(
        FitnessApiFactory factory,
        Actor coach,
        QuestionnaireType type = QuestionnaireType.Onboarding,
        bool isActive = true)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var questionnaire = new Questionnaire
        {
            PublicId = Guid.NewGuid(),
            ProfessionalId = coach.UserId,
            Title = "Plan review",
            Type = type,
            IsActive = isActive,
        };
        db.Questionnaires.Add(questionnaire);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return questionnaire.PublicId;
    }

    /// <summary>Inserts a training plan into the real Mongo container.</summary>
    public static async Task<Guid> SeedTrainingPlanAsync(
        FitnessApiFactory factory,
        Guid clientUserId,
        Guid authorUserId,
        DateTime? startDate,
        int weeks,
        TrainingPlanStatus status = TrainingPlanStatus.Active)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        var planId = Guid.NewGuid();

        await mongo.TrainingPlans.InsertOneAsync(new TrainingPlan
        {
            Id = ObjectId.GenerateNewId(),
            ExternalId = planId,
            ClientId = clientUserId,
            TrainerId = authorUserId,
            Name = "Automation training plan",
            Status = status,
            StartDate = startDate,
            Weeks = Enumerable.Range(1, weeks).Select(n => new TrainingWeek { WeekNumber = n }).ToList(),
            Version = 1,
            DateCreated = DateTime.UtcNow,
        }, cancellationToken: TestContext.Current.CancellationToken);

        return planId;
    }

    /// <summary>Inserts a nutrition plan into the real Mongo container.</summary>
    public static async Task SeedNutritionPlanAsync(
        FitnessApiFactory factory,
        Guid clientUserId,
        Guid authorUserId,
        DateTime? startDate,
        int weeks)
    {
        using var scope = factory.Services.CreateScope();
        var mongo = scope.ServiceProvider.GetRequiredService<IMongoContext>();

        await mongo.NutritionPlans.InsertOneAsync(new NutritionPlan
        {
            Id = ObjectId.GenerateNewId(),
            ExternalId = Guid.NewGuid(),
            ClientId = clientUserId,
            NutritionistId = authorUserId,
            Name = "Automation nutrition plan",
            Status = NutritionPlanStatus.Active,
            StartDate = startDate,
            Weeks = Enumerable.Range(1, weeks).Select(n => new PlanWeek { WeekNumber = n }).ToList(),
            Version = 1,
            DateCreated = DateTime.UtcNow,
        }, cancellationToken: TestContext.Current.CancellationToken);
    }

    /// <summary>Writes a run row for a rule and client.</summary>
    public static async Task AddRunAsync(
        FitnessApiFactory factory,
        Guid rulePublicId,
        long clientProfileId,
        AutomationRunStatus status,
        AutomationSkipReason? skipReason = null,
        DateTime? sentAt = null,
        DateTime? answeredAt = null,
        int attemptCount = 0,
        DateTime? nextRetryAt = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var ruleId = await db.AutomationRules
            .Where(r => r.PublicId == rulePublicId)
            .Select(r => r.Id)
            .FirstAsync(TestContext.Current.CancellationToken);

        db.AutomationRuns.Add(new AutomationRun
        {
            AutomationRuleId = ruleId,
            ClientProfileId = clientProfileId,
            Occurrence = Guid.NewGuid().ToString("N"),
            Status = status,
            SkipReason = skipReason,
            SentAt = sentAt,
            AnsweredAt = answeredAt,
            AttemptCount = attemptCount,
            NextRetryAt = nextRetryAt,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Inserts a rule owned by the coach: a daily 09:00 measurement request, no options. Tests
    /// change what they exercise through <paramref name="configure"/>.
    /// </summary>
    public static async Task<AutomationRule> InsertRuleAsync(
        FitnessApiFactory factory, Actor coach, Action<AutomationRule>? configure = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var rule = new AutomationRule
        {
            OwnerProfessionalProfileId = coach.ProfileId,
            Name = $"Rule {Guid.NewGuid():N}",
            ActionType = AutomationActionType.RequestMeasurements,
            TriggerKind = AutomationTriggerKind.Schedule,
            ScheduleInterval = 1,
            ScheduleUnit = AutomationScheduleUnit.Days,
            LocalTime = new TimeOnly(9, 0),
            SkipWhilePreviousUnanswered = false,
        };
        configure?.Invoke(rule);

        db.AutomationRules.Add(rule);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return rule;
    }

    /// <summary>Inserts a questionnaire owned by the coach.</summary>
    public static async Task<Questionnaire> InsertQuestionnaireAsync(
        FitnessApiFactory factory,
        Actor coach,
        QuestionnaireType type = QuestionnaireType.Onboarding,
        bool isActive = true,
        Guid? ownerUserId = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var questionnaire = new Questionnaire
        {
            ProfessionalId = ownerUserId ?? coach.UserId,
            Title = "Weekly review",
            Type = type,
            IsActive = isActive,
        };
        db.Questionnaires.Add(questionnaire);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return questionnaire;
    }

    /// <summary>Moves a rule's creation time, which the context stamps with the real clock on insert.</summary>
    public static async Task SetRuleCreatedAsync(FitnessApiFactory factory, long ruleId, DateTime createdAt)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        await db.AutomationRules
            .Where(r => r.Id == ruleId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.DateCreated, createdAt), TestContext.Current.CancellationToken);
    }

    /// <summary>Moves a link's creation time, which the context stamps with the real clock on insert.</summary>
    public static async Task SetLinkCreatedAsync(FitnessApiFactory factory, long linkId, DateTime createdAt)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        await db.ClientProfessionalLinks
            .Where(l => l.Id == linkId)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.DateCreated, createdAt), TestContext.Current.CancellationToken);
    }

    /// <summary>Reads the creation time of a link.</summary>
    public static async Task<DateTime> GetLinkCreatedAsync(FitnessApiFactory factory, long linkId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.ClientProfessionalLinks
            .Where(l => l.Id == linkId)
            .Select(l => l.DateCreated)
            .FirstAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Logs a body measurement for the client, created at the given instant.</summary>
    public static async Task AddMeasurementAsync(FitnessApiFactory factory, long clientProfileId, DateTime createdAt)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var measurement = new BodyMeasurement
        {
            ClientProfileId = clientProfileId,
            MeasuredAt = createdAt,
            WeightKg = 80m,
        };
        db.BodyMeasurements.Add(measurement);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await db.BodyMeasurements
            .Where(m => m.Id == measurement.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.DateCreated, createdAt), TestContext.Current.CancellationToken);
    }

    /// <summary>Stores a client's response to a questionnaire.</summary>
    public static async Task<QuestionnaireResponse> AddResponseAsync(
        FitnessApiFactory factory,
        Questionnaire questionnaire,
        Actor coach,
        Actor client,
        long linkId,
        QuestionnaireResponseStatus status,
        DateTime? submittedAt)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var response = new QuestionnaireResponse
        {
            QuestionnaireId = questionnaire.Id,
            ClientId = client.UserId,
            ProfessionalId = coach.UserId,
            LinkId = linkId,
            Status = status,
            SubmittedAt = submittedAt,
        };
        db.QuestionnaireResponses.Add(response);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return response;
    }
}
