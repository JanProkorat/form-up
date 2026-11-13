using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Domain.Services;

/// <summary>
/// Undoes the kept/released client choice a coach made while their paid plan had lapsed.
/// </summary>
public static class CoachLapseLinkReset
{
    /// <summary>
    /// Sets every <c>FreePlanKept</c> and <c>FreePlanReleased</c> link of the professional back to
    /// <c>Connected</c>. Tracks the links; the caller saves.
    /// </summary>
    /// <param name="db">Application database context.</param>
    /// <param name="professionalProfileId">The professional's <c>ProfessionalProfile.Id</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task ResetAsync(IApplicationDbContext db, long professionalProfileId, CancellationToken ct)
    {
        var lapseLinks = await db.ClientProfessionalLinks
            .Where(link => link.ProfessionalProfileId == professionalProfileId
                           && (link.ConnectionState == ConnectionState.FreePlanKept
                               || link.ConnectionState == ConnectionState.FreePlanReleased))
            .ToListAsync(ct);

        foreach (var link in lapseLinks)
        {
            link.ConnectionState = ConnectionState.Connected;
        }
    }
}
