using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FormUp.Application.Features.PhotoDiaryRequests.AcceptRequest;

/// <summary>
/// POST /client/photo-diary-requests/{id}/accept
/// Transitions a Pending request to Accepted and records the AcceptedAt timestamp.
/// After a successful transition emits a <c>photoDiaryAccepted</c> SignalR event to the
/// <b>professional</b> group (<see cref="Domain.Entities.PhotoDiaryRequest.ProfessionalId"/>)
/// so the trainer/nutritionist's open diary card flips from Pending to Accepted in real time.
/// Broadcast failures are best-effort and never fail the HTTP response.
/// </summary>
public class AcceptRequestEndpoint(
    IApplicationDbContext db,
    IRealtimeNotifier notifier,
    TimeProvider timeProvider,
    ILogger<AcceptRequestEndpoint> logger)
    : Endpoint<AcceptRequestRequest, AcceptRequestResponse>
{
    public override void Configure()
    {
        Post("/client/photo-diary-requests/{id}/accept");
        Roles(AppRoles.Client);
        Summary(s =>
        {
            s.Summary = "Accept a photo diary request";
            s.Description = "Transitions a Pending photo diary request to Accepted. The mode was set by the coach.";
            s.Responses[StatusCodes.Status200OK] = "Request accepted";
            s.Responses[StatusCodes.Status404NotFound] = "Request not found, or not addressed to the caller";
            s.Responses[StatusCodes.Status409Conflict] = "Request is not Pending, is past its end date, or has no mode";
        });
    }

    public override async Task HandleAsync(AcceptRequestRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);
        var emailClaim = User.FindFirstValue(AppClaims.Email);
        if (userId is null) { await Send.UnauthorizedAsync(ct); return; }
        var clientUserId = Guid.Parse(userId);

        // Load the request with the link/invite for ownership check.
        // ClientProfile.User is included so ResolveClientName can populate the
        // SignalR payload without a second round-trip.
        var request = await db.PhotoDiaryRequests
            .Include(r => r.Link)
                .ThenInclude(l => l!.ClientProfile)
                    .ThenInclude(cp => cp.User)
            .Include(r => r.PendingInvite)
            .FirstOrDefaultAsync(r => r.Id == req.Id, ct);

        // 404 if not found — do not leak existence for IDOR
        if (request is null || !PhotoDiaryRequestOwnership.IsOwnedByClient(request, clientUserId, emailClaim))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var now = timeProvider.GetUtcNow();

        // 409 if not Pending, past its end date, or a legacy row without a mode (the coach picks
        // the mode at creation now; accepting a mode-less row would break the mode constraint).
        if (request.Status != PhotoDiaryStatus.Pending || request.IsExpiredAt(now) || request.Mode is null)
        {
            await this.SendProblemAsync(409, ErrorCodes.PhotoDiaryRequestInvalidStatus,
                "This request cannot be accepted: it is not Pending, has expired, or has no mode.", ct);
            return;
        }

        request.Status = PhotoDiaryStatus.Accepted;
        request.AcceptedAt = now;
        request.UpdatedAt = now;

        await db.SaveChangesAsync(ct);

        // ── Emit photoDiaryAccepted to the professional group (best-effort) ─────
        // Recipient: request.ProfessionalId  →  nutritionist/trainer group.
        // The web AppShell handler invalidates ['diary-requests', planId] so the
        // open diary card flips from Pending to Accepted/InProgress immediately.
        try
        {
            var clientName = ResolveClientName(request, emailClaim);
            await notifier.NotifyAsync(
                request.ProfessionalId,   // → professional group
                "photodiaryaccepted",
                new PhotoDiaryAcceptedEvent
                {
                    RequestId = request.Id,
                    ClientName = clientName,
                    Mode = request.Mode.ToString() ?? string.Empty,
                    PlanId = request.PlanId,
                    AcceptedAt = request.UpdatedAt,
                },
                ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to emit photoDiaryAccepted for request {RequestId} to professional {ProfessionalId}",
                request.Id, request.ProfessionalId);
        }

        await Send.OkAsync(new AcceptRequestResponse
        {
            Id = request.Id,
            Status = request.Status,
            Mode = request.Mode,
            AcceptedAt = request.AcceptedAt,
        }, ct);
    }

    /// <summary>
    /// Resolves a display name for the client from the request's link navigation.
    /// Falls back to the email claim if it is not populated.
    /// </summary>
    private static string ResolveClientName(
        Domain.Entities.PhotoDiaryRequest request,
        string? clientEmail)
    {
        if (request.Link?.ClientProfile?.User is { } user)
            return $"{user.FirstName} {user.LastName}".Trim();

        return clientEmail ?? string.Empty;
    }
}
