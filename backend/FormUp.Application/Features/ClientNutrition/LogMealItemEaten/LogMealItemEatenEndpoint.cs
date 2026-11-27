using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Application.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using MongoDB.Driver;

namespace FormUp.Application.Features.ClientNutrition.LogMealItemEaten;

/// <summary>
/// Marks one food or recipe of a meal in the client's current plan as eaten. The meal's own
/// <c>EatenAt</c> is set only once every current item is eaten; until then the row stays partly eaten.
/// An offline-synced tick (one carrying <c>BasedOnPlanVersion</c>) that collides with a plan change is
/// recorded as an <see cref="OfflineSyncConflict"/> and the plan's author is notified.
/// </summary>
/// <param name="mongo">MongoDB context.</param>
/// <param name="db">Relational database context.</param>
/// <param name="notifier">Realtime notifier for pushing SignalR events.</param>
/// <param name="timeProvider">Clock.</param>
/// <param name="notifications">Creates the localized in-app notification and push for a conflict.</param>
/// <param name="coachDiscipline">Narrows link flags to the disciplines the coach still holds a role for.</param>
/// <param name="logger">Logger for the non-fatal coach notification step.</param>
internal sealed class LogMealItemEatenEndpoint(
    IMongoContext mongo,
    IApplicationDbContext db,
    IRealtimeNotifier notifier,
    TimeProvider timeProvider,
    INotificationService notifications,
    ICoachDisciplineAccess coachDiscipline,
    ILogger<LogMealItemEatenEndpoint> logger)
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

        var current = await CurrentPlanMealResolver.FindCurrentPlanMealAsync(mongo, clientId, req.MealId, todayLocalUtc, ct);

        if (current is null)
        {
            await RecordMealRemovedAsync(req, clientProfile, alreadyApplied, now, todayLocalUtc, ct);
            await Send.NotFoundAsync(ct);
            return;
        }

        var dayFilter = MealLogDayFilter.Build(
            clientId, current.Plan.ExternalId, req.MealId, todayLocalUtc, windowStartUtc, windowEndUtc);
        using var rowCursor = await mongo.MealLogs.FindAsync(dayFilter, cancellationToken: ct);
        var row = await rowCursor.FirstOrDefaultAsync(ct);

        // A row with a snapshot is judged against the items it was eaten from, not the live meal.
        var items = row is null ? MealItemEatenState.ItemIds(current.Meal) : MealItemEatenState.ItemIds(row, current.Meal);

        if (!items.Contains(req.ItemId))
        {
            await RecordItemRemovedAsync(req, current, clientProfile, alreadyApplied, now, todayLocalUtc, ct);
            await this.SendProblemAsync(404, ErrorCodes.MealItemNotFound, "The item is not part of this meal.", ct);
            return;
        }

        if (!alreadyApplied)
        {
            row = await ApplyTickAsync(req, current, row, items, dayFilter, now, todayLocalUtc, ct);

            OfflineSyncConflict? conflict = null;

            if (req.BasedOnPlanVersion is { } basedOn && current.Meal.ChangedAtPlanVersion > basedOn)
            {
                conflict = await OfflineSyncConflictRecorder.RecordForPlanMealAsync(
                    mongo, current, OperationName, basedOn, clientId, OfflineSyncConflictKind.PlanChanged,
                    mealLogged: true, now, todayLocalUtc, timeProvider.GetUtcNow().UtcDateTime, ct);
            }

            await ClientRequestDeduplicator.RecordAsync(
                mongo, clientId, req.ClientRequestId, OperationName, req.BasedOnPlanVersion, conflict?.ExternalId,
                timeProvider.GetUtcNow().UtcDateTime, ct);

            if (conflict is not null)
            {
                await OfflineSyncConflictRecorder.NotifyCoachesAsync(
                    db, mongo, notifications, notifier, coachDiscipline, logger, clientProfile, conflict, ct);
            }

            await ClientComplianceNotifier.NotifyLinkedProfessionalsAsync(
                db, notifier, clientProfile.Id, clientProfile.PublicId, ct);
        }

        var currentIds = row is null ? [] : MealItemEatenState.EffectiveEatenIds(row, current.Meal);
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
    /// Adds the item to the day's row with atomic updates: <c>$addToSet</c> on the row found by the day filter,
    /// else an upsert on the row's natural key. The snapshot and <c>EatenAt</c> are then set or cleared by
    /// conditional updates, so a concurrent tick or untick on the same meal cannot lose an id or add a second row.
    /// Returns the row as it stands afterwards (null only if a concurrent untick deleted it again).
    /// </summary>
    private async Task<MealLog?> ApplyTickAsync(
        LogMealItemEatenRequest req, CurrentPlanMeal current, MealLog? row, HashSet<Guid> items,
        FilterDefinition<MealLog> dayFilter, DateTime now, DateTime todayLocalUtc, CancellationToken ct)
    {
        if (row is not null)
        {
            await MealItemEatenWriter.MaterialiseLegacyRowAsync(mongo, row, items, ct);
        }

        var hadEatenItems = row is not null && MealItemEatenState.EffectiveEatenIds(row, current.Meal).Count > 0;
        var after = await AddItemAsync(req, current, dayFilter, todayLocalUtc, ct);
        var rowFilter = Builders<MealLog>.Filter.Eq(l => l.Id, after.Id);

        // The snapshot is written once, when the row goes from nothing eaten to something eaten, and
        // never overwritten by later ticks. A row holding only this item counts as that moment even when the
        // row read earlier showed other items, because a racing untick may have emptied it meanwhile. The
        // filter keeps that untick's cleanup from being undone.
        if ((!hadEatenItems || after.EatenItemIds.Count == 1) && after.EatenSnapshot is null)
        {
            await mongo.MealLogs.UpdateOneAsync(
                Builders<MealLog>.Filter.And(
                    rowFilter,
                    Builders<MealLog>.Filter.Exists(l => l.EatenSnapshot, false),
                    Builders<MealLog>.Filter.Not(Builders<MealLog>.Filter.Size(l => l.EatenItemIds, 0))),
                Builders<MealLog>.Update.Set(l => l.EatenSnapshot, MealItemEatenState.BuildSnapshot(current.Meal)),
                cancellationToken: ct);
        }

        // An item tick always has at least one item (an unknown item answers 404 earlier); Filter.All with an
        // empty list would match nothing.
        var allItems = items.ToList();
        var allEaten = Builders<MealLog>.Filter.All(l => l.EatenItemIds, allItems);

        await mongo.MealLogs.UpdateOneAsync(
            Builders<MealLog>.Filter.And(rowFilter, Builders<MealLog>.Filter.Exists(l => l.EatenAt, false), allEaten),
            Builders<MealLog>.Update.Set(l => l.EatenAt, now),
            cancellationToken: ct);

        await mongo.MealLogs.UpdateOneAsync(
            Builders<MealLog>.Filter.And(
                rowFilter, Builders<MealLog>.Filter.Exists(l => l.EatenAt, true), Builders<MealLog>.Filter.Not(allEaten)),
            Builders<MealLog>.Update.Unset(l => l.EatenAt),
            cancellationToken: ct);

        return await MealItemEatenWriter.FindByIdAsync(mongo, after.Id, ct);
    }

    private async Task<MealLog> AddItemAsync(
        LogMealItemEatenRequest req, CurrentPlanMeal current, FilterDefinition<MealLog> dayFilter,
        DateTime todayLocalUtc, CancellationToken ct)
    {
        var update = Builders<MealLog>.Update
            .AddToSet(l => l.EatenItemIds, req.ItemId)
            .Set(l => l.LogDate, todayLocalUtc)
            .Set(l => l.FoodsEaten, current.Meal.Foods);

        if (req.BasedOnPlanVersion is not null)
        {
            update = update.Set(l => l.BasedOnPlanVersion, req.BasedOnPlanVersion);
        }

        // The natural key of a row. The partial unique index on it makes a racing insert fail loudly.
        var naturalKey = Builders<MealLog>.Filter.And(
            Builders<MealLog>.Filter.Eq(l => l.ClientId, current.Plan.ClientId),
            Builders<MealLog>.Filter.Eq(l => l.PlanId, current.Plan.ExternalId),
            Builders<MealLog>.Filter.Eq(l => l.MealId, req.MealId),
            Builders<MealLog>.Filter.Eq(l => l.LogDate, todayLocalUtc));

        var upsert = update.SetOnInsert(l => l.Photos, new List<MealPhoto>());
        var matchOptions = new FindOneAndUpdateOptions<MealLog> { ReturnDocument = ReturnDocument.After };

        for (var attempt = 0; attempt < 5; attempt++)
        {
            // Matched by the day filter, never by a previously read _id: a concurrent untick may have deleted
            // the row meanwhile, in which case this returns null and the upsert below recreates it.
            var matched = await mongo.MealLogs.FindOneAndUpdateAsync(dayFilter, update, matchOptions, ct);

            if (matched is not null)
            {
                return matched;
            }

            try
            {
                await mongo.MealLogs.UpdateOneAsync(
                    naturalKey, upsert, new UpdateOptions { IsUpsert = true }, ct);
            }
            catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
            {
                // A concurrent request inserted the row between the match and the upsert; match it again.
                continue;
            }

            using var cursor = await mongo.MealLogs.FindAsync(naturalKey, cancellationToken: ct);
            var created = await cursor.FirstOrDefaultAsync(ct);

            if (created is not null)
            {
                return created;
            }
        }

        throw new InvalidOperationException("The meal log row kept disappearing while an item was being ticked.");
    }

    /// <summary>
    /// Records a MealRemoved conflict (and tells the plan's author) for an offline tick on a meal that left
    /// the plan. Online requests, replays and meal ids unknown to the client record nothing.
    /// </summary>
    private async Task RecordMealRemovedAsync(
        LogMealItemEatenRequest req, ClientProfile clientProfile, bool alreadyApplied, DateTime now,
        DateTime todayLocalUtc, CancellationToken ct)
    {
        if (alreadyApplied || req.BasedOnPlanVersion is not { } basedOn)
        {
            return;
        }

        var conflict = await OfflineSyncConflictRecorder.RecordMealRemovedAsync(
            mongo, clientProfile.UserId, req.MealId, OperationName, basedOn, now, todayLocalUtc,
            timeProvider.GetUtcNow().UtcDateTime, ct);

        await FinishConflictAsync(req, clientProfile, conflict, ct);
    }

    /// <summary>
    /// Records a MealRemoved conflict (and tells the plan's author) for an offline tick on an item the coach
    /// removed from a meal that is still in the plan.
    /// </summary>
    private async Task RecordItemRemovedAsync(
        LogMealItemEatenRequest req, CurrentPlanMeal current, ClientProfile clientProfile,
        bool alreadyApplied, DateTime now, DateTime todayLocalUtc, CancellationToken ct)
    {
        if (alreadyApplied || req.BasedOnPlanVersion is not { } basedOn)
        {
            return;
        }

        var conflict = await OfflineSyncConflictRecorder.RecordForPlanMealAsync(
            mongo, current, OperationName, basedOn, clientProfile.UserId, OfflineSyncConflictKind.MealRemoved,
            mealLogged: false, now, todayLocalUtc, timeProvider.GetUtcNow().UtcDateTime, ct);

        await FinishConflictAsync(req, clientProfile, conflict, ct);
    }

    private async Task FinishConflictAsync(
        LogMealItemEatenRequest req, ClientProfile clientProfile, OfflineSyncConflict? conflict,
        CancellationToken ct)
    {
        if (conflict is null)
        {
            return;
        }

        // Recorded so a replay of this request creates no second conflict and no second notification.
        await ClientRequestDeduplicator.RecordAsync(
            mongo, clientProfile.UserId, req.ClientRequestId, OperationName, req.BasedOnPlanVersion, conflict.ExternalId,
            timeProvider.GetUtcNow().UtcDateTime, ct);

        await OfflineSyncConflictRecorder.NotifyCoachesAsync(
            db, mongo, notifications, notifier, coachDiscipline, logger, clientProfile, conflict, ct);
    }
}
