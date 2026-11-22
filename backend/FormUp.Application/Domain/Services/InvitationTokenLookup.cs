using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Domain.Services;

/// <summary>
/// Looks up the email an unused, unexpired invitation token was issued for. Shared by the anonymous
/// resolve endpoint and sign-up so both judge "valid" identically.
/// </summary>
public static class InvitationTokenLookup
{
    /// <summary>
    /// Returns the invited email when the token exists, is unused and has not expired at
    /// <paramref name="nowUtc"/>; otherwise null. One query, then in-memory checks, so every bad
    /// token takes the same path.
    /// </summary>
    /// <param name="db">Database context.</param>
    /// <param name="token">The invitation token string.</param>
    /// <param name="nowUtc">Current UTC time.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<string?> FindValidEmailAsync(
        IApplicationDbContext db, string token, DateTime nowUtc, CancellationToken ct)
    {
        var row = await db.InvitationTokens
            .AsNoTracking()
            .Where(t => t.Token == token)
            .Select(t => new { t.Email, t.IsUsed, t.ExpiresAt })
            .FirstOrDefaultAsync(ct);

        return row is { IsUsed: false } && row.ExpiresAt > nowUtc ? row.Email : null;
    }
}
