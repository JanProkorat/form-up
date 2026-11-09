using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Domain.Services;

/// <summary>What a professional may do in a thread with one client.</summary>
public enum ProfessionalSendAccess
{
    /// <summary>No link has ever existed and the client never sent a join request — an invite-only thread.</summary>
    Locked,

    /// <summary>May send; the message must not un-archive the thread (ended link or a join request of any status).</summary>
    SendOnly,

    /// <summary>May send and un-archive the thread (live link).</summary>
    SendAndUnarchive,
}

/// <summary>
/// Decides whether a professional may send into a thread. Only a thread with no link ever and no
/// join request from the client, of any status, is locked: an invite-only thread the client has not accepted. Ended links
/// keep sending as before.
/// </summary>
public static class ProfessionalSendGuard
{
    /// <summary>Evaluates the access of one professional to one client.</summary>
    public static async Task<ProfessionalSendAccess> EvaluateAsync(
        IApplicationDbContext db, Guid professionalUserId, Guid clientUserId, CancellationToken ct)
    {
        var access = await EvaluateManyAsync(db, professionalUserId, [clientUserId], ct);
        return access[clientUserId];
    }

    /// <summary>Evaluates the access of one professional to many clients in at most two queries.</summary>
    public static async Task<Dictionary<Guid, ProfessionalSendAccess>> EvaluateManyAsync(
        IApplicationDbContext db, Guid professionalUserId, IReadOnlyCollection<Guid> clientUserIds, CancellationToken ct)
    {
        var result = clientUserIds.Distinct().ToDictionary(id => id, _ => ProfessionalSendAccess.Locked);

        if (result.Count == 0)
        {
            return result;
        }

        var ids = result.Keys.ToList();

        var links = await db.ClientProfessionalLinks
            .AsNoTracking()
            .Where(l => l.ProfessionalProfile.UserId == professionalUserId && ids.Contains(l.ClientProfile.UserId))
            .Select(l => new { ClientUserId = l.ClientProfile.UserId, IsActive = l.ConnectionState != ConnectionState.Ended })
            .ToListAsync(ct);

        foreach (var link in links)
        {
            if (link.IsActive)
            {
                result[link.ClientUserId] = ProfessionalSendAccess.SendAndUnarchive;
            }
            else if (result[link.ClientUserId] == ProfessionalSendAccess.Locked)
            {
                result[link.ClientUserId] = ProfessionalSendAccess.SendOnly;
            }
        }

        var unresolved = result.Where(pair => pair.Value == ProfessionalSendAccess.Locked).Select(pair => pair.Key).ToList();

        if (unresolved.Count > 0)
        {
            var requestingClientUserIds = await db.ClientRequests
                .AsNoTracking()
                .Where(r =>
                    r.ProfessionalProfile.UserId == professionalUserId &&
                    unresolved.Contains(r.ClientProfile.UserId))
                .Select(r => r.ClientProfile.UserId)
                .Distinct()
                .ToListAsync(ct);

            foreach (var clientUserId in requestingClientUserIds)
            {
                result[clientUserId] = ProfessionalSendAccess.SendOnly;
            }
        }

        return result;
    }

    /// <summary>
    /// From one client's side: which of the given professionals are locked out of the thread with
    /// that client (no link ever, no join request ever). Used to withhold the client's presence
    /// and typing from them.
    /// </summary>
    public static async Task<HashSet<Guid>> FindLockedProfessionalsAsync(
        IApplicationDbContext db, Guid clientUserId, IReadOnlyCollection<Guid> professionalUserIds, CancellationToken ct)
    {
        var locked = professionalUserIds.Distinct().ToHashSet();

        if (locked.Count == 0)
        {
            return locked;
        }

        var ids = locked.ToList();

        var linked = await db.ClientProfessionalLinks
            .AsNoTracking()
            .Where(l => l.ClientProfile.UserId == clientUserId && ids.Contains(l.ProfessionalProfile.UserId))
            .Select(l => l.ProfessionalProfile.UserId)
            .Distinct()
            .ToListAsync(ct);

        locked.ExceptWith(linked);

        if (locked.Count > 0)
        {
            var remaining = locked.ToList();

            var requested = await db.ClientRequests
                .AsNoTracking()
                .Where(r => r.ClientProfile.UserId == clientUserId && remaining.Contains(r.ProfessionalProfile.UserId))
                .Select(r => r.ProfessionalProfile.UserId)
                .Distinct()
                .ToListAsync(ct);

            locked.ExceptWith(requested);
        }

        return locked;
    }

    /// <summary>
    /// Writes the 403 <see cref="ErrorCodes.ConversationLocked"/> response and returns
    /// <see langword="null"/> when the thread is locked; otherwise returns the access level.
    /// </summary>
    public static async Task<ProfessionalSendAccess?> RequireProfessionalAccessOrRespondAsync(
        this IEndpoint endpoint, IApplicationDbContext db, Guid professionalUserId, Guid clientUserId, CancellationToken ct)
    {
        var access = await EvaluateAsync(db, professionalUserId, clientUserId, ct);

        if (access != ProfessionalSendAccess.Locked)
        {
            return access;
        }

        await endpoint.SendProblemAsync(
            403, ErrorCodes.ConversationLocked, "The client has not accepted the invitation yet.", ct);
        return null;
    }
}
