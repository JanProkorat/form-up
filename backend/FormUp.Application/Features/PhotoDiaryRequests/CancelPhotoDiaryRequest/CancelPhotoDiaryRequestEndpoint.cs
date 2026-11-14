using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.PhotoDiaryRequests.CancelPhotoDiaryRequest;

/// <summary>
/// POST /trainer/photo-diary-requests/{Id}/cancel
/// Lets the coach who created a photo diary request close it while it is still open.
/// Ownership is the creator check only: closing your own request grants no access to client data,
/// so a coach whose link has ended can still close it.
/// </summary>
[RemovedCoachRole(RemovedCoachRoleMode.Refuse)]
internal sealed class CancelPhotoDiaryRequestEndpoint(IApplicationDbContext db, TimeProvider timeProvider)
    : Endpoint<CancelPhotoDiaryRequestRequest, CancelPhotoDiaryRequestResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/trainer/photo-diary-requests/{Id}/cancel");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Cancel a photo diary request";
            s.Description = "Closes one of the caller's own photo diary requests while it is still open.";
            s.Responses[StatusCodes.Status200OK] = "Request cancelled";
            s.Responses[StatusCodes.Status401Unauthorized] = "Missing or unreadable caller claim";
            s.Responses[StatusCodes.Status404NotFound] = "Request not found, or not created by the caller";
            s.Responses[StatusCodes.Status409Conflict] = "Request is no longer open";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(CancelPhotoDiaryRequestRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);

        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var professionalId = Guid.Parse(userId);

        if (await this.RespondIfCoachFeatureNotEntitledAsync(
                professionalId, entitlements => entitlements.CanUseProgressPhotos,
                "The current subscription does not include progress photos.", ct))
        {
            return;
        }

        var request = await db.PhotoDiaryRequests.FirstOrDefaultAsync(r => r.Id == req.Id, ct);

        // 404 for unknown and foreign ids alike — existence must not leak.
        if (request is null || request.ProfessionalId != professionalId)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var now = timeProvider.GetUtcNow();

        if (!request.IsOpenAt(now))
        {
            await this.SendProblemAsync(409, ErrorCodes.PhotoDiaryRequestInvalidStatus,
                "This request is no longer open and cannot be cancelled.", ct);
            return;
        }

        request.Status = PhotoDiaryStatus.Cancelled;
        request.UpdatedAt = now;

        await db.SaveChangesAsync(ct);

        await Send.OkAsync(new CancelPhotoDiaryRequestResponse
        {
            Id = request.Id,
            Status = request.Status,
        }, ct);
    }
}
