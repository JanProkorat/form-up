using System.Text.Json;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Application.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using MongoDB.Driver;

namespace FormUp.Application.Domain.Services;

/// <summary>
/// Builds <see cref="OfflineSyncConflict"/> documents and tells the plan's author about them. Shared by
/// every meal-eaten endpoint that accepts an offline-synced write.
/// </summary>
public static class OfflineSyncConflictRecorder
{
    /// <summary>
    /// Builds the conflict document for an offline write that collided with a plan change.
    /// </summary>
    public static OfflineSyncConflict Build(
        NutritionPlan plan,
        MealKind mealKind,
        int? mealChangedAtPlanVersion,
        string operation,
        Guid mealId,
        int basedOnPlanVersion,
        Guid clientId,
        OfflineSyncConflictKind kind,
        bool mealLogged,
        DateTime occurredAt,
        DateTime logDate,
        DateTime dateCreated,
        List<SeenMealItem> seenItems) => new()
    {
        ExternalId = Guid.NewGuid(),
        ClientId = clientId,
        PlanId = plan.ExternalId,
        PlanAuthorId = plan.NutritionistId,
        Operation = operation,
        Kind = kind,
        MealId = mealId,
        OccurredAt = occurredAt,
        LogDate = logDate,
        BasedOnPlanVersion = basedOnPlanVersion,
        CurrentPlanVersion = plan.Version,
        MealChangedAtPlanVersion = mealChangedAtPlanVersion,
        PlanName = plan.Name,
        MealKind = mealKind,
        MealLogged = mealLogged,
        DateCreated = dateCreated,
        SeenItems = seenItems,
    };

    /// <summary>
    /// Builds and stores a conflict for an offline write on a meal that is still in the current plan
    /// (the meal changed after the client's snapshot, or one of its items was removed).
    /// </summary>
    public static async Task<OfflineSyncConflict> RecordForPlanMealAsync(
        IMongoContext mongo,
        CurrentPlanMeal current,
        string operation,
        int basedOnPlanVersion,
        Guid clientId,
        OfflineSyncConflictKind kind,
        bool mealLogged,
        DateTime occurredAt,
        DateTime logDate,
        DateTime dateCreated,
        CancellationToken ct)
    {
        var conflict = Build(
            current.Plan, current.Meal.Kind, current.Meal.ChangedAtPlanVersion, operation, current.Meal.MealId,
            basedOnPlanVersion, clientId, kind, mealLogged, occurredAt, logDate, dateCreated, []);

        await mongo.OfflineSyncConflicts.InsertOneAsync(conflict, cancellationToken: ct);

        return conflict;
    }

    /// <summary>
    /// Records a <see cref="OfflineSyncConflictKind.MealRemoved"/> conflict for an offline write on a meal that
    /// left the client's current plan. Returns the stored conflict, or null when the meal id is unknown to the client
    /// (nothing is recorded then).
    /// </summary>
    public static async Task<OfflineSyncConflict?> RecordMealRemovedAsync(
        IMongoContext mongo,
        Guid clientId,
        Guid mealId,
        string operation,
        int basedOnPlanVersion,
        DateTime occurredAt,
        DateTime logDate,
        DateTime dateCreated,
        CancellationToken ct)
    {
        var removed = await RemovedMealFinder.FindRemovedMealAsync(mongo, clientId, mealId, ct);

        if (removed is null)
        {
            return null;
        }

        var conflict = Build(
            removed.Value.Plan, removed.Value.Kind, removed.Value.ChangedAtPlanVersion, operation, mealId,
            basedOnPlanVersion, clientId, OfflineSyncConflictKind.MealRemoved, mealLogged: false, occurredAt, logDate,
            dateCreated, []);

        await mongo.OfflineSyncConflicts.InsertOneAsync(conflict, cancellationToken: ct);

        return conflict;
    }

    /// <summary>
    /// Tells the plan's author, if still actively linked with effective nutrition access, about the conflict: the first conflict
    /// per client and plan creates one unread notification (with push), later ones only update its
    /// count. Failures are logged and never fail the client's already-applied write.
    /// </summary>
    public static async Task NotifyCoachesAsync(
        IApplicationDbContext db,
        IMongoContext mongo,
        INotificationService notifications,
        IRealtimeNotifier notifier,
        ICoachDisciplineAccess coachDiscipline,
        ILogger logger,
        ClientProfile clientProfile,
        OfflineSyncConflict conflict,
        CancellationToken ct)
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

                await NotifyCoachAsync(
                    db, notifications, notifier, link.UserId, link.Language, parameters, clientProfile.PublicId,
                    conflict.PlanId, count, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex, "Offline sync conflict notification failed for client {ClientId} and plan {PlanId}",
                conflict.ClientId, conflict.PlanId);
        }
    }

    private static async Task NotifyCoachAsync(
        IApplicationDbContext db,
        INotificationService notifications,
        IRealtimeNotifier notifier,
        Guid coachUserId,
        string? coachLanguage,
        Dictionary<string, string> parameters,
        Guid clientPublicId,
        Guid planId,
        int count,
        CancellationToken ct)
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
}
