using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Domain.Services;

/// <summary>
/// Tells a client's linked professionals that the client's compliance changed.
/// </summary>
public static class ClientComplianceNotifier
{
    /// <summary>
    /// Pushes a <c>clientcomplianceupdated</c> SignalR event to every active professional linked to
    /// the client so their dashboards can refresh without polling. The payload's ClientId is the
    /// trainer-facing ClientProfile.PublicId.
    /// </summary>
    public static async Task NotifyLinkedProfessionalsAsync(
        IApplicationDbContext db, IRealtimeNotifier notifier, long clientProfileId, Guid clientPublicId, CancellationToken ct)
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
