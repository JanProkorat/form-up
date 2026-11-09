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

namespace FormUp.Application.Features.ClientNutrition.LogMealItemEaten;

/// <summary>
/// Marks one food or recipe of a meal in the client's current plan as eaten. The meal's own
/// <c>EatenAt</c> is set only once every current item is eaten; until then the row stays partly eaten.
/// </summary>
/// <param name="mongo">MongoDB context.</param>
/// <param name="db">Relational database context.</param>
/// <param name="notifier">Realtime notifier for pushing SignalR events.</param>
/// <param name="timeProvider">Clock.</param>
internal sealed class LogMealItemEatenEndpoint(
    IMongoContext mongo,
    IApplicationDbContext db,
    IRealtimeNotifier notifier,
    TimeProvider timeProvider)
    : Endpoint<LogMealItemEatenRequest, LogMealItemEatenResponse>
{
    private const string OperationName = "LogMealItemEaten";

    /// <inheritdoc />
    public override void Configure()
    {
        Post("/client/nutrition/log/meals/{MealId}/items/{ItemId}/eaten");
        Roles(AppRoles.Client);
        Summary(s =>
        {
            s.Summary = "Mark one meal item as eaten";
            s.Description = "Ticks a single food or recipe of a meal in the client's current plan. The meal counts as "
                + "eaten once every item is ticked. Safe to replay with the same clientRequestId.";
            s.Response<LogMealItemEatenResponse>(StatusCodes.Status200OK, "Eaten state of the meal after the tick");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest, "Invalid request");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or unreadable caller claim");
            s.Response(StatusCodes.Status404NotFound,
                "No client profile, or the meal is not in the client's current plan, or (errorCode MEAL_ITEM_NOT_FOUND) the item is not in the meal");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(LogMealItemEatenRequest req, CancellationToken ct)
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
        var alreadyApplied = await ClientRequestDeduplicator.FindAsync(mongo, clientId, req.ClientRequestId, ct) is not null;

        var now = req.OccurredAt?.UtcDateTime ?? timeProvider.GetUtcNow().UtcDateTime;
        var (todayLocalUtc, windowStartUtc, windowEndUtc) = await db.ResolveClientLocalDayWindowAsync(clientId, now, ct);

        var meal = await FindCurrentPlanMealAsync(clientId, req.MealId, todayLocalUtc, ct);

        if (meal is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var row = await FindRowAsync(clientId, meal.Plan.ExternalId, req.MealId, todayLocalUtc, windowStartUtc, windowEndUtc, ct);

        // A row with a snapshot is judged against the items it was eaten from, not the live meal.
        var items = row is null ? MealItemEatenState.ItemIds(meal.Meal) : MealItemEatenState.ItemIds(row, meal.Meal);

        if (!items.Contains(req.ItemId))
        {
            await this.SendProblemAsync(404, ErrorCodes.MealItemNotFound, "The item is not part of this meal.", ct);
            return;
        }

        if (!alreadyApplied)
        {
            var eatenIds = row is null ? [] : MealItemEatenState.EffectiveEatenIds(row, meal.Meal);
            var hadEatenItems = eatenIds.Count > 0;
            eatenIds.Add(req.ItemId);
            row = await WriteRowAsync(
                req, meal, row, eatenIds, items.IsSubsetOf(eatenIds), !hadEatenItems, now, todayLocalUtc, ct);

            await ClientRequestDeduplicator.RecordAsync(
                mongo, clientId, req.ClientRequestId, OperationName, req.BasedOnPlanVersion, null,
                timeProvider.GetUtcNow().UtcDateTime, ct);

            await NotifyLinkedProfessionalsAsync(clientProfile.Id, clientProfile.PublicId, ct);
        }

        var currentIds = row is null ? [] : MealItemEatenState.EffectiveEatenIds(row, meal.Meal);
        var state = MealItemEatenState.StateOf(items, currentIds, row?.EatenAt is not null);

        await Send.OkAsync(new LogMealItemEatenResponse
        {
            MealId = req.MealId,
            ItemId = req.ItemId,
            EatenState = state,
            EatenItemIds = currentIds.ToList(),
            EatenAt = state == MealEatenState.Eaten ? row?.EatenAt : null,
        }, ct);
    }

    /// <summary>
    /// Resolves the Active plan whose date window contains today (a client may hold several
    /// sequential plans, #780) and the meal inside it; null when either is missing.
    /// </summary>
    private async Task<PlanAndMeal?> FindCurrentPlanMealAsync(
        Guid clientId, Guid mealId, DateTime todayLocalUtc, CancellationToken ct)
    {
        var filter = Builders<NutritionPlan>.Filter.And(
            Builders<NutritionPlan>.Filter.Eq(p => p.ClientId, clientId),
            Builders<NutritionPlan>.Filter.Eq(p => p.Status, NutritionPlanStatus.Active));

        using var cursor = await mongo.NutritionPlans.FindAsync(filter, cancellationToken: ct);
        var activePlans = await cursor.ToListAsync(ct);
        var plan = PlanWindowResolver.ResolveCurrentPlan(activePlans, p => p.StartDate, p => p.Weeks.Count, todayLocalUtc);

        var meal = plan?.Weeks
            .SelectMany(w => w.Days)
            .SelectMany(d => d.Meals)
            .FirstOrDefault(m => m.MealId == mealId);

        return plan is null || meal is null ? null : new PlanAndMeal(plan, meal);
    }

    /// <summary>
    /// Finds today's row of the meal — the same lookup as marking the whole meal, so one meal has one row per day.
    /// </summary>
    private async Task<MealLog?> FindRowAsync(
        Guid clientId, Guid planId, Guid mealId, DateTime todayLocalUtc, DateTime windowStartUtc, DateTime windowEndUtc,
        CancellationToken ct)
    {
        var filter = Builders<MealLog>.Filter.And(
            Builders<MealLog>.Filter.Eq(l => l.ClientId, clientId),
            Builders<MealLog>.Filter.Eq(l => l.PlanId, planId),
            Builders<MealLog>.Filter.Eq(l => l.MealId, mealId),
            Builders<MealLog>.Filter.Or(
                Builders<MealLog>.Filter.Eq(l => l.LogDate, todayLocalUtc),
                Builders<MealLog>.Filter.And(
                    Builders<MealLog>.Filter.Gte(l => l.EatenAt, windowStartUtc),
                    Builders<MealLog>.Filter.Lt(l => l.EatenAt, windowEndUtc))));

        using var cursor = await mongo.MealLogs.FindAsync(filter, cancellationToken: ct);
        return await cursor.FirstOrDefaultAsync(ct);
    }

    private async Task<MealLog> WriteRowAsync(
        LogMealItemEatenRequest req, PlanAndMeal meal, MealLog? row, HashSet<Guid> eatenIds, bool everyItemEaten,
        bool firstEatenItem, DateTime now, DateTime todayLocalUtc, CancellationToken ct)
    {
        if (row is null)
        {
            var created = new MealLog
            {
                EatenSnapshot = MealItemEatenState.BuildSnapshot(meal.Meal),
                ClientId = meal.Plan.ClientId,
                PlanId = meal.Plan.ExternalId,
                MealId = req.MealId,
                LogDate = todayLocalUtc,
                EatenAt = everyItemEaten ? now : null,
                EatenItemIds = eatenIds.ToList(),
                FoodsEaten = meal.Meal.Foods,
                BasedOnPlanVersion = req.BasedOnPlanVersion,
            };

            await mongo.MealLogs.InsertOneAsync(created, cancellationToken: ct);
            return created;
        }

        var update = Builders<MealLog>.Update
            .Set(l => l.EatenItemIds, eatenIds.ToList())
            .Set(l => l.LogDate, todayLocalUtc)
            .Set(l => l.FoodsEaten, meal.Meal.Foods);

        // The snapshot is written once, when the row goes from nothing eaten to something eaten, and
        // never overwritten by later ticks.
        if (firstEatenItem && row.EatenSnapshot is null)
        {
            row.EatenSnapshot = MealItemEatenState.BuildSnapshot(meal.Meal);
            update = update.Set(l => l.EatenSnapshot, row.EatenSnapshot);
        }

        if (everyItemEaten)
        {
            row.EatenAt ??= now;
            update = update.Set(l => l.EatenAt, row.EatenAt);
        }
        else
        {
            row.EatenAt = null;
            update = update.Unset(l => l.EatenAt);
        }

        if (req.BasedOnPlanVersion is not null)
        {
            update = update.Set(l => l.BasedOnPlanVersion, req.BasedOnPlanVersion);
        }

        row.EatenItemIds = eatenIds.ToList();

        await mongo.MealLogs.UpdateOneAsync(Builders<MealLog>.Filter.Eq(l => l.Id, row.Id), update, cancellationToken: ct);
        return row;
    }

    /// <summary>
    /// Pushes a <c>clientcomplianceupdated</c> SignalR event to every active professional linked to
    /// this client so their dashboards can refresh without polling. The payload's ClientId is the
    /// trainer-facing ClientProfile.PublicId.
    /// </summary>
    private async Task NotifyLinkedProfessionalsAsync(long clientProfileId, Guid clientPublicId, CancellationToken ct)
    {
        var professionalUserIds = await db.ClientProfessionalLinks
            .AsNoTracking()
            .Where(l => l.ClientProfileId == clientProfileId && l.ConnectionState != ConnectionState.Ended)
            .Join(db.ProfessionalProfiles.AsNoTracking(),
                link => link.ProfessionalProfileId,
                prof => prof.Id,
                (_, prof) => prof.UserId)
            .Distinct()
            .ToListAsync(ct);

        var payload = new { ClientId = clientPublicId };

        foreach (var professionalUserId in professionalUserIds)
        {
            await notifier.NotifyAsync(professionalUserId, "clientcomplianceupdated", payload, ct);
        }
    }

    private sealed record PlanAndMeal(NutritionPlan Plan, PlanMeal Meal);
}
