using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Domain.Extensions;

/// <summary>
/// Shared ownership predicate for photo diary requests. A request is owned by the
/// calling client when its link points at that client's active profile, or when its
/// pending invite matches the caller's email claim. Consumed by every client-facing
/// photo diary request endpoint (Accept, Dismiss, Submit, GetClientPhotoDiaryRequest)
/// plus <c>FinalizePlanPhotoEndpoint</c>, which validates a diary request's ownership
/// as part of finalizing a plan photo upload.
/// </summary>
public static class PhotoDiaryRequestOwnership
{
    /// <summary>
    /// Determines whether the given photo diary request belongs to the calling client.
    /// </summary>
    public static bool IsOwnedByClient(
        PhotoDiaryRequest request,
        Guid clientUserId,
        string? clientEmail)
    {
        if (request.Link is not null)
        {
            return request.Link.ClientProfile.UserId == clientUserId && request.Link.ConnectionState != ConnectionState.Ended;
        }

        if (request.PendingInvite is not null && clientEmail is not null)
        {
            return string.Equals(request.PendingInvite.Email, clientEmail,
                StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}
