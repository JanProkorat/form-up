using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.ClientTraining;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using Microsoft.EntityFrameworkCore;
using MongoDB.Driver;

namespace FormUp.Application.Features.Client.Plans.GetClientPlans;

/// <summary>
/// Returns a list of the authenticated client's plans (nutrition + training),
/// optionally filtered by status.
/// </summary>
/// <param name="mongo">MongoDB context.</param>
/// <param name="db">Relational database context.</param>
/// <param name="timeProvider">Clock abstraction (#955) — lets tests pin the "now" instant deterministically.</param>
/// <param name="macroCalculator">Meal-totals calculator for the per-day nutrition averages.</param>
/// <param name="entitlements">Subscription entitlements, used for the over-cap check.</param>
public class GetClientPlansEndpoint(
    IMongoContext mongo,
    IApplicationDbContext db,
    TimeProvider timeProvider,
    IMacroCalculatorService macroCalculator,
    EntitlementService entitlements)
    : Endpoint<GetClientPlansRequest, GetClientPlansResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/client/plans");
        Roles(AppRoles.Client);
        Summary(s =>
        {
            s.Summary = "List client plans";
            s.Description =
                "Returns a combined list of nutrition and training plans for the authenticated client, optionally filtered by status.";
            s.Responses[StatusCodes.Status200OK] = "The client's plans";
            s.Responses[StatusCodes.Status401Unauthorized] = "Missing or unreadable caller claim";
            s.Responses[StatusCodes.Status404NotFound] = "Client profile not found";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(GetClientPlansRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);
        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var clientProfile = await db.ClientProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(cp => cp.UserId == Guid.Parse(userId), ct);

        if (clientProfile is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // Canonical client id on Mongo docs is ApplicationUser.Id (#840).
        var clientId = clientProfile.UserId;

        // Resolve the client's local calendar day (#935) — anchors current-week resolution,
        // plan-window disambiguation, and the day-of-week HasTodaySession check below on the
        // client's local "today" rather than the server's UTC day.
        var now = await db.ResolveClientLocalDateUtcAsync(clientId, timeProvider.GetUtcNow().UtcDateTime, ct);

        // Parse status filter
        NutritionPlanStatus? nutritionStatus = null;
        TrainingPlanStatus? trainingStatus = null;

        if (!string.IsNullOrWhiteSpace(req.Status))
        {
            if (Enum.TryParse<NutritionPlanStatus>(req.Status, ignoreCase: true, out var ns))
                nutritionStatus = ns;
            if (Enum.TryParse<TrainingPlanStatus>(req.Status, ignoreCase: true, out var ts))
                trainingStatus = ts;
        }

        var items = new List<ClientOwnPlanItem>();
        List<NutritionPlan> nutritionPlans = [];
        List<TrainingPlan> trainingPlans = [];

        // ── Nutrition plans ──
        {
            var filter = Builders<NutritionPlan>.Filter.Eq(p => p.ClientId, clientId);

            if (nutritionStatus.HasValue)
            {
                filter &= Builders<NutritionPlan>.Filter.Eq(p => p.Status, nutritionStatus.Value);
            }
            else
            {
                // Exclude drafts by default
                filter &= Builders<NutritionPlan>.Filter.Ne(p => p.Status, NutritionPlanStatus.Draft);
            }

            // Only return plans that have at least one published week
            filter &= Builders<NutritionPlan>.Filter.ElemMatch(
                p => p.Weeks, w => w.Status == WeekStatus.Published);

            using var nCursor = await mongo.NutritionPlans.FindAsync(filter, cancellationToken: ct);
            nutritionPlans = await nCursor.ToListAsync(ct);

            foreach (var plan in nutritionPlans)
            {
                var publishedWeeks = plan.Weeks
                    .Where(w => w.Status == WeekStatus.Published)
                    .OrderBy(w => w.WeekNumber)
                    .ToList();

                // Distinct() is a no-op for plans with unique week numbers (publishedWeeks is
                // already ascending by WeekNumber above). It only changes anything for a legacy
                // plan whose weeks carry a DUPLICATE WeekNumber — PlanWeekCalculator's legacy
                // cycle must use the same deduped cycle length the ClientNutrition/ClientTraining
                // "today" reads use for that plan, or the two disagree on which week is current
                // (#850).
                var publishedWeekNumbers = publishedWeeks.Select(w => w.WeekNumber).Distinct().ToList();
                var currentWeek = PlanWeekCalculator.ResolveCurrentWeekNumber(
                    plan.StartDate,
                    publishedWeekNumbers,
                    plan.Weeks.Count,
                    publishedWeeks.FirstOrDefault()?.DatePublished,
                    plan.DateCreated,
                    now);

                var averages = CalculateDailyAverages(publishedWeeks);

                items.Add(new ClientOwnPlanItem
                {
                    AvgKcalPerDay = averages?.Kcal,
                    AvgProteinPerDay = averages?.Protein,
                    AvgCarbsPerDay = averages?.Carbs,
                    AvgFatPerDay = averages?.Fat,
                    AvgFiberPerDay = averages?.Fiber,
                    PlanId = plan.ExternalId,
                    PlanName = plan.Name,
                    Type = "nutrition",
                    Status = plan.Status.ToString(),
                    StartDate = plan.StartDate,
                    TotalWeeks = plan.Weeks.Count,
                    PublishedWeekCount = publishedWeeks.Count,
                    DateCompleted = plan.DateCompleted,
                    QuestionnaireResponseId = plan.QuestionnaireResponseId,
                    CurrentWeek = currentWeek,
                    DailyKcal = plan.GlobalSettings?.DailyKcal,
                    HasTodaySession = null, // nutrition plans don't have sessions
                    EndDate = PlanProgressCalculator.ResolveEndDate(plan.StartDate, plan.Weeks.Count),
                    ProfessionalRole = AppRoles.Nutritionist
                });
            }
        }

        // ── Training plans ──
        {
            var filter = Builders<TrainingPlan>.Filter.Eq(p => p.ClientId, clientId);

            if (trainingStatus.HasValue)
            {
                filter &= Builders<TrainingPlan>.Filter.Eq(p => p.Status, trainingStatus.Value);
            }
            else
            {
                filter &= Builders<TrainingPlan>.Filter.Ne(p => p.Status, TrainingPlanStatus.Draft);
            }

            // Only return plans that have at least one published week
            filter &= Builders<TrainingPlan>.Filter.ElemMatch(
                p => p.Weeks, w => w.Status == WeekStatus.Published);

            using var tCursor = await mongo.TrainingPlans.FindAsync(filter, cancellationToken: ct);
            trainingPlans = await tCursor.ToListAsync(ct);

            // Today's day-of-week (1 = Monday, 7 = Sunday)
            var todayDow = (int)now.DayOfWeek;
            todayDow = todayDow == 0 ? 7 : todayDow;

            // A client may hold several sequential, non-overlapping Active training plans
            // (#780). GetTodaySessionEndpoint disambiguates which ONE of them is "current" via
            // PlanWindowResolver.ResolveCurrentPlan before answering whether there's a session
            // today (#873) — this endpoint must apply the same disambiguation, or an unranged
            // Active sibling can independently claim HasTodaySession=true for a plan the
            // today-session endpoint never selected. Non-Active plans (Completed/Archived) are
            // unaffected — they carry no "current plan" concept to disambiguate.
            var activeTrainingPlans = trainingPlans.Where(p => p.Status == TrainingPlanStatus.Active).ToList();
            var currentActiveTrainingPlan = PlanWindowResolver.ResolveCurrentPlan(
                activeTrainingPlans,
                p => p.StartDate,
                p => p.Weeks.Count,
                now);

            foreach (var plan in trainingPlans)
            {
                var publishedWeeks = plan.Weeks
                    .Where(w => w.Status == WeekStatus.Published)
                    .OrderBy(w => w.WeekNumber)
                    .ToList();

                // NOT deduped, deliberately. PlanWeekCalculator's legacy branch cycles on
                // this list's Count, and the four ClientTraining callers
                // (GetTodaySession, GetFullTrainingPlan, TrainingProgressBroadcaster,
                // MarkWholeDayComplete) all pass the raw list. Deduping only here would make
                // the plans list name a different current week than the Today screen for a
                // legacy training plan with duplicate week numbers. Deduping the training
                // side belongs inside PlanWeekCalculator so all callers move together.
                var publishedWeekNumbers = publishedWeeks.Select(w => w.WeekNumber).ToList();
                var currentWeekNumber = PlanWeekCalculator.ResolveCurrentWeekNumber(
                    plan.StartDate,
                    publishedWeekNumbers,
                    plan.Weeks.Count,
                    publishedWeeks.FirstOrDefault()?.DatePublished,
                    plan.DateCreated,
                    now);

                bool? hasTodaySession = null;
                if (currentWeekNumber.HasValue)
                {
                    var currentWeek = plan.Weeks.FirstOrDefault(w => w.WeekNumber == currentWeekNumber.Value);
                    if (currentWeek is null || currentWeek.Status != WeekStatus.Published)
                        currentWeek = publishedWeeks.Last();

                    // CurrentWeek stays informational for every plan (set below regardless of
                    // selection), but only the plan ResolveCurrentPlan selected among Active
                    // siblings may assert a live session for today.
                    var isDisambiguatedCurrentPlan = plan.Status != TrainingPlanStatus.Active
                        || (currentActiveTrainingPlan is not null && currentActiveTrainingPlan.ExternalId == plan.ExternalId);

                    hasTodaySession = isDisambiguatedCurrentPlan
                        && currentWeek.Days.Any(d => d.DayOfWeek == todayDow && d.Sessions.Count > 0);
                }

                items.Add(new ClientOwnPlanItem
                {
                    PlanId = plan.ExternalId,
                    PlanName = plan.Name,
                    Type = "training",
                    Status = plan.Status.ToString(),
                    StartDate = plan.StartDate,
                    TotalWeeks = plan.Weeks.Count,
                    PublishedWeekCount = publishedWeeks.Count,
                    DateCompleted = plan.DateCompleted,
                    QuestionnaireResponseId = plan.QuestionnaireResponseId,
                    CurrentWeek = currentWeekNumber,
                    DailyKcal = null, // training plans don't have kcal targets
                    HasTodaySession = hasTodaySession,
                    EndDate = PlanProgressCalculator.ResolveEndDate(plan.StartDate, plan.Weeks.Count),
                    ProfessionalRole = AppRoles.Trainer
                });
            }
        }

        var itemsByPlanId = items.ToDictionary(i => i.PlanId);
        await ApplyTrainingProgressAsync(clientId, now, trainingPlans, itemsByPlanId, ct);
        await ApplyNutritionProgressAsync(clientId, now, nutritionPlans, itemsByPlanId, ct);
        await ApplyProfessionalNamesAsync(nutritionPlans, trainingPlans, itemsByPlanId, ct);
        await ApplyRunsUntilAsync(clientId, nutritionPlans, trainingPlans, itemsByPlanId, ct);

        // Sort by completion date descending, then creation
        items = items
            .OrderByDescending(i => i.DateCompleted ?? DateTime.MinValue)
            .ToList();

        await Send.OkAsync(new GetClientPlansResponse { Items = items }, ct);
    }

    /// <summary>
    /// Mean of each published day's planned totals over the days that have at least one meal,
    /// rounded to whole numbers. Null when no such day exists.
    /// </summary>
    private NutrientTotals? CalculateDailyAverages(List<PlanWeek> publishedWeeks)
    {
        var dayTotals = publishedWeeks
            .SelectMany(week => week.Days)
            .Where(day => day.Meals.Count > 0)
            .Select(day =>
            {
                var total = new NutrientTotals();
                foreach (var meal in day.Meals)
                {
                    var mealTotals = macroCalculator.CalculateMealTotals(meal.Foods, meal.Recipes);
                    total.Kcal += mealTotals.Kcal;
                    total.Protein += mealTotals.Protein;
                    total.Carbs += mealTotals.Carbs;
                    total.Fat += mealTotals.Fat;
                    total.Fiber += mealTotals.Fiber;
                }

                return total;
            })
            .ToList();

        if (dayTotals.Count == 0)
        {
            return null;
        }

        static decimal Mean(IEnumerable<decimal> values, int count) =>
            Math.Round(values.Sum() / count, 0, MidpointRounding.AwayFromZero);

        return new NutrientTotals
        {
            Kcal = Mean(dayTotals.Select(t => t.Kcal), dayTotals.Count),
            Protein = Mean(dayTotals.Select(t => t.Protein), dayTotals.Count),
            Carbs = Mean(dayTotals.Select(t => t.Carbs), dayTotals.Count),
            Fat = Mean(dayTotals.Select(t => t.Fat), dayTotals.Count),
            Fiber = Mean(dayTotals.Select(t => t.Fiber), dayTotals.Count),
        };
    }

    private async Task ApplyTrainingProgressAsync(
        Guid clientId,
        DateTime today,
        List<TrainingPlan> plans,
        Dictionary<Guid, ClientOwnPlanItem> itemsByPlanId,
        CancellationToken ct)
    {
        var windowedPlans = plans
            .Select(plan => (Plan: plan, Window: PlanProgressCalculator.ResolveProgressWindow(plan.StartDate, plan.Weeks.Count, today)))
            .Where(entry => entry.Window.HasValue)
            .ToList();

        if (windowedPlans.Count == 0)
        {
            return;
        }

        var planIds = windowedPlans.Select(entry => (Guid?)entry.Plan.ExternalId).ToList();
        var filter = Builders<SessionExecution>.Filter.Eq(e => e.ClientId, clientId)
            & Builders<SessionExecution>.Filter.In(e => e.PlanId, planIds);

        using var cursor = await mongo.SessionExecutions.FindAsync(filter, cancellationToken: ct);
        var executions = await cursor.ToListAsync(ct);

        foreach (var (plan, window) in windowedPlans)
        {
            var (total, done) = PlanProgressCalculator.CountSessions(plan, window!.Value, executions);

            if (total > 0)
            {
                itemsByPlanId[plan.ExternalId].SessionsTotal = total;
                itemsByPlanId[plan.ExternalId].SessionsDone = done;
            }
        }
    }

    private async Task ApplyNutritionProgressAsync(
        Guid clientId,
        DateTime today,
        List<NutritionPlan> plans,
        Dictionary<Guid, ClientOwnPlanItem> itemsByPlanId,
        CancellationToken ct)
    {
        var windowedPlans = plans
            .Select(plan => (Plan: plan, Window: PlanProgressCalculator.ResolveProgressWindow(plan.StartDate, plan.Weeks.Count, today)))
            .Where(entry => entry.Window.HasValue)
            .ToList();

        if (windowedPlans.Count == 0)
        {
            return;
        }

        var earliestStart = windowedPlans.Min(entry => entry.Window!.Value.Start);
        var latestEnd = windowedPlans.Max(entry => entry.Window!.Value.End);
        var planIds = windowedPlans.Select(entry => entry.Plan.ExternalId).ToList();

        // Rows are selected by LogDate (EatenAt for legacy rows): a partly eaten row has no EatenAt.
        var filter = MealLogDayFilter.BuildClientRange(
            clientId,
            planIds,
            DateTime.SpecifyKind(earliestStart.Date, DateTimeKind.Utc),
            DateTime.SpecifyKind(latestEnd.Date, DateTimeKind.Utc),
            earliestStart,
            latestEnd.AddDays(1));

        using var cursor = await mongo.MealLogs.FindAsync(filter, cancellationToken: ct);
        var plansById = windowedPlans.ToDictionary(entry => entry.Plan.ExternalId, entry => entry.Plan);
        var meals = MealLogMerger.Merge(
            await cursor.ToListAsync(ct),
            (planId, day, mealId) => plansById.TryGetValue(planId, out var plan)
                ? NutritionPlanDayResolver.FindMeal(plan, day, mealId)
                : null);

        foreach (var (plan, window) in windowedPlans)
        {
            var planned = 0;
            for (var day = window!.Value.Start; day <= window.Value.End; day = day.AddDays(1))
            {
                planned += PlannedMealCounter.GetPlannedMealCountForDate(plan, day);
            }

            if (planned == 0)
            {
                continue;
            }

            // A partly eaten meal adds its eaten share instead of a whole meal.
            var logged = meals
                .Where(meal => meal.PlanId == plan.ExternalId
                    && meal.DayUtc >= window.Value.Start.Date
                    && meal.DayUtc <= window.Value.End.Date)
                .Sum(meal => meal.Share);

            itemsByPlanId[plan.ExternalId].MealsLoggedPercent =
                Math.Min(100, (int)Math.Round(logged * 100m / planned, MidpointRounding.AwayFromZero));
        }
    }

    /// <summary>
    /// Fills RunsUntil on Active plans whose author lapsed to the Free plan and left this client over its cap.
    /// The over-cap answer costs several queries, so it is looked up once per author.
    /// </summary>
    private async Task ApplyRunsUntilAsync(
        Guid clientId,
        List<NutritionPlan> nutritionPlans,
        List<TrainingPlan> trainingPlans,
        Dictionary<Guid, ClientOwnPlanItem> itemsByPlanId,
        CancellationToken ct)
    {
        var overCapByAuthor = new Dictionary<Guid, bool>();

        async Task<bool> IsOverCapAsync(Guid authorId)
        {
            if (!overCapByAuthor.TryGetValue(authorId, out var overCap))
            {
                overCap = await entitlements.IsClientOverCapAsync(authorId, clientId, ct);
                overCapByAuthor[authorId] = overCap;
            }

            return overCap;
        }

        foreach (var plan in nutritionPlans.Where(p => p.Status == NutritionPlanStatus.Active))
        {
            if (await IsOverCapAsync(plan.NutritionistId))
            {
                itemsByPlanId[plan.ExternalId].RunsUntil = PlanRunsUntilResolver.Resolve(
                    plan.StartDate,
                    plan.Weeks.Where(w => w.Status == WeekStatus.Published).Select(w => w.WeekNumber));
            }
        }

        foreach (var plan in trainingPlans.Where(p => p.Status == TrainingPlanStatus.Active))
        {
            if (await IsOverCapAsync(plan.TrainerId))
            {
                itemsByPlanId[plan.ExternalId].RunsUntil = PlanRunsUntilResolver.Resolve(
                    plan.StartDate,
                    plan.Weeks.Where(w => w.Status == WeekStatus.Published).Select(w => w.WeekNumber));
            }
        }
    }

    private async Task ApplyProfessionalNamesAsync(
        List<NutritionPlan> nutritionPlans,
        List<TrainingPlan> trainingPlans,
        Dictionary<Guid, ClientOwnPlanItem> itemsByPlanId,
        CancellationToken ct)
    {
        var authorIds = nutritionPlans.Select(plan => plan.NutritionistId)
            .Concat(trainingPlans.Select(plan => plan.TrainerId))
            .Distinct()
            .ToList();

        if (authorIds.Count == 0)
        {
            return;
        }

        var authors = await db.Users
            .AsNoTracking()
            .Where(user => authorIds.Contains(user.Id))
            .Select(user => new { user.Id, user.FirstName, user.LastName })
            .ToListAsync(ct);

        var namesById = authors.ToDictionary(author => author.Id, author => $"{author.FirstName} {author.LastName}");

        foreach (var plan in nutritionPlans)
        {
            itemsByPlanId[plan.ExternalId].ProfessionalName = namesById.GetValueOrDefault(plan.NutritionistId);
        }

        foreach (var plan in trainingPlans)
        {
            itemsByPlanId[plan.ExternalId].ProfessionalName = namesById.GetValueOrDefault(plan.TrainerId);
        }
    }
}
