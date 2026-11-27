using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.ClientNutrition.Shared;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using Microsoft.EntityFrameworkCore;
using MongoDB.Driver;

namespace FormUp.Application.Features.ClientNutrition.GetTodayLog;

/// <summary>
/// Endpoint that returns the client's meal log for today, including consumed and remaining nutrients.
/// </summary>
/// <param name="mongo">MongoDB context.</param>
/// <param name="db">Relational database context.</param>
/// <param name="blobStorage">Blob storage service — converts each meal photo's stored BlobUrl into
/// a short-lived pre-signed read URL before the response leaves the process (F9).</param>
/// <param name="timeProvider">Clock abstraction (#955) — lets tests pin the "now" instant deterministically.</param>
/// <param name="macroCalculator">Sums the nutrients of the eaten items.</param>
public class GetTodayLogEndpoint(
    IMongoContext mongo,
    IApplicationDbContext db,
    IBlobStorageService blobStorage,
    TimeProvider timeProvider,
    IMacroCalculatorService macroCalculator)
    : EndpointWithoutRequest<GetTodayLogResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/client/nutrition/log/today");
        Roles(AppRoles.Client);
        Summary(s =>
        {
            s.Summary = "Get today's meal log";
            s.Description = "Returns all meals logged today with nutrient totals and remaining targets.";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(CancellationToken ct)
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

        // Resolve the client's local calendar day (#935) — todayUtc anchors LogDate-style
        // equality checks; windowStartUtc/windowEndUtc anchor the EatenAt instant-range filter
        // so a meal logged near local midnight lands in the correct local day's window rather
        // than the server's UTC day.
        var (todayUtc, windowStartUtc, windowEndUtc) = await db.ResolveClientLocalDayWindowAsync(clientId, timeProvider.GetUtcNow().UtcDateTime, ct);

        // Fetch today's meal logs.
        // Matches three cases uniformly:
        //   1. Logs created via LogMealEaten after the LogDate field was added — both
        //      LogDate == today and EatenAt is within today's window.
        //   2. Photo-only logs created via SaveMealPhotos — LogDate == today, EatenAt null.
        //   3. Legacy logs created before LogDate existed — LogDate = default(DateTime),
        //      EatenAt is within today's window.
        // MealsEaten in the response therefore includes photo-only entries that haven't
        // been marked eaten yet, which is correct: the mobile side uses these records for
        // both "is eaten" and "has photos" semantics.
        var logFilter = Builders<MealLog>.Filter.And(
            Builders<MealLog>.Filter.Eq(l => l.ClientId, clientId),
            Builders<MealLog>.Filter.Or(
                Builders<MealLog>.Filter.Eq(l => l.LogDate, todayUtc),
                Builders<MealLog>.Filter.And(
                    Builders<MealLog>.Filter.Gte(l => l.EatenAt, windowStartUtc),
                    Builders<MealLog>.Filter.Lt(l => l.EatenAt, windowEndUtc))));

        var logCursor = await mongo.MealLogs.FindAsync(logFilter, cancellationToken: ct);
        var logs = await logCursor.ToListAsync(ct);

        // Fetch the Active plan whose date window contains today for meal names and global
        // settings — a client may hold several sequential, non-overlapping Active plans (#780).
        var planFilter = Builders<NutritionPlan>.Filter.And(
            Builders<NutritionPlan>.Filter.Eq(p => p.ClientId, clientId),
            Builders<NutritionPlan>.Filter.Eq(p => p.Status, NutritionPlanStatus.Active));

        var planCursor = await mongo.NutritionPlans.FindAsync(planFilter, cancellationToken: ct);
        var activePlans = await planCursor.ToListAsync(ct);
        var plan = PlanWindowResolver.ResolveCurrentPlan(activePlans, p => p.StartDate, p => p.Weeks.Count, todayUtc);

        // Resolve today's plan day so we can use pre-computed MealTotals
        // (which include both foods AND recipes, matching the mobile optimistic
        // update). Without this, totals are computed from FoodsEaten only and
        // miss recipe kcal contributions.
        PlanDay? todayPlanDay = null;
        if (plan is not null)
        {
            var publishedWeeks = plan.Weeks
                .Where(w => w.Status == WeekStatus.Published)
                .ToList();

            if (publishedWeeks.Count > 0)
            {
                if (plan.StartDate.HasValue)
                {
                    var daysSinceStart = (int)(todayUtc - plan.StartDate.Value.Date).TotalDays;
                    if (daysSinceStart >= 0)
                    {
                        var weekNum = daysSinceStart / 7 + 1;
                        var dayIdx = daysSinceStart % 7;
                        var todayWeek = publishedWeeks.FirstOrDefault(w => w.WeekNumber == weekNum)
                                        ?? publishedWeeks[^1];
                        if (dayIdx < todayWeek.Days.Count)
                            todayPlanDay = todayWeek.Days[dayIdx];
                    }
                }
                // No else: published weeks imply a StartDate, so the legacy plan-level
                // DatePublished cycling branch that stood here was unreachable (#1015).
            }
        }

        // Build lookup: MealId → plan meal (for name + pre-computed totals)
        var planMeals = new Dictionary<Guid, PlanMeal>();
        if (todayPlanDay is not null)
        {
            foreach (var meal in todayPlanDay.Meals)
                planMeals.TryAdd(meal.MealId, meal);
        }
        else if (plan is not null)
        {
            // Fallback: scan all weeks for meal names (no MealTotals guarantee)
            foreach (var meal in plan.Weeks.SelectMany(w => w.Days).SelectMany(d => d.Meals))
                planMeals.TryAdd(meal.MealId, meal);
        }

        // Map logs to DTOs. A fully eaten (or untouched) meal uses the plan's MealTotals, falling back to
        // FoodsEaten for meals not found in today's plan day; a partly eaten meal sums just its eaten
        // items from the live plan meal.
        var mealsEaten = logs.Select(log =>
        {
            planMeals.TryGetValue(log.MealId, out var planMeal);
            var eatenIds = MealItemEatenState.EffectiveEatenIds(log, planMeal);
            var state = MealItemEatenState.StateOf(
                MealItemEatenState.ItemIds(log, planMeal), eatenIds, log.EatenAt is not null);
            // A snapshot row reports what was eaten from its own snapshot, whatever the live plan says now.
            var totals = MealItemEatenState.SnapshotTotals(log.EatenSnapshot, eatenIds)
                         ?? (state == MealEatenState.PartlyEaten && planMeal is not null
                             ? MealItemEatenState.EatenTotals(planMeal, eatenIds, macroCalculator)
                             : planMeal?.MealTotals ?? CalculateTotals(log.FoodsEaten));

            return new TodayMealLogDto
            {
                MealId = log.MealId,
                MealName = planMeal?.Kind.ToString() ?? string.Empty,
                EatenAt = state == MealEatenState.Eaten ? log.EatenAt : null,
                EatenState = state,
                EatenItemIds = eatenIds.ToList(),
                Totals = totals,
                Photos = log.Photos
                    .Select(p => new MealPhotoDto { BlobUrl = p.BlobUrl, UploadedAt = p.UploadedAt, Note = p.Note })
                    .ToList(),
                Note = log.Note
            };
        }).ToList();

        // A stored BlobUrl is no longer publicly fetchable — mint a short-lived DisplayUrl for
        // each meal photo before it leaves the process (F9). BlobUrl itself stays the canonical,
        // permanent identity value.
        foreach (var photo in mealsEaten.SelectMany(m => m.Photos))
        {
            photo.DisplayUrl = await blobStorage.GenerateReadUrlAsync(photo.BlobUrl, ct) ?? string.Empty;
        }

        // Off-plan foods logged on today's day log count toward the consumed totals.
        var offPlanFoods = new List<OffPlanFoodEntry>();
        if (plan is not null)
        {
            var dayLogFilter = TodayDayLogFilter.Build(clientId, plan.ExternalId, todayUtc, windowStartUtc, windowEndUtc);
            using var dayLogCursor = await mongo.DayLogs.FindAsync(dayLogFilter, cancellationToken: ct);
            var dayLogs = await dayLogCursor.ToListAsync(ct);
            offPlanFoods = dayLogs.SelectMany(l => l.OffPlanFoods).OrderBy(e => e.LoggedAt).ToList();
        }

        // Only rows with an eaten item count, once per meal: photo-only rows are returned above for
        // their photos but contribute nothing to consumption; a partly eaten row contributes its
        // eaten items' totals.
        var eatenMeals = mealsEaten
            .Where(m => m.EatenState != MealEatenState.NotEaten)
            .GroupBy(m => m.MealId)
            .Select(g => g.First())
            .ToList();

        // Sum eaten meal totals plus off-plan foods
        var totalConsumed = new NutrientTotals
        {
            Kcal = eatenMeals.Sum(m => m.Totals.Kcal) + offPlanFoods.Sum(e => e.Kcal),
            Protein = eatenMeals.Sum(m => m.Totals.Protein) + offPlanFoods.Sum(e => e.Protein),
            Carbs = eatenMeals.Sum(m => m.Totals.Carbs) + offPlanFoods.Sum(e => e.Carbs),
            Fat = eatenMeals.Sum(m => m.Totals.Fat) + offPlanFoods.Sum(e => e.Fat),
            Fiber = eatenMeals.Sum(m => m.Totals.Fiber) + offPlanFoods.Sum(e => e.Fiber)
        };

        // Calculate remaining if global settings exist
        NutrientTotals? remaining = null;
        if (plan?.GlobalSettings is not null)
        {
            var gs = plan.GlobalSettings;
            remaining = new NutrientTotals
            {
                Kcal = (gs.DailyKcal ?? 0) - totalConsumed.Kcal,
                Protein = (gs.ProteinGrams ?? 0) - totalConsumed.Protein,
                Carbs = (gs.CarbsGrams ?? 0) - totalConsumed.Carbs,
                Fat = (gs.FatGrams ?? 0) - totalConsumed.Fat,
                Fiber = (gs.FiberGrams ?? 0) - totalConsumed.Fiber
            };
        }

        await Send.OkAsync(new GetTodayLogResponse
        {
            MealsEaten = mealsEaten,
            OffPlanFoods = offPlanFoods.Select(TodayOffPlanFoodDto.FromDocument).ToList(),
            TotalConsumed = totalConsumed,
            Remaining = remaining
        }, ct);
    }

    /// <summary>
    /// Calculates nutrient totals from a list of foods based on their amount and per-100g values.
    /// </summary>
    /// <param name="foods">The foods to calculate totals for.</param>
    /// <returns>Aggregated nutrient totals.</returns>
    private static NutrientTotals CalculateTotals(List<MealFood> foods)
    {
        var totals = new NutrientTotals();

        foreach (var food in foods)
        {
            var ratio = food.AmountGrams / 100m;
            totals.Kcal += food.NutrientValuePer100Grams.Kcal * ratio;
            totals.Protein += food.NutrientValuePer100Grams.Protein * ratio;
            totals.Carbs += food.NutrientValuePer100Grams.Carbs * ratio;
            totals.Fat += food.NutrientValuePer100Grams.Fat * ratio;
        }

        return totals;
    }
}
