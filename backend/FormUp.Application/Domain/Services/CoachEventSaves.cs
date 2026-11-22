using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace FormUp.Application.Domain.Services;

/// <summary>
/// Saves for requests whose only pending change is a coach event, where two identical concurrent requests race on the
/// unique idempotency key.
/// </summary>
public static class CoachEventSaves
{
    /// <summary>True when the failure is a unique violation on a <c>coach_events</c> row (its idempotency key).</summary>
    public static bool IsDuplicateKey(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, TableName: "coach_events" };

    /// <summary>
    /// Saves the pending coach event; a duplicate key means the other request recorded it, which is the outcome wanted.
    /// </summary>
    /// <param name="db">Application database context holding only the pending event.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task SaveIgnoringDuplicateAsync(IApplicationDbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            // Another request already stored this event; nothing else was pending in this save.
        }
    }

    /// <summary>
    /// Saves all pending changes. When a pending coach event collides with an already stored idempotency key, the colliding
    /// events are detached and the save runs once more, so the other changes still commit. Other failures propagate.
    /// </summary>
    /// <param name="db">Application database context with the pending changes.</param>
    /// <param name="logger">Logger for the dropped events.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task SaveKeepingChangesOnDuplicateAsync(IApplicationDbContext db, ILogger logger, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            var pending = db.CoachEvents.Local.Where(e => e.Id == 0).ToList();
            var pendingKeys = pending.Select(e => e.IdempotencyKey).ToList();
            var stored = await db.CoachEvents
                .AsNoTracking()
                .Where(e => pendingKeys.Contains(e.IdempotencyKey))
                .Select(e => e.IdempotencyKey)
                .ToListAsync(ct);

            if (stored.Count == 0)
            {
                throw;
            }

            logger.LogInformation("Dropping {Count} coach event(s) a concurrent request already recorded", stored.Count);

            foreach (var duplicate in pending.Where(e => stored.Contains(e.IdempotencyKey)))
            {
                db.CoachEvents.Local.Remove(duplicate);
            }

            await db.SaveChangesAsync(ct);
        }
    }
}
