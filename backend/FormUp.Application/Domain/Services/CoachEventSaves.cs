using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
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
}
