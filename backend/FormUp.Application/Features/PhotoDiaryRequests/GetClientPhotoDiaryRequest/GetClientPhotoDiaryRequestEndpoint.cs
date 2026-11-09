using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Features.PhotoDiaryRequests.Shared;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.PhotoDiaryRequests.GetClientPhotoDiaryRequest;

/// <summary>
/// GET /client/photo-diary-requests/{Id}
/// Returns a single photo diary request addressed to the authenticated client.
/// Resolved the same way as the list endpoint: via a client-professional link
/// (where ClientProfile.UserId matches and the link is active) or via a pending
/// invite (where PendingInvite.Email matches the caller's email claim).
/// </summary>
public class GetClientPhotoDiaryRequestEndpoint(IApplicationDbContext db)
    : Endpoint<GetClientPhotoDiaryRequestRequest, ClientPhotoDiaryRequestSummary>
{
    public override void Configure()
    {
        Get("/client/photo-diary-requests/{Id}");
        Roles(AppRoles.Client);
        Summary(s =>
        {
            s.Summary = "Get a photo diary request (client view)";
            s.Description = "Returns a single photo diary request addressed to the authenticated client.";
            s.Responses[StatusCodes.Status200OK] = "Photo diary request detail";
            s.Responses[StatusCodes.Status401Unauthorized] = "Missing or invalid bearer token";
            s.Responses[StatusCodes.Status403Forbidden] = "Caller is not a client";
            s.Responses[StatusCodes.Status404NotFound] = "Request not found, or not owned by the caller";
        });
    }

    public override async Task HandleAsync(GetClientPhotoDiaryRequestRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);
        var emailClaim = User.FindFirstValue(AppClaims.Email);
        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var clientUserId = Guid.Parse(userId);

        var request = await db.PhotoDiaryRequests
            .AsNoTracking()
            .Include(r => r.Link)
                .ThenInclude(l => l!.ClientProfile)
            .Include(r => r.Link)
                .ThenInclude(l => l!.ProfessionalProfile)
                    .ThenInclude(pp => pp.User)
            .Include(r => r.PendingInvite)
                .ThenInclude(i => i!.ProfessionalProfile)
                    .ThenInclude(pp => pp.User)
            .FirstOrDefaultAsync(r => r.Id == req.Id, ct);

        // 404 if not found — do not leak existence for IDOR
        if (request is null || !PhotoDiaryRequestOwnership.IsOwnedByClient(request, clientUserId, emailClaim))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var coach = (request.Link?.ProfessionalProfile ?? request.PendingInvite!.ProfessionalProfile);

        await Send.OkAsync(new ClientPhotoDiaryRequestSummary
        {
            Id = request.Id,
            ProfessionalId = request.ProfessionalId,
            ProfessionalPublicId = coach.PublicId,
            ProfessionalName = coach.User.FirstName + " " + coach.User.LastName,
            ProfessionalRole = request.Link?.ProfessionalRole,
            LinkId = request.LinkId,
            PendingInviteId = request.PendingInviteId,
            PlanId = request.PlanId,
            DurationDays = request.DurationDays,
            Mode = request.Mode,
            Message = request.Message,
            Status = request.Status,
            DismissReason = request.DismissReason,
            AcceptedAt = request.AcceptedAt,
            CompletedAt = request.CompletedAt,
            CreatedAt = request.CreatedAt,
            UpdatedAt = request.UpdatedAt,
        }, ct);
    }
}
