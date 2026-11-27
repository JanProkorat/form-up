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

namespace FormUp.Application.Features.ClientNutrition.UnlogMealItemEaten;

/// <summary>
/// Unmarks one food or recipe of a meal in the client's current plan. The meal's <c>EatenAt</c> is
/// cleared; a row left with no eaten item, photo or note is removed. An offline-synced untick (one
/// carrying <c>BasedOnPlanVersion</c>) that collides with a plan change is recorded as an
/// <see cref="OfflineSyncConflict"/> and the plan's author is notified.
/// </summary>
/// <param name="mongo">MongoDB context.</param>
/// <param name="db">Relational database context.</param>
/// <param name="notifier">Realtime notifier for pushing SignalR events.</param>
/// <param name="timeProvider">Clock.</param>
/// <param name="notifications">Creates the localized in-app notification and push for a conflict.</param>
/// <param name="coachDiscipline">Narrows link flags to the disciplines the coach still holds a role for.</param>
/// <param name="logger">Logger for the non-fatal coach notification step.</param>
internal sealed class UnlogMealItemEatenEndpoint(
    IMongoContext mongo,
    IApplicationDbContext db,
    IRealtimeNotifier notifier,
    TimeProvider timeProvider,
    INotificationService notifications,
    ICoachDisciplineAccess coachDiscipline,
    ILogger<UnlogMealItemEatenEndpoint> logger)
    : Endpoint<UnlogMealItemEatenRequest, UnlogMealItemEatenResponse>
{
    private const string OperationName = "UnlogMealItemEaten";

    /// <inheritdoc />
    public override void Configure()
    {
        Delete("/client/nutrition/log/meals/{MealId}/items/{ItemId}/eaten");
        Roles(AppRoles.Client);
        Summary(s =>
        {
            s.Summary = "Unmark one meal item as eaten";
            s.Description = "Unticks a single food or recipe of a meal in the client's current plan. Safe to replay "
                + "with the same clientRequestId.";
            s.Response<UnlogMealItemEatenResponse>(StatusCodes.Status200OK, "Eaten state of the meal after the untick");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest, "Invalid request");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or unreadable caller claim");
            s.Response(StatusCodes.Status404NotFound,
                "No client profile, or the meal is not in the client's current plan, or (errorCode MEAL_ITEM_NOT_FOUND) the item is not in the meal");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(UnlogMealItemEatenRequest req, CancellationToken ct)
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
            row = await ApplyUntickAsync(req, row, items, dayFilter, todayLocalUtc, ct);

            OfflineSyncConflict? conflict = null;

            if (req.BasedOnPlanVersion is { } basedOn && current.Meal.ChangedAtPlanVersion > basedOn)
            {
                // The client's untick was applied, so the conflict reads "applied anyway", like a tick.
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

        await Send.OkAsync(new UnlogMealItemEatenResponse
        {
            MealId = req.MealId,
            ItemId = req.ItemId,
            EatenState = state,
            EatenItemIds = currentIds.ToList(),
            EatenAt = state == MealEatenState.Eaten ? row?.EatenAt : null,
        }, ct);
    }

    /// <summary>
    /// Pulls the item from the row's eaten ids and clears <c>EatenAt</c> in one atomic update on the row found by
    /// the day filter. A row left with no eaten item, photo or note is deleted by a conditional delete; otherwise
    /// it is kept (its id is referenced by plan photos) and, when nothing is eaten, loses its snapshot by a
    /// conditional update. Returns the row as it stands afterwards, or null when it was deleted or never existed.
    /// </summary>
    private async Task<MealLog?> ApplyUntickAsync(
        UnlogMealItemEatenRequest req, MealLog? row, HashSet<Guid> items, FilterDefinition<MealLog> dayFilter,
        DateTime todayLocalUtc, CancellationToken ct)
    {
        if (row is null)
        {
            return null;
        }

        await MealItemEatenWriter.MaterialiseLegacyRowAsync(mongo, row, items, ct);

        var after = await mongo.MealLogs.FindOneAndUpdateAsync(
            dayFilter,
            Builders<MealLog>.Update
                .Pull(l => l.EatenItemIds, req.ItemId)
                .Unset(l => l.EatenAt)
                .Set(l => l.LogDate, todayLocalUtc),
            new FindOneAndUpdateOptions<MealLog> { ReturnDocument = ReturnDocument.After },
            ct);

        if (after is null)
        {
            return null;
        }

        // Nothing eaten any more: no stored id is still one of the row's items. The conditions are part of the
        // write, so a tick that lands in between keeps the row.
        if (after.EatenItemIds.Any(items.Contains))
        {
            return await MealItemEatenWriter.FindByIdAsync(mongo, after.Id, ct);
        }

        var rowFilter = Builders<MealLog>.Filter.Eq(l => l.Id, after.Id);
        var nothingEaten = Builders<MealLog>.Filter.AnyNin(l => l.EatenItemIds, items.ToList());

        var deleted = await mongo.MealLogs.DeleteOneAsync(
            Builders<MealLog>.Filter.And(
                rowFilter,
                nothingEaten,
                Builders<MealLog>.Filter.Or(
                    Builders<MealLog>.Filter.Size(l => l.Photos, 0),
                    Builders<MealLog>.Filter.Exists(l => l.Photos, false)),
                Builders<MealLog>.Filter.Eq(l => l.Note, null)),
            ct);

        if (deleted.DeletedCount > 0)
        {
            return null;
        }

        // Dropping the snapshot unlocks the meal for the coach.
        await mongo.MealLogs.UpdateOneAsync(
            Builders<MealLog>.Filter.And(rowFilter, nothingEaten),
            Builders<MealLog>.Update.Unset(l => l.EatenSnapshot),
            cancellationToken: ct);

        return await MealItemEatenWriter.FindByIdAsync(mongo, after.Id, ct);
    }

    /// <summary>
    /// Records a MealRemoved conflict (and tells the plan's author) for an offline untick on a meal that left
    /// the plan. Online requests, replays and meal ids unknown to the client record nothing.
    /// </summary>
    private async Task RecordMealRemovedAsync(
        UnlogMealItemEatenRequest req, ClientProfile clientProfile, bool alreadyApplied, DateTime now,
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
    /// Records a MealRemoved conflict (and tells the plan's author) for an offline untick on an item the coach
    /// removed from a meal that is still in the plan.
    /// </summary>
    private async Task RecordItemRemovedAsync(
        UnlogMealItemEatenRequest req, CurrentPlanMeal current, ClientProfile clientProfile, bool alreadyApplied,
        DateTime now, DateTime todayLocalUtc, CancellationToken ct)
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
        UnlogMealItemEatenRequest req, ClientProfile clientProfile, OfflineSyncConflict? conflict, CancellationToken ct)
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
