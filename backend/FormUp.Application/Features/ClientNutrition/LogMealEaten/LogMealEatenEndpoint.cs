using System.Security.Claims;
using System.Text.Json;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.ClientNutrition.Shared;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Application.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using MongoDB.Driver;

namespace FormUp.Application.Features.ClientNutrition.LogMealEaten;

/// <summary>
/// Endpoint for logging a meal from the client's active plan as eaten.
/// Creates a <see cref="MealLog"/> document with a snapshot of the meal's foods and every item marked eaten. An offline-synced
/// request (one carrying <c>BasedOnPlanVersion</c>) that collides with a plan change is still applied
/// and recorded as an <see cref="OfflineSyncConflict"/>, and the plan's author is notified.
/// </summary>
/// <param name="mongo">MongoDB context.</param>
/// <param name="db">Relational database context.</param>
/// <param name="notifier">Realtime notifier for pushing SignalR events.</param>
/// <param name="timeProvider">Clock abstraction (#955) — lets tests pin the "now" instant deterministically.</param>
/// <param name="notifications">Creates the localized in-app notification and push for a conflict.</param>
/// <param name="coachDiscipline">Narrows link flags to the disciplines the coach still holds a role for.</param>
/// <param name="logger">Logger for the non-fatal coach notification step.</param>
public class LogMealEatenEndpoint(
    IMongoContext mongo,
    IApplicationDbContext db,
    IRealtimeNotifier notifier,
    TimeProvider timeProvider,
    INotificationService notifications,
    ICoachDisciplineAccess coachDiscipline,
    ILogger<LogMealEatenEndpoint> logger) : Endpoint<LogMealEatenRequest, LogMealEatenResponse>
{
    private const string OperationName = "LogMealEaten";

    /// <inheritdoc />
    public override void Configure()
    {
        Post("/client/nutrition/log/meals/{MealId}/eaten");
        Roles(AppRoles.Client);
        Description(b => b.ClearDefaultProduces(StatusCodes.Status200OK)
            .Produces<LogMealEatenResponse>(StatusCodes.Status201Created));
        Summary(s =>
        {
            s.Summary = "Log a meal as eaten";
            s.Description = "Records that the client has eaten a specific meal from their active nutrition plan. "
                + "An offline-synced request whose plan snapshot is stale is still recorded and flagged in the body.";
            s.Responses[StatusCodes.Status201Created] = "Meal logged; syncConflict flags a collision with a plan change; a meal that left the plan is still logged from the items the client saw, and mealLogged is false only when it left the plan and the request carried no seen items";
            s.Responses[StatusCodes.Status400BadRequest] = "Invalid request";
            s.Responses[StatusCodes.Status401Unauthorized] = "Missing or unreadable caller claim";
            s.Responses[StatusCodes.Status404NotFound] = "No such meal in the client's plans";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(LogMealEatenRequest req, CancellationToken ct)
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

        var processed = await ClientRequestDeduplicator.FindAsync(mongo, clientId, req.ClientRequestId, ct);

        if (processed is not null)
        {
            await Send.ResponseAsync(await RebuildReplayAsync(processed, ct), 201, ct);
            return;
        }

        // OccurredAt (offline sync) anchors the local day, plan resolution and EatenAt; without it
        // the server clock is used.
        var now = req.OccurredAt?.UtcDateTime ?? timeProvider.GetUtcNow().UtcDateTime;
        var (todayLocalUtc, windowStartUtc, windowEndUtc) = await db.ResolveClientLocalDayWindowAsync(clientId, now, ct);

        // Resolve the Active plan whose date window contains today — a client may hold several
        // sequential, non-overlapping Active plans (#780).
        var filter = Builders<NutritionPlan>.Filter.And(
            Builders<NutritionPlan>.Filter.Eq(p => p.ClientId, clientId),
            Builders<NutritionPlan>.Filter.Eq(p => p.Status, NutritionPlanStatus.Active));

        var cursor = await mongo.NutritionPlans.FindAsync(filter, cancellationToken: ct);
        var activePlans = await cursor.ToListAsync(ct);
        var plan = PlanWindowResolver.ResolveCurrentPlan(activePlans, p => p.StartDate, p => p.Weeks.Count, todayLocalUtc);

        var meal = plan is null ? null : FindMeal(plan, req.MealId);
        OfflineSyncConflict? conflict = null;

        if (plan is null || meal is null)
        {
            // Only an offline-synced request may be answered for a meal that left the plan, and only
            // when the meal is provably this client's own; an unknown id stays 404.
            var removed = req.BasedOnPlanVersion is null
                ? null
                : await FindRemovedMealAsync(clientId, req.MealId, ct);

            if (removed is null)
            {
                await Send.NotFoundAsync(ct);
                return;
            }

            // What the client ate is what it saw; with no seen items there is nothing to snapshot, so
            // no log is written and only the conflict records the action.
            var logRemovedMeal = req.SeenMealItems is { Count: > 0 };

            if (logRemovedMeal)
            {
                await WriteMealLogAsync(
                    req, removed.Value.Plan.ExternalId, removed.Value.Kind, null, clientId, now, todayLocalUtc,
                    windowStartUtc, windowEndUtc, ct);
            }

            conflict = NewConflict(
                removed.Value.Plan, removed.Value.Kind, removed.Value.ChangedAtPlanVersion, req, clientId,
                OfflineSyncConflictKind.MealRemoved, logRemovedMeal, now, todayLocalUtc);
        }
        else
        {
            await WriteMealLogAsync(
                req, plan.ExternalId, meal.Kind, meal, clientId, now, todayLocalUtc, windowStartUtc, windowEndUtc, ct);

            if (req.BasedOnPlanVersion is { } basedOn && meal.ChangedAtPlanVersion > basedOn)
            {
                conflict = NewConflict(
                    plan, meal.Kind, meal.ChangedAtPlanVersion, req, clientId,
                    OfflineSyncConflictKind.PlanChanged, mealLogged: true, now, todayLocalUtc);
            }
        }

        if (conflict is not null)
        {
            await mongo.OfflineSyncConflicts.InsertOneAsync(conflict, cancellationToken: ct);
        }

        await ClientRequestDeduplicator.RecordAsync(
            mongo, clientId, req.ClientRequestId, OperationName, req.BasedOnPlanVersion, conflict?.ExternalId,
            timeProvider.GetUtcNow().UtcDateTime, ct);

        if (conflict is not null)
        {
            await NotifyCoachesOfConflictAsync(clientProfile, conflict, ct);
        }

        // The SignalR payload's ClientId is the trainer-facing ClientProfile.PublicId
        // convention (unrelated to the Mongo document clientId key migrated in #840) —
        // pass clientProfile.PublicId explicitly rather than the (now UserId-valued) clientId.
        await NotifyLinkedProfessionalsAsync(clientProfile.Id, clientProfile.PublicId, ct);

        await Send.ResponseAsync(LogMealEatenResponse.FromConflict(conflict?.Kind, conflict?.MealLogged ?? true), 201, ct);
    }

    private static EatenMealItem ToEatenItem(SeenMealItemRequest item) => new()
    {
        ItemId = item.ItemId,
        Kind = item.Kind,
        RefId = item.RefId,
        Name = item.Name,
        Amount = item.Amount,
        Kcal = item.Kcal,
        Protein = item.Protein,
        Carbs = item.Carbs,
        Fat = item.Fat,
        Fibre = item.Fibre,
    };

    private static PlanMeal? FindMeal(NutritionPlan plan, Guid mealId) =>
        plan.Weeks
            .SelectMany(w => w.Days)
            .SelectMany(d => d.Meals)
            .FirstOrDefault(m => m.MealId == mealId);

    private OfflineSyncConflict NewConflict(
        NutritionPlan plan, MealKind mealKind, int? mealChangedAtPlanVersion, LogMealEatenRequest req, Guid clientId,
        OfflineSyncConflictKind kind, bool mealLogged, DateTime occurredAt, DateTime logDate) => new()
    {
        ExternalId = Guid.NewGuid(),
        ClientId = clientId,
        PlanId = plan.ExternalId,
        PlanAuthorId = plan.NutritionistId,
        Operation = OperationName,
        Kind = kind,
        MealId = req.MealId,
        OccurredAt = occurredAt,
        LogDate = logDate,
        BasedOnPlanVersion = req.BasedOnPlanVersion!.Value,
        CurrentPlanVersion = plan.Version,
        MealChangedAtPlanVersion = mealChangedAtPlanVersion,
        PlanName = plan.Name,
        MealKind = mealKind,
        MealLogged = mealLogged,
        DateCreated = timeProvider.GetUtcNow().UtcDateTime,
        SeenItems = (req.SeenMealItems ?? [])
            .Select(item => new SeenMealItem
            {
                Kind = item.Kind,
                RefId = item.RefId,
                Name = item.Name,
                Amount = item.Amount,
                Kcal = item.Kcal,
                ItemId = item.ItemId,
                Protein = item.Protein,
                Carbs = item.Carbs,
                Fat = item.Fat,
                Fibre = item.Fibre,
            })
            .ToList(),
    };

    /// <summary>
    /// Finds where a meal that is no longer in the current plan went: a plan of the client's own
    /// (any status) that still contains it, or that recorded its removal in
    /// <see cref="NutritionPlan.RemovedMeals"/>. Null when the id is unknown to the client.
    /// </summary>
    private async Task<(NutritionPlan Plan, MealKind Kind, int? ChangedAtPlanVersion)?> FindRemovedMealAsync(
        Guid clientId, Guid mealId, CancellationToken ct)
    {
        var containing = await FindOwnPlanContainingMealAsync(clientId, mealId, ct);

        if (containing is not null)
        {
            var meal = FindMeal(containing, mealId)!;
            return (containing, meal.Kind, meal.ChangedAtPlanVersion);
        }

        var recordFilter = Builders<NutritionPlan>.Filter.Eq(p => p.ClientId, clientId)
                           & Builders<NutritionPlan>.Filter.ElemMatch(
                               p => p.RemovedMeals, Builders<RemovedPlanMeal>.Filter.Eq(r => r.MealId, mealId));

        using var cursor = await mongo.NutritionPlans.FindAsync(recordFilter, cancellationToken: ct);
        var candidates = await cursor.ToListAsync(ct);

        foreach (var candidate in candidates.Where(p => p.ClientId == clientId))
        {
            var record = candidate.RemovedMeals.FirstOrDefault(r => r.MealId == mealId);

            if (record is not null)
            {
                return (candidate, record.Kind, record.RemovedAtPlanVersion);
            }
        }

        return null;
    }

    /// <summary>
    /// Finds any of the client's own plans (any status) that contains the meal, or null.
    /// </summary>
    private async Task<NutritionPlan?> FindOwnPlanContainingMealAsync(Guid clientId, Guid mealId, CancellationToken ct)
    {
        var filter = Builders<NutritionPlan>.Filter.Eq(p => p.ClientId, clientId)
                     & Builders<NutritionPlan>.Filter.ElemMatch(
                         p => p.Weeks,
                         Builders<PlanWeek>.Filter.ElemMatch(
                             w => w.Days,
                             Builders<PlanDay>.Filter.ElemMatch(
                                 d => d.Meals,
                                 Builders<PlanMeal>.Filter.Eq(m => m.MealId, mealId))));

        using var cursor = await mongo.NutritionPlans.FindAsync(filter, cancellationToken: ct);
        var candidates = await cursor.ToListAsync(ct);

        return candidates.FirstOrDefault(p => p.ClientId == clientId && FindMeal(p, mealId) is not null);
    }

    /// <summary>
    /// Rebuilds the original response of a replayed request from its dedupe record.
    /// </summary>
    private async Task<LogMealEatenResponse> RebuildReplayAsync(ProcessedClientRequest processed, CancellationToken ct)
    {
        if (processed.ResultId is not { } conflictId)
        {
            return LogMealEatenResponse.FromConflict(null, mealLogged: true);
        }

        using var cursor = await mongo.OfflineSyncConflicts.FindAsync(
            Builders<OfflineSyncConflict>.Filter.Eq(c => c.ExternalId, conflictId), cancellationToken: ct);
        var conflict = await cursor.FirstOrDefaultAsync(ct);

        return conflict is null
            ? LogMealEatenResponse.FromConflict(null, mealLogged: true)
            : LogMealEatenResponse.FromConflict(conflict.Kind, conflict.MealLogged);
    }

    /// <summary>
    /// Writes or revives the day's log row for the meal. <paramref name="meal"/> is the live plan meal,
    /// or null for a meal that has left the plan (the request's seen items are then the only source).
    /// </summary>
    private async Task WriteMealLogAsync(
        LogMealEatenRequest req, Guid planId, MealKind mealKind, PlanMeal? meal, Guid clientId, DateTime now,
        DateTime todayLocalUtc, DateTime windowStartUtc, DateTime windowEndUtc, CancellationToken ct)
    {
        // Same lookup as SaveMealPhotos: a photo-only row kept by an unmark (or created by a
        // photo save) is revived instead of duplicated, so one meal has one row per day.
        var existingFilter = Builders<MealLog>.Filter.And(
            Builders<MealLog>.Filter.Eq(l => l.ClientId, clientId),
            Builders<MealLog>.Filter.Eq(l => l.PlanId, planId),
            Builders<MealLog>.Filter.Eq(l => l.MealId, req.MealId),
            Builders<MealLog>.Filter.Or(
                Builders<MealLog>.Filter.Eq(l => l.LogDate, todayLocalUtc),
                Builders<MealLog>.Filter.And(
                    Builders<MealLog>.Filter.Gte(l => l.EatenAt, windowStartUtc),
                    Builders<MealLog>.Filter.Lt(l => l.EatenAt, windowEndUtc))));

        using var existingCursor = await mongo.MealLogs.FindAsync(existingFilter, cancellationToken: ct);
        var existingLog = await existingCursor.FirstOrDefaultAsync(ct);

        var note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim();

        // An offline request carries the items the client saw; they are what was eaten, even when the
        // live plan has moved on. A request without them (online, or a legacy queue) snapshots the live
        // meal and keeps its foods in FoodsEaten.
        var hasSeenItems = req.SeenMealItems is { Count: > 0 };
        var snapshot = hasSeenItems
            ? req.SeenMealItems!.Select(ToEatenItem).ToList()
            : meal is null ? [] : MealItemEatenState.BuildSnapshot(meal);
        List<MealFood> foodsEaten = hasSeenItems || meal is null ? [] : meal.Foods;

        if (existingLog is null)
        {
            var mealLog = new MealLog
            {
                ClientId = clientId,
                PlanId = planId,
                MealId = req.MealId,
                MealKind = mealKind,
                // LogDate is the CLIENT's local calendar day (#935) — todayLocalUtc, not now.Date
                // (the server's UTC day), so a meal logged near local midnight is filed under the
                // day the client's own Today card shows. EatenAt stays the true UTC instant.
                LogDate = todayLocalUtc,
                EatenAt = now,
                EatenItemIds = snapshot.Select(item => item.ItemId).ToList(),
                EatenSnapshot = snapshot,
                FoodsEaten = foodsEaten,
                Photos = (req.PhotoBlobUrls ?? [])
                    .Select(url => new MealPhoto { BlobUrl = url, UploadedAt = now })
                    .ToList(),
                Note = note,
                BasedOnPlanVersion = req.BasedOnPlanVersion
            };

            await mongo.MealLogs.InsertOneAsync(mealLog, cancellationToken: ct);
            return;
        }

        var knownUrls = existingLog.Photos.Select(p => p.BlobUrl).ToHashSet();
        var photos = existingLog.Photos.ToList();
        foreach (var url in req.PhotoBlobUrls ?? [])
        {
            if (knownUrls.Add(url))
            {
                photos.Add(new MealPhoto { BlobUrl = url, UploadedAt = now });
            }
        }

        // A snapshot is written once, when the row goes from nothing eaten to something eaten; an
        // existing snapshot (or a legacy row that already has eaten items) is kept as it is.
        var writeSnapshot = existingLog.EatenSnapshot is null
                            && MealItemEatenState.EffectiveEatenIds(existingLog, meal).Count == 0;
        var eatenItemIds = writeSnapshot
            ? snapshot.Select(item => item.ItemId).ToList()
            : MealItemEatenState.ItemIds(existingLog, meal).ToList();

        var update = Builders<MealLog>.Update
            .Set(l => l.EatenAt, now)
            .Set(l => l.EatenItemIds, eatenItemIds)
            .Set(l => l.LogDate, todayLocalUtc)
            .Set(l => l.MealKind, mealKind)
            .Set(l => l.Photos, photos);

        if (writeSnapshot)
        {
            update = update
                .Set(l => l.EatenSnapshot, snapshot)
                .Set(l => l.FoodsEaten, foodsEaten);
        }

        if (note is not null)
        {
            update = update.Set(l => l.Note, note);
        }

        if (req.BasedOnPlanVersion is not null)
        {
            update = update.Set(l => l.BasedOnPlanVersion, req.BasedOnPlanVersion);
        }

        await mongo.MealLogs.UpdateOneAsync(
            Builders<MealLog>.Filter.Eq(l => l.Id, existingLog.Id), update, cancellationToken: ct);
    }

    /// <summary>
    /// Tells the plan's author, if still actively linked with effective nutrition access, about the conflict: the first conflict
    /// per client and plan creates one unread notification (with push), later ones only update its
    /// count. Failures are logged and never fail the client's already-applied write.
    /// </summary>
    private async Task NotifyCoachesOfConflictAsync(
        ClientProfile clientProfile, OfflineSyncConflict conflict, CancellationToken ct)
    {
        try
        {
            var links = await db.ClientProfessionalLinks
                .AsNoTracking()
                .Where(l => l.ClientProfileId == clientProfile.Id
                            && l.ConnectionState != ConnectionState.Ended
                            && l.ProfessionalProfile.UserId == conflict.PlanAuthorId)
                .Select(l => new
                {
                    l.ProfessionalProfile.UserId,
                    l.CanViewNutritionPlans,
                    l.CanViewTrainingPlans,
                    l.ProfessionalProfile.User.Language,
                })
                .ToListAsync(ct);

            if (links.Count == 0)
            {
                return;
            }

            var clientName = await db.Users
                .AsNoTracking()
                .Where(u => u.Id == clientProfile.UserId)
                .Select(u => u.FirstName + " " + u.LastName)
                .FirstOrDefaultAsync(ct) ?? string.Empty;

            var count = (int)await mongo.OfflineSyncConflicts.CountDocumentsAsync(
                Builders<OfflineSyncConflict>.Filter.Eq(c => c.ClientId, conflict.ClientId)
                & Builders<OfflineSyncConflict>.Filter.Eq(c => c.PlanId, conflict.PlanId)
                & Builders<OfflineSyncConflict>.Filter.Eq(c => c.ReviewedAt, null),
                cancellationToken: ct);

            var coalesceKey = $"{clientProfile.PublicId}:{conflict.PlanId}";
            var parameters = new Dictionary<string, string>
            {
                ["clientName"] = clientName,
                ["planName"] = conflict.PlanName,
                ["count"] = count.ToString(),
                ["clientPublicId"] = clientProfile.PublicId.ToString(),
                ["planId"] = conflict.PlanId.ToString(),
                ["coalesceKey"] = coalesceKey,
            };

            foreach (var link in links.DistinctBy(l => l.UserId))
            {
                var effective = await coachDiscipline.GetEffectiveAsync(
                    link.UserId, new LinkCapabilities(link.CanViewNutritionPlans, link.CanViewTrainingPlans), ct);

                if (!effective.CanViewNutritionPlans)
                {
                    continue;
                }

                await NotifyCoachAsync(link.UserId, link.Language, parameters, clientProfile.PublicId, conflict.PlanId, count, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex, "Offline sync conflict notification failed for client {ClientId} and plan {PlanId}",
                conflict.ClientId, conflict.PlanId);
        }
    }

    private async Task NotifyCoachAsync(
        Guid coachUserId, string? coachLanguage, Dictionary<string, string> parameters,
        Guid clientPublicId, Guid planId, int count, CancellationToken ct)
    {
        var coalesceKey = parameters["coalesceKey"];
        var unread = await db.Notifications
            .Where(n => n.RecipientUserId == coachUserId
                        && n.Type == NotificationType.OfflineSyncConflict
                        && !n.IsRead
                        && n.Data != null
                        && n.Data.Contains(coalesceKey))
            .FirstOrDefaultAsync(ct);

        var conflictPayload = new { clientId = clientPublicId, planId, count };

        if (unread is null)
        {
            var created = await notifications.CreateAsync(
                coachUserId, NotificationType.OfflineSyncConflict, parameters, ct: ct);

            await notifier.NotifyAsync(
                coachUserId,
                "newnotification",
                new { id = created.Id, type = NotificationType.OfflineSyncConflict.ToString(), data = created.Data },
                ct);
            await notifier.NotifyAsync(coachUserId, "offlinesyncconflict", conflictPayload, ct);
            return;
        }

        var (title, body) = NotificationTemplates.Resolve(
            NotificationType.OfflineSyncConflict, coachLanguage, parameters);
        unread.Title = title;
        unread.Body = body;
        unread.Data = JsonSerializer.Serialize(parameters);
        await db.SaveChangesAsync(ct);

        await notifier.NotifyAsync(coachUserId, "offlinesyncconflict", conflictPayload, ct);
    }

    /// <summary>
    /// Pushes a <c>clientcomplianceupdated</c> SignalR event to every active professional
    /// (trainer/nutritionist) linked to this client so their dashboards can refresh streak
    /// and compliance without polling.
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
        foreach (var userId in professionalUserIds)
        {
            await notifier.NotifyAsync(userId, "clientcomplianceupdated", payload, ct);
        }
    }
}
