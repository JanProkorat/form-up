using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.NutritionPlans.GetPlan;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Application.Middleware;
using Microsoft.EntityFrameworkCore;
using MongoDB.Driver;

namespace FormUp.Application.Features.NutritionPlans.UpdatePlan;

/// <summary>
/// Full-state update of a nutrition plan: replaces name, settings, and all weeks/days/meals/foods.
/// Preserves per-week Status and DatePublished. Uses optimistic concurrency.
/// </summary>
/// <param name="mongo">MongoDB context.</param>
/// <param name="macroCalculator">Service to recalculate nutrient totals.</param>
/// <param name="db">Relational database context used to resolve the client user id for notifications.</param>
/// <param name="notifier">Realtime notifier used to push the plan-updated event to the client.</param>
/// <param name="guard">Shared version-gated fetch-check-replace-409 skeleton.</param>
/// <param name="linkAuthorizationService">Resolves link capabilities — authorship identifies the
/// plan, the caller's live link to its client decides access.</param>
/// <param name="timeProvider">Clock used to find the client's local today for the meal lock.</param>
[RemovedCoachRole(AppRoles.Nutritionist, RemovedCoachRoleMode.WhilePlanInProgress)]
public class UpdatePlanEndpoint(
    IMongoContext mongo,
    IMacroCalculatorService macroCalculator,
    IApplicationDbContext db,
    IRealtimeNotifier notifier,
    PlanConcurrencyGuard guard,
    IClientLinkAuthorizationService linkAuthorizationService,
    TimeProvider timeProvider)
    : Endpoint<UpdatePlanRequest, GetPlanResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Put("/nutrition/plans/{PlanId}");
        Roles(AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Full-state update of a nutrition plan";
            s.Description = "Replaces the plan's name, global settings, and all weeks/days/meals/foods. " +
                            "Per-week publish status is preserved. Uses optimistic concurrency via version field. " +
                            "Meals the client ate (fully or partly) and meals on days before the client's local today " +
                            "are locked: changing, moving or deleting one returns 409 MEAL_LOCKED, adding a meal to a " +
                            "past day returns 409 PAST_DAY_LOCKED. With PreserveLockedMeals the locked and past-day " +
                            "meals are kept as stored instead and the rest of the payload is applied.";
            s.Response<GetPlanResponse>(StatusCodes.Status200OK, "Updated plan");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest, "Invalid request, or a published week was removed or the start date is invalid");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or unreadable caller claim");
            s.Response(StatusCodes.Status403Forbidden,
                "The subscription does not include plans (SUBSCRIPTION_REQUIRED), or the client is over the Free plan's client limit " +
                "and the plan's last published week has ended or the start date changed (CLIENT_OVER_PLAN_LIMIT)");
            s.Response(StatusCodes.Status404NotFound, "Plan not found, or not editable by the caller");
            s.Response(StatusCodes.Status409Conflict,
                "PLAN_VERSION_CONFLICT, MEAL_LOCKED or PAST_DAY_LOCKED. Lock responses (MEAL_LOCKED, " +
                "PAST_DAY_LOCKED) also carry the rejected meal as top-level mealId (null when the request " +
                "sent none), weekNumber and dayOfWeek");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(UpdatePlanRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);
        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var nutritionistId = Guid.Parse(userId);

        var lookupFilter = Builders<NutritionPlan>.Filter.Eq(p => p.ExternalId, req.PlanId)
                     & Builders<NutritionPlan>.Filter.Eq(p => p.NutritionistId, nutritionistId);
        var replaceFilter = Builders<NutritionPlan>.Filter.Eq(p => p.ExternalId, req.PlanId)
                            & Builders<NutritionPlan>.Filter.Eq(p => p.Version, req.Version);

        var guardResult = await guard.ReplaceWithVersionGuardAsync(
            mongo.NutritionPlans,
            lookupFilter,
            replaceFilter,
            req.Version,
            p => p.Version,
            (plan, authorizeCt) => AuthorizeAsync(plan, nutritionistId, authorizeCt),
            (plan, mutateCt) => MutateAsync(plan, nutritionistId, req, mutateCt),
            ct);

        switch (guardResult.Outcome)
        {
            case PlanConcurrencyOutcome.NotFound:
                await Send.NotFoundAsync(ct);
                return;
            case PlanConcurrencyOutcome.VersionConflict:
                await this.SendProblemAsync(409, ErrorCodes.PlanVersionConflict,
                    "Version conflict. The plan was modified by another request.", ct);
                return;
            case PlanConcurrencyOutcome.ReplaceConflict:
                await this.SendProblemAsync(409, ErrorCodes.PlanVersionConflict,
                    "Version conflict. The plan was modified concurrently.", ct);
                return;
            case PlanConcurrencyOutcome.HandledByMutator:
                // The authorize delegate wrote its 404, or the mutate delegate refused a removed-role extension.
                return;
        }

        var plan = guardResult.Document!;

        // Notify the client in real-time when published weeks were modified
        if (plan.Weeks.Any(w => w.Status == WeekStatus.Published))
        {
            // NutritionPlan.ClientId is ApplicationUser.Id (#840).
            var clientProfile = await db.ClientProfiles
                .AsNoTracking()
                .FirstOrDefaultAsync(cp => cp.UserId == plan.ClientId, ct);

            if (clientProfile is not null)
            {
                await notifier.NotifyAsync(clientProfile.UserId, "nutritionplanupdated", new
                {
                    PlanId = plan.ExternalId,
                }, ct);
            }
        }

        // Response ClientId must stay the client-facing ClientProfile.PublicId (pre-#840
        // contract), regardless of whether the published-week notification branch above ran.
        var clientPublicId = await db.ResolveClientPublicIdAsync(plan.ClientId, ct);
        await Send.OkAsync(GetPlanResponse.FromDocument(plan, clientPublicId), ct);
    }

    /// <summary>
    /// The lookup filter proved authorship, which is permanent. Access is not — require the
    /// caller's link to the plan's client to still grant nutrition access. Runs before the
    /// guard's version comparison so a denial is indistinguishable from a missing plan.
    /// </summary>
    private async Task<bool> AuthorizeAsync(NutritionPlan plan, Guid nutritionistId, CancellationToken ct)
    {
        // plan.ClientId is ApplicationUser.Id (#840) — the UserId-addressed overload.
        var capabilities = await linkAuthorizationService.GetCapabilitiesByClientUserIdAsync(
            nutritionistId, plan.ClientId, ct);

        if (capabilities is not { CanViewNutritionPlans: true })
        {
            await Send.NotFoundAsync(ct);
            return false;
        }

        // The subscription gate comes after the lookup, so a missing or foreign plan still answers 404.
        return !await this.RespondIfFeatureNotEntitledAsync(
            db, nutritionistId, entitlements => entitlements.CanCreatePlans,
            "The current subscription does not include editing plans.", ct);
    }

    private async Task<bool> RefuseExtensionAsync(CancellationToken ct)
    {
        await this.SendErrorWithCodeAsync(
            ErrorCodes.CoachRoleRemoved,
            "This coach role was removed; an in-progress plan cannot be extended.",
            StatusCodes.Status403Forbidden,
            ct);
        return false;
    }

    /// <summary>
    /// Endpoint-specific validation and mutation applied to the fetched plan before the
    /// version-gated replace. Returns <c>false</c> after writing a 403 when a removed coach role
    /// tries to extend an in-progress plan; other validation failures throw via <c>ThrowError</c>.
    /// </summary>
    private async Task<bool> MutateAsync(NutritionPlan plan, Guid nutritionistId, UpdatePlanRequest req, CancellationToken ct)
    {
        // A client over the Free plan's cap keeps the published weeks they have: the plan stays editable
        // until the last of them ends, but not in a way that pushes that end out.
        var overCapAccess = await this.RespondIfClientOverPlanLimitAfterRunsUntilAsync(
            db,
            nutritionistId,
            plan.ClientId,
            PlanRunsUntilResolver.Resolve(
                plan.StartDate, plan.Weeks.Where(w => w.Status == WeekStatus.Published).Select(w => w.WeekNumber)),
            ct);

        if (overCapAccess == OverCapEditAccess.Refused)
        {
            return false;
        }

        if (overCapAccess == OverCapEditAccess.WindowOpen && req.StartDate?.Date != plan.StartDate?.Date)
        {
            await this.SendProblemAsync(
                StatusCodes.Status403Forbidden,
                ErrorCodes.ClientOverPlanLimit,
                "The start date of a plan cannot change while its client is over the Free plan's client limit.",
                ct);
            return false;
        }

        // A removed role may finish its in-progress plans but not push their end date out.
        if (HttpContext.Items.ContainsKey(RemovedCoachRoleMiddleware.RoleRemovedItemKey)
            && plan.StartDate.HasValue
            && PlanWindowResolver.EndsLater(plan.StartDate.Value, plan.Weeks.Count, req.StartDate, req.Weeks.Count))
        {
            return await RefuseExtensionAsync(ct);
        }

        // Build lookup of existing week statuses
        var existingWeeks = plan.Weeks.ToDictionary(w => w.WeekNumber);

        // Check that no published weeks are being removed
        var incomingWeekNumbers = req.Weeks.Select(w => w.WeekNumber).ToHashSet();
        var removedPublished = plan.Weeks
            .Where(w => w.Status == WeekStatus.Published && !incomingWeekNumbers.Contains(w.WeekNumber))
            .ToList();

        if (removedPublished.Count > 0)
        {
            ThrowError($"Cannot remove published weeks: {string.Join(", ", removedPublished.Select(w => w.WeekNumber))}");
            return false;
        }

        // Start date validation
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        if (plan.StartDate.HasValue && req.StartDate?.Date != plan.StartDate.Value.Date)
        {
            // Trying to change or clear an existing start date
            if (DateOnly.FromDateTime(plan.StartDate.Value) < today)
            {
                ThrowError(ErrorCodes.StartDateLocked, "Start date cannot be changed after it has arrived.");
                return false;
            }

            // Clearing: only allowed if no weeks are published
            if (!req.StartDate.HasValue && plan.Weeks.Any(w => w.Status == WeekStatus.Published))
            {
                ThrowError(ErrorCodes.StartDateLocked, "Start date cannot be cleared when weeks are published.");
                return false;
            }
        }

        if (req.StartDate.HasValue)
        {
            if (req.StartDate.Value.DayOfWeek != System.DayOfWeek.Monday)
            {
                ThrowError(ErrorCodes.StartDateNotMonday, "Start date must be a Monday.");
                return false;
            }

            // Only enforce "not in past" when the start date is being set or changed.
            // A plan that has already started naturally has a past start date in every
            // subsequent save — that must not block editing of other fields.
            var isStartDateNewOrChanged = !plan.StartDate.HasValue
                || req.StartDate.Value.Date != plan.StartDate.Value.Date;
            if (isStartDateNewOrChanged && DateOnly.FromDateTime(req.StartDate.Value) < today)
            {
                ThrowError(ErrorCodes.StartDateInPast, "Start date cannot be in the past.");
                return false;
            }
        }

        // Lock rule: judged on the stored plan before it is replaced. The stored start date dates the
        // slots; it can only change while no day has started, so it never moves a day out of the past.
        var clientToday = await ResolveClientTodayAsync(plan, ct);
        var lockedMeals = PlanMealLockResolver
            .Resolve(plan, await LoadMealLogsAsync(plan, ct), clientToday)
            .Where(mealLock => mealLock.IsLocked)
            .GroupBy(mealLock => mealLock.Meal.MealId)
            .ToDictionary(group => group.Key, group => group.First());
        var storedStartDate = plan.StartDate;

        if (!req.PreserveLockedMeals
            && FindLockViolation(req, storedStartDate, lockedMeals, clientToday) is { } violation)
        {
            await this.SendProblemAsync(
                409,
                violation.ErrorCode,
                violation.Message,
                ct,
                new Dictionary<string, object?>
                {
                    ["mealId"] = violation.MealId,
                    ["weekNumber"] = violation.WeekNumber,
                    ["dayOfWeek"] = violation.DayOfWeek
                });
            return false;
        }

        // Meal ids key the client's diary photo folder, so a caller-sent id may not collide with
        // another plan: foreign ids and repeats later in the request get a fresh id, silently.
        var foreignMealIds = await FindForeignMealIdsAsync(plan, req, ct);
        var keptMealIds = new HashSet<Guid>();

        Guid ResolveMealId(Guid? requested)
        {
            if (requested is { } mealId && !foreignMealIds.Contains(mealId) && keptMealIds.Add(mealId))
            {
                return mealId;
            }

            return Guid.NewGuid();
        }

        // Snapshot the stored meals before the weeks are replaced, so StampChangedMeals can tell
        // which meals really changed and stamp them with the version this save produces.
        var storedMeals = plan.Weeks
            .SelectMany(w => w.Days.SelectMany(d => d.Meals.Select(m => (Meal: m, w.WeekNumber, d.DayOfWeek))))
            .GroupBy(entry => entry.Meal.MealId)
            .ToDictionary(group => group.Key, group => group.First());

        // Map request to domain
        plan.Name = req.Name;
        plan.StartDate = req.StartDate.HasValue ? DateTime.SpecifyKind(req.StartDate.Value.Date, DateTimeKind.Utc) : null;
        plan.GlobalSettings = req.GlobalSettings;
        // Transitional guard: web/mobile clients built against the pre-#493 Swagger do not
        // yet send Goal/TargetWeightKg in their update payloads, so the fields arrive as
        // null. Blindly assigning would clobber a goal set at create-time or via the
        // backfill migration. Preserve the stored value whenever the caller omits the field.
        // Explicit clear-to-null will be supported once regen-api ships the updated contract.
        if (req.Goal.HasValue) plan.Goal = req.Goal;
        if (req.TargetWeightKg.HasValue) plan.TargetWeightKg = req.TargetWeightKg;
        plan.Weeks = req.Weeks.Select(rw =>
        {
            var existing = existingWeeks.GetValueOrDefault(rw.WeekNumber);
            return new PlanWeek
            {
                WeekNumber = rw.WeekNumber,
                Status = existing?.Status ?? WeekStatus.Draft,
                DatePublished = existing?.DatePublished,
                Days = rw.Days.Select(rd => new PlanDay
                {
                    DayOfWeek = rd.DayOfWeek,
                    Note = rd.Note,
                    Meals = rd.Meals.Select(rm =>
                    {
                        var mealId = ResolveMealId(rm.MealId);
                        return BuildMeal(rm, mealId, storedMeals.TryGetValue(mealId, out var stored) ? stored.Meal : null);
                    }).ToList()
                }).ToList()
            };
        }).ToList();

        KeepLockedMeals(plan, existingWeeks, lockedMeals, req.PreserveLockedMeals, storedStartDate, clientToday);

        StampChangedMeals(plan, storedMeals, plan.Version + 1);
        RecordRemovedMeals(plan, storedMeals, plan.Version + 1, DateTime.UtcNow);

        // Map supplements (full-state replace)
        plan.Supplements = req.Supplements.Select(rs => new Supplement
        {
            ExternalId = rs.ExternalId ?? Guid.NewGuid(),
            Name = rs.Name,
            Dose = rs.Dose,
            Notes = rs.Notes
        }).ToList();

        // Recalculate totals
        macroCalculator.RecalculateTotals(plan);

        // Derive plan-level status from week statuses
        plan.Status = plan.Weeks.Any(w => w.Status == WeekStatus.Published)
            ? NutritionPlanStatus.Active
            : NutritionPlanStatus.Draft;

        plan.DateUpdated = DateTime.UtcNow;
        plan.Version += 1;

        return true;
    }

    private async Task<DateOnly> ResolveClientTodayAsync(NutritionPlan plan, CancellationToken ct)
    {
        var timeZone = await db.ResolveClientTimeZoneAsync(plan.ClientId, ct);

        return ClientLocalDateResolver.ResolveLocalDate(timeProvider.GetUtcNow().UtcDateTime, timeZone);
    }

    private async Task<List<MealLog>> LoadMealLogsAsync(NutritionPlan plan, CancellationToken ct)
    {
        var cursor = await mongo.MealLogs.FindAsync(
            Builders<MealLog>.Filter.Eq(log => log.PlanId, plan.ExternalId), cancellationToken: ct);

        return await cursor.ToListAsync(ct);
    }

    /// <summary>
    /// The first way the request breaks the lock: a locked meal that is missing, moved or changed
    /// (<see cref="ErrorCodes.MealLocked"/>), or any other meal placed on a past day
    /// (<see cref="ErrorCodes.PastDayLocked"/>). Null when the request is allowed.
    /// </summary>
    private static LockViolation? FindLockViolation(
        UpdatePlanRequest req,
        DateTime? storedStartDate,
        Dictionary<Guid, PlanMealLock> lockedMeals,
        DateOnly clientToday)
    {
        var seenLockedIds = new HashSet<Guid>();

        foreach (var week in req.Weeks)
        {
            foreach (var day in week.Days)
            {
                foreach (var meal in day.Meals)
                {
                    if (meal.MealId is { } mealId
                        && lockedMeals.TryGetValue(mealId, out var mealLock)
                        && seenLockedIds.Add(mealId))
                    {
                        if (mealLock.WeekNumber != week.WeekNumber
                            || mealLock.DayOfWeek != day.DayOfWeek
                            || !HasSameLockedContent(mealLock.Meal, meal))
                        {
                            return new LockViolation(
                                ErrorCodes.MealLocked,
                                "A meal the client ate, or one on a past day, cannot be changed or moved.",
                                mealId,
                                week.WeekNumber,
                                day.DayOfWeek);
                        }

                        continue;
                    }

                    if (PlanMealLockResolver.IsPastDay(storedStartDate, week.WeekNumber, day.DayOfWeek, clientToday))
                    {
                        return new LockViolation(
                            ErrorCodes.PastDayLocked,
                            "Meals cannot be added to a day that has already passed.",
                            meal.MealId,
                            week.WeekNumber,
                            day.DayOfWeek);
                    }
                }
            }
        }

        var deleted = lockedMeals.Values.FirstOrDefault(mealLock => !seenLockedIds.Contains(mealLock.Meal.MealId));

        return deleted is null
            ? null
            : new LockViolation(
                ErrorCodes.MealLocked,
                "A meal the client ate, or one on a past day, cannot be deleted.",
                deleted.Meal.MealId,
                deleted.WeekNumber,
                deleted.DayOfWeek);
    }

    /// <summary>The first lock the request breaks, with the meal slot the web should outline.</summary>
    private readonly record struct LockViolation(
        string ErrorCode, string Message, Guid? MealId, int WeekNumber, int DayOfWeek);

    private static bool HasSameLockedContent(PlanMeal stored, UpdateMealRequest incoming) =>
        stored.Kind == incoming.Kind
        && (stored.Time ?? string.Empty) == (incoming.Time ?? string.Empty)
        && (stored.Note ?? string.Empty) == (incoming.Note ?? string.Empty)
        && stored.Foods.Select(food => (food.FoodExternalId, food.AmountGrams, Note: food.Note ?? string.Empty))
            .SequenceEqual(incoming.Foods.Select(food => (food.FoodExternalId, food.AmountGrams, Note: food.Note ?? string.Empty)))
        && stored.Recipes.Select(recipe => (recipe.RecipeId, recipe.Servings, Note: recipe.Note ?? string.Empty))
            .SequenceEqual(incoming.Recipes.Select(recipe => (recipe.RecipeId, recipe.Servings, Note: recipe.Note ?? string.Empty)));

    /// <summary>
    /// Puts the stored instance of every locked meal back into the mapped weeks. In the strict mode
    /// the request was already proven to match, so each is swapped in place. With
    /// <paramref name="preserve"/> the locked meals and every past-day meal are removed from the
    /// payload and the stored locked meals are re-inserted, recreating a week or day the payload omitted.
    /// </summary>
    private static void KeepLockedMeals(
        NutritionPlan plan,
        Dictionary<int, PlanWeek> existingWeeks,
        Dictionary<Guid, PlanMealLock> lockedMeals,
        bool preserve,
        DateTime? storedStartDate,
        DateOnly clientToday)
    {
        if (!preserve)
        {
            foreach (var meals in plan.Weeks.SelectMany(week => week.Days).Select(day => day.Meals))
            {
                for (var index = 0; index < meals.Count; index++)
                {
                    if (lockedMeals.TryGetValue(meals[index].MealId, out var mealLock))
                    {
                        meals[index] = mealLock.Meal;
                    }
                }
            }

            return;
        }

        foreach (var week in plan.Weeks)
        {
            foreach (var day in week.Days)
            {
                day.Meals.RemoveAll(meal =>
                    lockedMeals.ContainsKey(meal.MealId)
                    || PlanMealLockResolver.IsPastDay(storedStartDate, week.WeekNumber, day.DayOfWeek, clientToday));
            }
        }

        var touchedDays = new HashSet<PlanDay>();

        foreach (var mealLock in lockedMeals.Values)
        {
            var week = plan.Weeks.FirstOrDefault(candidate => candidate.WeekNumber == mealLock.WeekNumber);

            if (week is null)
            {
                var existing = existingWeeks.GetValueOrDefault(mealLock.WeekNumber);
                week = new PlanWeek
                {
                    WeekNumber = mealLock.WeekNumber,
                    Status = existing?.Status ?? WeekStatus.Draft,
                    DatePublished = existing?.DatePublished,
                    Days = []
                };
                plan.Weeks.Add(week);
            }

            var day = week.Days.FirstOrDefault(candidate => candidate.DayOfWeek == mealLock.DayOfWeek);

            if (day is null)
            {
                day = new PlanDay { DayOfWeek = mealLock.DayOfWeek };
                week.Days.Add(day);
            }

            day.Meals.Insert(0, mealLock.Meal);
            touchedDays.Add(day);
        }

        foreach (var day in touchedDays)
        {
            day.Meals = day.Meals.OrderBy(meal => meal.Order).ToList();
        }

        plan.Weeks = plan.Weeks
            .OrderBy(week => week.WeekNumber)
            .Select(week =>
            {
                week.Days = week.Days.OrderBy(day => day.DayOfWeek).ToList();
                return week;
            })
            .ToList();
    }

    /// <summary>
    /// Maps one requested meal; item ids are carried over from the stored meal with the same id
    /// (see <see cref="MealItemIdResolver"/>) so a client's eaten ticks survive the edit.
    /// </summary>
    private static PlanMeal BuildMeal(UpdateMealRequest request, Guid mealId, PlanMeal? stored)
    {
        var foodIds = MealItemIdResolver.ResolveFoodIds(
            stored, request.Foods.Select(food => (food.ItemId, food.FoodExternalId)).ToList());
        var recipeIds = MealItemIdResolver.ResolveRecipeIds(
            stored, request.Recipes.Select(recipe => (recipe.ItemId, recipe.RecipeId)).ToList());

        return new PlanMeal
        {
            MealId = mealId,
            Kind = request.Kind,
            Order = request.Order,
            Time = request.Time,
            Note = request.Note,
            Foods = request.Foods.Select((food, index) => new MealFood
            {
                ItemId = foodIds[index],
                FoodExternalId = food.FoodExternalId,
                FoodName = food.FoodName,
                FoodNameCs = food.FoodNameCs,
                FoodNameEn = food.FoodNameEn,
                FoodNameDe = food.FoodNameDe,
                FoodCategory = food.FoodCategory,
                NutrientValuePer100Grams = food.NutrientValuePer100Grams,
                AmountGrams = food.AmountGrams,
                Note = food.Note
            }).ToList(),
            Recipes = request.Recipes.Select((recipe, index) => new MealRecipe
            {
                ItemId = recipeIds[index],
                RecipeId = recipe.RecipeId,
                RecipeName = recipe.RecipeName,
                NutrientValuePerServing = recipe.NutrientValuePerServing,
                Servings = recipe.Servings,
                Note = recipe.Note,
                FoodCategories = recipe.FoodCategories
            }).ToList()
        };
    }

    /// <summary>
    /// Sets <see cref="PlanMeal.ChangedAtPlanVersion"/> to <paramref name="newPlanVersion"/> on every
    /// meal that is new or whose kind, foods, recipes or week/day position changed, and carries the
    /// stored marker over on unchanged meals.
    /// </summary>
    private static void StampChangedMeals(
        NutritionPlan plan,
        Dictionary<Guid, (PlanMeal Meal, int WeekNumber, int DayOfWeek)> storedMeals,
        int newPlanVersion)
    {
        foreach (var week in plan.Weeks)
        {
            foreach (var day in week.Days)
            {
                foreach (var meal in day.Meals)
                {
                    var unchanged = storedMeals.TryGetValue(meal.MealId, out var stored)
                        && stored.WeekNumber == week.WeekNumber
                        && stored.DayOfWeek == day.DayOfWeek
                        && stored.Meal.Kind == meal.Kind
                        && HasSameContent(stored.Meal, meal);

                    meal.ChangedAtPlanVersion = unchanged ? stored.Meal.ChangedAtPlanVersion : newPlanVersion;
                }
            }
        }
    }

    /// <summary>
    /// Keeps <see cref="NutritionPlan.RemovedMeals"/> in step with the save: meals that were stored
    /// but are gone get a record, re-added ids lose theirs, and entries past the offline window
    /// (8 days) are pruned.
    /// </summary>
    private static void RecordRemovedMeals(
        NutritionPlan plan,
        Dictionary<Guid, (PlanMeal Meal, int WeekNumber, int DayOfWeek)> storedMeals,
        int newPlanVersion,
        DateTime now)
    {
        var currentMealIds = plan.Weeks
            .SelectMany(w => w.Days)
            .SelectMany(d => d.Meals)
            .Select(m => m.MealId)
            .ToHashSet();
        var cutoff = now.AddDays(-8);

        var kept = plan.RemovedMeals
            .Where(r => r.RemovedAt >= cutoff && !currentMealIds.Contains(r.MealId))
            .ToList();
        var recordedIds = kept.Select(r => r.MealId).ToHashSet();

        foreach (var (mealId, stored) in storedMeals)
        {
            if (currentMealIds.Contains(mealId) || !recordedIds.Add(mealId))
            {
                continue;
            }

            kept.Add(new RemovedPlanMeal
            {
                MealId = mealId,
                Kind = stored.Meal.Kind,
                WeekNumber = stored.WeekNumber,
                DayOfWeek = stored.DayOfWeek,
                RemovedAtPlanVersion = newPlanVersion,
                RemovedAt = now,
            });
        }

        plan.RemovedMeals = kept;
    }

    private static bool HasSameContent(PlanMeal stored, PlanMeal incoming) =>
        stored.Foods.Select(f => (f.FoodExternalId, f.AmountGrams))
            .SequenceEqual(incoming.Foods.Select(f => (f.FoodExternalId, f.AmountGrams)))
        && stored.Recipes.Select(r => (r.RecipeId, r.Servings))
            .SequenceEqual(incoming.Recipes.Select(r => (r.RecipeId, r.Servings)));

    /// <summary>
    /// Returns the request's meal ids that already belong to another plan or to another plan's
    /// meal logs. Ids stored on this plan, and ids only on this plan's own logs, are never foreign.
    /// </summary>
    private async Task<HashSet<Guid>> FindForeignMealIdsAsync(
        NutritionPlan plan, UpdatePlanRequest req, CancellationToken ct)
    {
        var storedMealIds = plan.Weeks
            .SelectMany(w => w.Days)
            .SelectMany(d => d.Meals)
            .Select(m => m.MealId)
            .ToHashSet();

        var candidates = req.Weeks
            .SelectMany(w => w.Days)
            .SelectMany(d => d.Meals)
            .Where(m => m.MealId.HasValue && !storedMealIds.Contains(m.MealId.Value))
            .Select(m => m.MealId!.Value)
            .ToHashSet();

        var foreign = new HashSet<Guid>();

        if (candidates.Count == 0)
        {
            return foreign;
        }

        var planFilter = Builders<NutritionPlan>.Filter.Ne(p => p.ExternalId, plan.ExternalId)
                         & Builders<NutritionPlan>.Filter.ElemMatch(
                             p => p.Weeks,
                             Builders<PlanWeek>.Filter.ElemMatch(
                                 w => w.Days,
                                 Builders<PlanDay>.Filter.ElemMatch(
                                     d => d.Meals,
                                     Builders<PlanMeal>.Filter.In(m => m.MealId, candidates))));

        var otherPlans = await mongo.NutritionPlans
            .Find(planFilter)
            .Project(p => new { p.ExternalId, p.Weeks })
            .ToListAsync(ct);

        foreach (var otherPlan in otherPlans.Where(p => p.ExternalId != plan.ExternalId))
        {
            foreach (var mealId in otherPlan.Weeks
                         .SelectMany(w => w.Days)
                         .SelectMany(d => d.Meals)
                         .Select(m => m.MealId)
                         .Where(candidates.Contains))
            {
                foreign.Add(mealId);
            }
        }

        var logFilter = Builders<MealLog>.Filter.In(l => l.MealId, candidates)
                        & Builders<MealLog>.Filter.Ne(l => l.PlanId, plan.ExternalId);

        var otherLogs = await mongo.MealLogs
            .Find(logFilter)
            .Project(l => new { l.MealId, l.PlanId })
            .ToListAsync(ct);

        foreach (var log in otherLogs.Where(l => l.PlanId != plan.ExternalId && candidates.Contains(l.MealId)))
        {
            foreign.Add(log.MealId);
        }

        return foreign;
    }
}
