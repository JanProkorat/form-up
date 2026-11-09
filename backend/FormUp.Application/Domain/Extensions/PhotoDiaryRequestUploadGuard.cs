using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Domain.Extensions;

/// <summary>
/// Shared load-and-validate sequence for uploading a photo against a photo diary request,
/// used by both the plan and the no-plan finalize endpoints.
/// </summary>
public static class PhotoDiaryRequestUploadGuard
{
    /// <summary>
    /// Loads the request and checks that the caller may upload against it. Writes the response and
    /// returns null on failure: 404 when unknown or not the caller's, 409
    /// <see cref="ErrorCodes.PhotoDiaryRequestInvalidStatus"/> when not Accepted/InProgress, 409
    /// <see cref="ErrorCodes.PhotoDiaryRequestExpired"/> when past its end date.
    /// </summary>
    public static async Task<PhotoDiaryRequest?> LoadForUploadOrRespondAsync(
        this IEndpoint endpoint,
        IApplicationDbContext db,
        Guid requestId,
        Guid callerUserId,
        string? emailClaim,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var request = await db.PhotoDiaryRequests
            .Include(r => r.Link)
                .ThenInclude(l => l!.ClientProfile)
                    .ThenInclude(cp => cp.User)
            .Include(r => r.PendingInvite)
            .FirstOrDefaultAsync(r => r.Id == requestId, ct);

        // 404 if not found or owned by another client — don't leak existence
        if (request is null || !PhotoDiaryRequestOwnership.IsOwnedByClient(request, callerUserId, emailClaim))
        {
            await endpoint.HttpContext.Response.SendNotFoundAsync(ct);
            return null;
        }

        if (request.Status is not (PhotoDiaryStatus.Accepted or PhotoDiaryStatus.InProgress))
        {
            await endpoint.SendProblemAsync(409, ErrorCodes.PhotoDiaryRequestInvalidStatus,
                "Photos can only be uploaded against diary requests in Accepted or InProgress status.", ct);
            return null;
        }

        if (!request.IsOpenAt(now))
        {
            await endpoint.SendProblemAsync(409, ErrorCodes.PhotoDiaryRequestExpired,
                "This photo diary request has expired and no longer accepts photos.", ct);
            return null;
        }

        return request;
    }

    /// <summary>
    /// Display name of the client for the coach-facing upload event; falls back to the email claim.
    /// </summary>
    public static string ResolveClientName(PhotoDiaryRequest request, string? clientEmail)
    {
        if (request.Link?.ClientProfile?.User is { } user)
        {
            return $"{user.FirstName} {user.LastName}".Trim();
        }

        return clientEmail ?? string.Empty;
    }
}
