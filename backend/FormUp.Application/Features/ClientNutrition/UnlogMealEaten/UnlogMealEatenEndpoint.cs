using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using Microsoft.EntityFrameworkCore;
using MongoDB.Driver;
using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Features.ClientNutrition.UnlogMealEaten;

/// <summary>
/// Endpoint for removing a previously logged meal entry for the current day,
/// letting a client uncheck a meal they marked as eaten by mistake.
/// </summary>
/// <param name="mongo">MongoDB context.</param>
/// <param name="db">Relational database context.</param>
/// <param name="notifier">Realtime notifier for pushing SignalR events.</param>
/// <param name="timeProvider">Clock abstraction (#955) — lets tests pin the "now" instant deterministically.</param>
public class UnlogMealEatenEndpoint(IMongoContext mongo, IApplicationDbContext db, IRealtimeNotifier notifier, TimeProvider timeProvider) : Endpoint<UnlogMealEatenRequest>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Delete("/client/nutrition/log/meals/{MealId}/eaten");
        Roles(AppRoles.Client);
        Summary(s =>
        {
            s.Summary = "Unmark a meal as eaten";
            s.Description = "Unchecks a meal: today's entries without photos or a note are deleted; entries with photos or a note are kept as not eaten.";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(UnlogMealEatenRequest req, CancellationToken ct)
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

        // Remove any meal log entries for this meal and client logged today. Resolves the
        // client's local calendar day (#935) — today anchors the LogDate equality check,
        // windowStart/windowEnd anchor the legacy EatenAt instant-range filter, so a meal
        // logged near local midnight is still found under the correct local day.
        // Uses the same OR pattern as GetTodayLog and SaveMealPhotos to find all log
        // variants: modern records (LogDate == today), photo-only records (EatenAt null,
        // LogDate == today), and legacy records (LogDate = default, EatenAt in today's window).
        if (await ClientRequestDeduplicator.FindAsync(mongo, clientId, req.ClientRequestId, ct) is not null)
        {
            await Send.NoContentAsync(ct);
            return;
        }

        var (today, windowStart, windowEnd) = await db.ResolveClientLocalDayWindowAsync(
            clientId, req.OccurredAt?.UtcDateTime ?? timeProvider.GetUtcNow().UtcDateTime, ct);

        var filter = Builders<MealLog>.Filter.And(
            Builders<MealLog>.Filter.Eq(l => l.ClientId, clientId),
            Builders<MealLog>.Filter.Eq(l => l.MealId, req.MealId),
            Builders<MealLog>.Filter.Or(
                Builders<MealLog>.Filter.Eq(l => l.LogDate, today),
                Builders<MealLog>.Filter.And(
                    Builders<MealLog>.Filter.Gte(l => l.EatenAt, windowStart),
                    Builders<MealLog>.Filter.Lt(l => l.EatenAt, windowEnd))));

        // Rows with nothing worth keeping (no photos, no note) are removed. Rows that carry
        // photos or a note stay — their _id is referenced by PlanPhoto.MealLogId — and only lose
        // their eaten state (EatenAt and every ticked item). LogDate is backfilled so legacy rows (LogDate = default, found only
        // through EatenAt) stay findable once EatenAt is gone.
        var emptyRowFilter = Builders<MealLog>.Filter.And(
            filter,
            Builders<MealLog>.Filter.Eq(l => l.Note, null),
            Builders<MealLog>.Filter.Or(
                Builders<MealLog>.Filter.Size(l => l.Photos, 0),
                Builders<MealLog>.Filter.Exists(l => l.Photos, false)));

        var deleteResult = await mongo.MealLogs.DeleteManyAsync(emptyRowFilter, ct);

        var keepResult = await mongo.MealLogs.UpdateManyAsync(
            Builders<MealLog>.Filter.And(
                filter,
                Builders<MealLog>.Filter.Or(
                    Builders<MealLog>.Filter.Ne(l => l.EatenAt, null),
                    Builders<MealLog>.Filter.SizeGt(l => l.EatenItemIds, 0))),
            Builders<MealLog>.Update
                .Unset(l => l.EatenAt)
                .Unset(l => l.EatenSnapshot)
                .Set(l => l.EatenItemIds, new List<Guid>())
                .Set(l => l.LogDate, today),
            cancellationToken: ct);

        await ClientRequestDeduplicator.RecordAsync(
            mongo, clientId, req.ClientRequestId, "UnlogMealEaten", req.BasedOnPlanVersion, null,
            timeProvider.GetUtcNow().UtcDateTime, ct);

        if (deleteResult.DeletedCount + keepResult.ModifiedCount > 0)
        {
            // The SignalR payload's ClientId is the trainer-facing ClientProfile.PublicId
            // convention (unrelated to the Mongo document clientId key migrated in #840) —
            // pass clientProfile.PublicId explicitly rather than the (now UserId-valued) clientId.
            await NotifyLinkedProfessionalsAsync(clientProfile.Id, clientProfile.PublicId, ct);
        }

        await Send.NoContentAsync(ct);
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
