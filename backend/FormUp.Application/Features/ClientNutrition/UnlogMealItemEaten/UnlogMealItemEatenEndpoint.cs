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

namespace FormUp.Application.Features.ClientNutrition.UnlogMealItemEaten;

/// <summary>
/// Unmarks one food or recipe of a meal in the client's current plan. The meal's <c>EatenAt</c> is
/// cleared; a row left with no eaten item, photo or note is removed.
/// </summary>
/// <param name="mongo">MongoDB context.</param>
/// <param name="db">Relational database context.</param>
/// <param name="notifier">Realtime notifier for pushing SignalR events.</param>
/// <param name="timeProvider">Clock.</param>
internal sealed class UnlogMealItemEatenEndpoint(
    IMongoContext mongo,
    IApplicationDbContext db,
    IRealtimeNotifier notifier,
    TimeProvider timeProvider)
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
            s.Responses[StatusCodes.Status200OK] = "Eaten state of the meal after the untick";
            s.Responses[StatusCodes.Status400BadRequest] = "Invalid request";
            s.Responses[StatusCodes.Status401Unauthorized] = "Missing or unreadable caller claim";
            s.Responses[StatusCodes.Status404NotFound] =
                "No client profile, or the meal is not in the client's current plan, or (errorCode MEAL_ITEM_NOT_FOUND) the item is not in the meal";
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

        var (plan, meal) = await FindCurrentPlanMealAsync(clientId, req.MealId, todayLocalUtc, ct);

        if (plan is null || meal is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var row = await FindRowAsync(clientId, plan.ExternalId, req.MealId, todayLocalUtc, windowStartUtc, windowEndUtc, ct);

        // A row with a snapshot is judged against the items it was eaten from, not the live meal.
        var items = row is null ? MealItemEatenState.ItemIds(meal) : MealItemEatenState.ItemIds(row, meal);

        if (!items.Contains(req.ItemId))
        {
            await this.SendProblemAsync(404, ErrorCodes.MealItemNotFound, "The item is not part of this meal.", ct);
            return;
        }

        if (!alreadyApplied)
        {
            row = await ApplyAsync(req, row, meal, todayLocalUtc, ct);

            await ClientRequestDeduplicator.RecordAsync(
                mongo, clientId, req.ClientRequestId, OperationName, req.BasedOnPlanVersion, null,
                timeProvider.GetUtcNow().UtcDateTime, ct);

            await NotifyLinkedProfessionalsAsync(clientProfile.Id, clientProfile.PublicId, ct);
        }

        var currentIds = row is null ? [] : MealItemEatenState.EffectiveEatenIds(row, meal);

        await Send.OkAsync(new UnlogMealItemEatenResponse
        {
            MealId = req.MealId,
            ItemId = req.ItemId,
            EatenState = MealItemEatenState.StateOf(items, currentIds, row?.EatenAt is not null),
            EatenItemIds = currentIds.ToList(),
            EatenAt = null,
        }, ct);
    }

    /// <summary>
    /// Resolves the Active plan whose date window contains today (a client may hold several
    /// sequential plans, #780) and the meal inside it; both null when either is missing.
    /// </summary>
    private async Task<(NutritionPlan? Plan, PlanMeal? Meal)> FindCurrentPlanMealAsync(
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

        return meal is null ? (null, null) : (plan, meal);
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

    /// <summary>
    /// Removes the item from the row's eaten ids. A row left with no eaten item, photo or note is
    /// deleted; otherwise it is kept (its id is referenced by plan photos) without <c>EatenAt</c>.
    /// Returns the row as it stands afterwards, or null when it was deleted or never existed.
    /// </summary>
    private async Task<MealLog?> ApplyAsync(
        UnlogMealItemEatenRequest req, MealLog? row, PlanMeal meal, DateTime todayLocalUtc, CancellationToken ct)
    {
        if (row is null)
        {
            return null;
        }

        var eatenIds = MealItemEatenState.EffectiveEatenIds(row, meal);
        eatenIds.Remove(req.ItemId);

        if (eatenIds.Count == 0 && row.Photos.Count == 0 && row.Note is null)
        {
            await mongo.MealLogs.DeleteOneAsync(Builders<MealLog>.Filter.Eq(l => l.Id, row.Id), ct);
            return null;
        }

        var update = Builders<MealLog>.Update
            .Set(l => l.EatenItemIds, eatenIds.ToList())
            .Unset(l => l.EatenAt)
            .Set(l => l.LogDate, todayLocalUtc);

        // Nothing eaten any more: drop the snapshot, which unlocks the meal for the coach.
        if (eatenIds.Count == 0)
        {
            update = update.Unset(l => l.EatenSnapshot);
            row.EatenSnapshot = null;
        }

        await mongo.MealLogs.UpdateOneAsync(Builders<MealLog>.Filter.Eq(l => l.Id, row.Id), update, cancellationToken: ct);

        row.EatenItemIds = eatenIds.ToList();
        row.EatenAt = null;

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
}
