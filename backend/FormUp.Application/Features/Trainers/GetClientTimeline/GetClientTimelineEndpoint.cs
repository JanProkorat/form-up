using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using Microsoft.EntityFrameworkCore;
using MongoDB.Driver;

namespace FormUp.Application.Features.Trainers.GetClientTimeline;

/// <summary>
/// Returns a merged, chronological activity timeline for a single client,
/// composed on-read from existing sources (meal logs, workout logs, body
/// measurements, questionnaire responses, plan publish events, linking).
/// The requesting trainer must have an active link to the client.
/// </summary>
/// <param name="db">Relational data source.</param>
/// <param name="mongo">Document data source.</param>
/// <param name="audit">Audit logging service.</param>
/// <param name="coachDiscipline">Narrows the link flags to the disciplines the caller holds a role for (active or removed but not yet swept).</param>
public class GetClientTimelineEndpoint(
    IApplicationDbContext db,
    IMongoContext mongo,
    IAuditService audit,
    ICoachDisciplineAccess coachDiscipline)
    : Endpoint<GetClientTimelineRequest, GetClientTimelineResponse>
{
    /// <summary>
    /// Projection for locating a nutrition plan's plan-publish event: externalId, name, and
    /// per-week status/datePublished only — excludes the heavy <c>weeks[].days</c> sub-tree
    /// entirely. Same idiom as
    /// <see cref="Application.Features.ClientNutrition.GetTodayPlan.GetTodayPlanEndpoint.LightPlanProjection"/>.
    /// </summary>
    internal static readonly ProjectionDefinition<NutritionPlan> NutritionPlanPublishProjection =
        Builders<NutritionPlan>.Projection.Combine(
            Builders<NutritionPlan>.Projection.Include(p => p.ExternalId),
            Builders<NutritionPlan>.Projection.Include(p => p.Name),
            Builders<NutritionPlan>.Projection.Include("weeks.weekNumber"),
            Builders<NutritionPlan>.Projection.Include("weeks.status"),
            Builders<NutritionPlan>.Projection.Include("weeks.datePublished"));

    /// <summary>
    /// Projection for counting planned meals per day: start date, week status and per-day
    /// meal ids only.
    /// </summary>
    private static readonly ProjectionDefinition<NutritionPlan> NutritionPlannedMealsProjection =
        Builders<NutritionPlan>.Projection.Combine(
            Builders<NutritionPlan>.Projection.Include(p => p.ExternalId),
            Builders<NutritionPlan>.Projection.Include(p => p.StartDate),
            Builders<NutritionPlan>.Projection.Include("weeks.weekNumber"),
            Builders<NutritionPlan>.Projection.Include("weeks.status"),
            Builders<NutritionPlan>.Projection.Include("weeks.days.dayOfWeek"),
            Builders<NutritionPlan>.Projection.Include("weeks.days.meals.mealId"),
            Builders<NutritionPlan>.Projection.Include("weeks.days.meals.foods.itemId"),
            Builders<NutritionPlan>.Projection.Include("weeks.days.meals.recipes.itemId"));

    /// <summary>
    /// Projection for resolving session names: plan id plus each session's id and name.
    /// </summary>
    private static readonly ProjectionDefinition<TrainingPlan> TrainingSessionNamesProjection =
        Builders<TrainingPlan>.Projection.Combine(
            Builders<TrainingPlan>.Projection.Include(p => p.ExternalId),
            Builders<TrainingPlan>.Projection.Include("weeks.days.sessions.sessionId"),
            Builders<TrainingPlan>.Projection.Include("weeks.days.sessions.name"));

    /// <summary>
    /// Same idiom as <see cref="NutritionPlanPublishProjection"/>, for training plans.
    /// </summary>
    internal static readonly ProjectionDefinition<TrainingPlan> TrainingPlanPublishProjection =
        Builders<TrainingPlan>.Projection.Combine(
            Builders<TrainingPlan>.Projection.Include(p => p.ExternalId),
            Builders<TrainingPlan>.Projection.Include(p => p.Name),
            Builders<TrainingPlan>.Projection.Include("weeks.weekNumber"),
            Builders<TrainingPlan>.Projection.Include("weeks.status"),
            Builders<TrainingPlan>.Projection.Include("weeks.datePublished"));

    /// <inheritdoc />
    public override void Configure()
    {
        Get("/trainer/clients/{ClientId}/timeline");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Get client activity timeline";
            s.Description = "Returns a merged timeline of recent client activity (meals, workouts, measurements, plan events) for a specific client managed by the authenticated trainer.";
            s.Responses[StatusCodes.Status200OK] = "Timeline items, newest first";
            s.Responses[StatusCodes.Status401Unauthorized] = "Missing or unreadable caller claim";
            s.Responses[StatusCodes.Status403Forbidden] = "The link grants no nutrition or training access to the caller";
            s.Responses[StatusCodes.Status404NotFound] = "Client not found, or not linked to the caller";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(GetClientTimelineRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);

        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var trainerUserId = Guid.Parse(userId);

        // Locate the trainer profile
        var professionalProfile = await db.ProfessionalProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(tp => tp.UserId == trainerUserId, ct);

        if (professionalProfile is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // Locate the client profile (req.ClientId is the ClientProfile.PublicId)
        var clientProfile = await db.ClientProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(cp => cp.PublicId == req.ClientId, ct);

        if (clientProfile is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // Verify active trainer-client link
        var link = await db.ClientProfessionalLinks
            .AsNoTracking()
            .FirstOrDefaultAsync(ctl =>
                ctl.ProfessionalProfileId == professionalProfile.Id &&
                ctl.ClientProfileId == clientProfile.Id &&
                ctl.ConnectionState != ConnectionState.Ended, ct);

        if (link is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // A discipline is visible only when the link grants it AND the caller still holds that coach
        // role actively. Neither visible: deny outright (matches the LinkCapabilities.GrantsNothing
        // deny semantics from #903).
        var effective = await coachDiscipline.GetEffectiveAsync(
            trainerUserId, LinkCapabilities.FromLink(link), ct);

        if (effective.GrantsNothing)
        {
            await Send.ForbiddenAsync(ct);
            return;
        }

        // Every Mongo document's clientId (MealLog, NutritionPlan, TrainingPlan,
        // WorkoutLog, PersonalRecord) is keyed on ApplicationUser.Id (#840).
        // QuestionnaireResponse.ClientId is an EF entity keyed on UserId too.
        var clientUserId = clientProfile.UserId;

        // Look back up to 90 days; we'll take the top `Limit` overall.
        var from = DateTime.UtcNow.Date.AddDays(-90);

        var items = new List<ClientTimelineItem>();

        // ── 1. Meal logs — aggregate per day to avoid dozens of rows ──
        // Nutrition-domain: gated on CanViewNutritionPlans.
        if (effective.CanViewNutritionPlans)
        {
            // Rows are selected by LogDate (EatenAt for legacy rows): a partly eaten row has no EatenAt.
            var lastDay = DateTime.UtcNow.Date.AddDays(1);
            var mealFilter = MealLogDayFilter.BuildClientRange(
                clientUserId, null, from, lastDay, from, lastDay.AddDays(1));

            using var cursor = await mongo.MealLogs.FindAsync(mealFilter, cancellationToken: ct);
            var logs = (await cursor.ToListAsync(ct)).Where(l => MealLogMerger.DayOf(l) >= from).ToList();

            // Planned-meal counts come only from plans the caller authored; any other plan
            // referenced by a log yields a null count.
            var authoredPlans = await LoadAuthoredNutritionPlansAsync(
                logs.Select(l => l.PlanId).Distinct().ToList(), clientUserId, trainerUserId, ct);

            var perDay = MealLogMerger
                .Merge(logs, (planId, day, mealId) => authoredPlans.TryGetValue(planId, out var plan)
                    ? NutritionPlanDayResolver.FindMeal(plan, day, mealId)
                    : null)
                .Where(meal => meal.State != MealEatenState.NotEaten)
                .GroupBy(meal => meal.DayUtc)
                .OrderByDescending(g => g.Key)
                .ToList();

            foreach (var day in perDay)
            {
                var authoredPlanIds = day.Select(l => l.PlanId).Distinct()
                    .Where(authoredPlans.ContainsKey)
                    .ToList();
                var mealsPlanned = authoredPlanIds
                    .Sum(planId => PlannedMealCounter.GetPlannedMealCountForDate(authoredPlans[planId], day.Key));
                var mealsEaten = day.Where(meal => meal.State == MealEatenState.Eaten).Select(meal => meal.MealId).Distinct().Count();
                var mealsPartlyEaten = day.Where(meal => meal.State == MealEatenState.PartlyEaten).Select(meal => meal.MealId).Distinct().Count();

                items.Add(new ClientTimelineItem
                {
                    Id = $"meals:{day.Key:yyyy-MM-dd}",
                    Type = "meal_day",
                    OccurredAt = day.SelectMany(meal => meal.Rows).Max(l => l.EatenAt ?? l.LogDate),
                    Title = $"Zaznamenáno {day.Sum(meal => meal.Rows.Count)} jídel",
                    Icon = "🍽",
                    MealDay = new TimelineMealDayPayload
                    {
                        MealsLogged = mealsEaten,
                        MealsPartlyEaten = mealsPartlyEaten,
                        MealsPlanned = mealsPlanned > 0 ? mealsPlanned : null,
                    },
                });
            }
        }

        // ── 2. Workout logs (completed) ──
        // #841: scoped to executions that carry Performance data (a live-training-assistant
        // log) — checkbox-only completions never appeared in the old WorkoutLogs collection.
        // Training-domain: gated on CanViewTrainingPlans.
        if (effective.CanViewTrainingPlans)
        {
            var workoutFilter = Builders<SessionExecution>.Filter.Eq(l => l.ClientId, clientUserId)
                & Builders<SessionExecution>.Filter.Exists(l => l.Performance)
                & Builders<SessionExecution>.Filter.Gte(l => l.Performance!.StartedAt, from)
                & Builders<SessionExecution>.Filter.Eq(l => l.Status, SessionExecutionStatus.Completed);

            using var cursor = await mongo.SessionExecutions.FindAsync(workoutFilter, cancellationToken: ct);
            var logs = await cursor.ToListAsync(ct);

            // Session names come only from training plans the caller authored.
            var sessionNames = await LoadAuthoredSessionNamesAsync(
                logs.Where(l => l.PlanId.HasValue).Select(l => l.PlanId!.Value).Distinct().ToList(),
                clientUserId, trainerUserId, ct);

            foreach (var log in logs)
            {
                string? sessionName = null;

                if (log.PlanId.HasValue && log.SessionId.HasValue)
                {
                    sessionNames.TryGetValue((log.PlanId.Value, log.SessionId.Value), out sessionName);
                }

                items.Add(new ClientTimelineItem
                {
                    Id = $"workout:{log.ExternalId}",
                    Type = "workout",
                    OccurredAt = log.Performance!.CompletedAt ?? log.Performance.StartedAt,
                    Title = "Dokončil trénink",
                    Description = log.Exercises.Count > 0
                        ? $"{log.Exercises.Count} cviků"
                        : null,
                    Icon = "🏋",
                    Workout = new TimelineWorkoutPayload
                    {
                        SessionExecutionId = log.ExternalId,
                        SessionName = sessionName,
                        ExerciseCount = log.Exercises.Count,
                    },
                });
            }
        }

        // ── 3. Body measurements — dual-readable standalone entries (not attached to a
        //      nutrition or training item), so they stay visible to any caller holding
        //      at least one capability flag. See #916 classification rule. ──
        var measurements = await db.BodyMeasurements
            .AsNoTracking()
            .Where(bm => bm.ClientProfileId == clientProfile.Id && bm.MeasuredAt >= from)
            .OrderByDescending(bm => bm.MeasuredAt)
            .Select(bm => new { bm.PublicId, bm.MeasuredAt, bm.WeightKg, bm.BodyFatPercentage })
            .ToListAsync(ct);

        foreach (var m in measurements)
        {
            items.Add(new ClientTimelineItem
            {
                Id = $"measurement:{m.PublicId}",
                Type = "measurement",
                OccurredAt = m.MeasuredAt,
                Title = "Zapsal tělesné míry",
                Description = m.WeightKg.HasValue ? $"Váha: {m.WeightKg.Value} kg" : null,
                Icon = "📏",
                Measurement = new TimelineMeasurementPayload
                {
                    WeightKg = m.WeightKg,
                    BodyFatPercentage = m.BodyFatPercentage,
                },
            });
        }

        // ── 4. Questionnaire responses (submitted only) — author-scoped: a coach sees only
        //      answers to questionnaires they wrote, independent of the capability flags. ──
        var questionnaires = await db.QuestionnaireResponses
            .AsNoTracking()
            .Include(r => r.Questionnaire)
            .Where(r => r.ClientId == clientUserId
                     && r.Questionnaire.ProfessionalId == trainerUserId
                     && r.SubmittedAt != null
                     && r.SubmittedAt >= from)
            .OrderByDescending(r => r.SubmittedAt)
            .Select(r => new { r.PublicId, r.SubmittedAt, QuestionnaireTitle = r.Questionnaire.Title })
            .ToListAsync(ct);

        foreach (var q in questionnaires)
        {
            items.Add(new ClientTimelineItem
            {
                Id = $"questionnaire:{q.PublicId}",
                Type = "questionnaire",
                OccurredAt = q.SubmittedAt!.Value,
                Title = "Vyplnil dotazník",
                Description = q.QuestionnaireTitle,
                Icon = "📋",
                Questionnaire = new TimelineQuestionnairePayload { QuestionnaireTitle = q.QuestionnaireTitle },
            });
        }

        // ── 5. Nutrition & training plan publish events ──
        // One event per published week. There is no plan-level DatePublished field — publish only
        // ever set weeks[].datePublished (#1014), and the unwritten plan-level one was deleted in
        // #1015. Each published week with a datePublished inside the lookback emits its own
        // event dated at that week's datePublished; a plan with none emits nothing.
        // Nutrition-domain: gated on CanViewNutritionPlans.
        if (effective.CanViewNutritionPlans)
        {
            var nutritionPlanFilter = Builders<NutritionPlan>.Filter.Eq(p => p.ClientId, clientUserId)
                & Builders<NutritionPlan>.Filter.Eq(p => p.NutritionistId, trainerUserId)
                & Builders<NutritionPlan>.Filter.ElemMatch(p => p.Weeks, w => w.Status == WeekStatus.Published);

            using var cursor = await mongo.NutritionPlans.FindAsync(
                nutritionPlanFilter,
                new FindOptions<NutritionPlan, NutritionPlan> { Projection = NutritionPlanPublishProjection },
                ct);
            var plans = await cursor.ToListAsync(ct);

            foreach (var plan in plans)
            {
                var publishedWeeks = plan.Weeks
                    .Where(w => w.Status == WeekStatus.Published && w.DatePublished.HasValue && w.DatePublished.Value >= from);

                foreach (var week in publishedWeeks)
                {
                    items.Add(new ClientTimelineItem
                    {
                        Id = $"nutrition_plan:{plan.ExternalId}:week:{week.WeekNumber}",
                        Type = "nutrition_plan_published",
                        OccurredAt = week.DatePublished!.Value,
                        Title = "Zveřejněn jídelníček",
                        Description = string.IsNullOrWhiteSpace(plan.Name) ? null : plan.Name,
                        Icon = "🥗",
                        PlanPublished = new TimelinePlanPublishedPayload
                        {
                            PlanId = plan.ExternalId,
                            PlanName = string.IsNullOrWhiteSpace(plan.Name) ? null : plan.Name,
                            WeekNumber = week.WeekNumber,
                        },
                    });
                }
            }
        }

        // Training-domain: gated on CanViewTrainingPlans.
        if (effective.CanViewTrainingPlans)
        {
            var trainingPlanFilter = Builders<TrainingPlan>.Filter.Eq(p => p.ClientId, clientUserId)
                & Builders<TrainingPlan>.Filter.Eq(p => p.TrainerId, trainerUserId)
                & Builders<TrainingPlan>.Filter.ElemMatch(p => p.Weeks, w => w.Status == WeekStatus.Published);

            using var cursor = await mongo.TrainingPlans.FindAsync(
                trainingPlanFilter,
                new FindOptions<TrainingPlan, TrainingPlan> { Projection = TrainingPlanPublishProjection },
                ct);
            var plans = await cursor.ToListAsync(ct);

            foreach (var plan in plans)
            {
                var publishedWeeks = plan.Weeks
                    .Where(w => w.Status == WeekStatus.Published && w.DatePublished.HasValue && w.DatePublished.Value >= from);

                foreach (var week in publishedWeeks)
                {
                    items.Add(new ClientTimelineItem
                    {
                        Id = $"training_plan:{plan.ExternalId}:week:{week.WeekNumber}",
                        Type = "training_plan_published",
                        OccurredAt = week.DatePublished!.Value,
                        Title = "Zveřejněn tréninkový plán",
                        Description = string.IsNullOrWhiteSpace(plan.Name) ? null : plan.Name,
                        Icon = "🏋",
                        PlanPublished = new TimelinePlanPublishedPayload
                        {
                            PlanId = plan.ExternalId,
                            PlanName = string.IsNullOrWhiteSpace(plan.Name) ? null : plan.Name,
                            WeekNumber = week.WeekNumber,
                        },
                    });
                }
            }
        }

        // ── 6. Personal records ── Training-domain: gated on CanViewTrainingPlans.
        if (effective.CanViewTrainingPlans)
        {
            var prFilter = Builders<PersonalRecord>.Filter.Eq(r => r.ClientId, clientUserId)
                & Builders<PersonalRecord>.Filter.Gte(r => r.AchievedAt, from);

            using var cursor = await mongo.PersonalRecords.FindAsync(prFilter, cancellationToken: ct);
            var records = await cursor.ToListAsync(ct);
            foreach (var record in records)
            {
                items.Add(new ClientTimelineItem
                {
                    Id = $"personal_record:{record.ExternalId}",
                    Type = "personal_record",
                    OccurredAt = record.AchievedAt,
                    Title = record.ExerciseName,
                    Description = $"{record.WeightKg} kg \u00d7 {record.Reps}",
                    Icon = "\U0001f3c6",
                    PersonalRecord = new PersonalRecordPayload
                    {
                        ExternalId = record.ExternalId,
                        ExerciseExternalId = record.ExerciseExternalId,
                        ExerciseName = record.ExerciseName,
                        WeightKg = record.WeightKg,
                        Reps = record.Reps,
                        WorkoutLogId = record.WorkoutLogId,
                    },
                });
            }
        }

        // ── 7. Trainer-client link (the "klient propojen" event) — dual-readable. ──
        items.Add(new ClientTimelineItem
        {
            Id = $"linked:{link.PublicId}",
            Type = "linked",
            OccurredAt = link.DateCreated,
            Title = "Klient propojen",
            Icon = "\U0001f517",
        });

        // Audit the trainer read of client activity.
        await audit.LogAsync(
            trainerUserId,
            "Read",
            "ClientTimeline",
            req.ClientId,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            ct: ct);

        var ordered = items
            .OrderByDescending(i => i.OccurredAt)
            .Take(req.Limit)
            .ToList();

        await Send.OkAsync(new GetClientTimelineResponse
        {
            Items = ordered,
            CanViewNutritionPlans = effective.CanViewNutritionPlans,
            CanViewTrainingPlans = effective.CanViewTrainingPlans
        }, ct);
    }

    /// <summary>
    /// Loads, by plan id, only the nutrition plans the caller authored for this client.
    /// </summary>
    private async Task<Dictionary<Guid, NutritionPlan>> LoadAuthoredNutritionPlansAsync(
        List<Guid> planIds, Guid clientUserId, Guid trainerUserId, CancellationToken ct)
    {
        if (planIds.Count == 0)
        {
            return [];
        }

        var filter = Builders<NutritionPlan>.Filter.In(p => p.ExternalId, planIds)
            & Builders<NutritionPlan>.Filter.Eq(p => p.ClientId, clientUserId)
            & Builders<NutritionPlan>.Filter.Eq(p => p.NutritionistId, trainerUserId);

        using var cursor = await mongo.NutritionPlans.FindAsync(
            filter,
            new FindOptions<NutritionPlan, NutritionPlan> { Projection = NutritionPlannedMealsProjection },
            ct);
        var plans = await cursor.ToListAsync(ct);

        return plans.ToDictionary(p => p.ExternalId);
    }

    /// <summary>
    /// Maps (plan id, session id) to the session name for training plans the caller authored
    /// for this client.
    /// </summary>
    private async Task<Dictionary<(Guid PlanId, Guid SessionId), string>> LoadAuthoredSessionNamesAsync(
        List<Guid> planIds, Guid clientUserId, Guid trainerUserId, CancellationToken ct)
    {
        if (planIds.Count == 0)
        {
            return [];
        }

        var filter = Builders<TrainingPlan>.Filter.In(p => p.ExternalId, planIds)
            & Builders<TrainingPlan>.Filter.Eq(p => p.ClientId, clientUserId)
            & Builders<TrainingPlan>.Filter.Eq(p => p.TrainerId, trainerUserId);

        using var cursor = await mongo.TrainingPlans.FindAsync(
            filter,
            new FindOptions<TrainingPlan, TrainingPlan> { Projection = TrainingSessionNamesProjection },
            ct);
        var plans = await cursor.ToListAsync(ct);

        var names = new Dictionary<(Guid PlanId, Guid SessionId), string>();

        foreach (var plan in plans)
        {
            var sessions = plan.Weeks.SelectMany(w => w.Days).SelectMany(d => d.Sessions);

            foreach (var session in sessions)
            {
                names.TryAdd((plan.ExternalId, session.SessionId), session.Name);
            }
        }

        return names;
    }
}
