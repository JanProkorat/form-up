using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Features.PhotoDiaryRequests.Shared;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.PhotoDiaryRequests.ListClientRequests;

/// <summary>
/// GET /client/photo-diary-requests
/// Returns a paginated list of photo diary requests addressed to the authenticated client.
/// Requests are resolved via client-professional links (where ClientProfile.UserId matches)
/// and via pending invites (where PendingInvite.Email matches the caller's email claim).
/// </summary>
public class ListClientRequestsEndpoint(IApplicationDbContext db)
    : Endpoint<ListClientRequestsRequest, ListClientRequestsResponse>
{
    public override void Configure()
    {
        Get("/client/photo-diary-requests");
        Roles(AppRoles.Client);
        Summary(s =>
        {
            s.Summary = "List photo diary requests (client view)";
            s.Description = "Returns a paginated list of photo diary requests addressed to the authenticated client.";
        });
    }

    public override async Task HandleAsync(ListClientRequestsRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);
        var emailClaim = User.FindFirstValue(AppClaims.Email);
        if (userId is null) { await Send.UnauthorizedAsync(ct); return; }
        var clientUserId = Guid.Parse(userId);

        // Collect link IDs where this client is the client profile
        var clientLinkIds = await db.ClientProfessionalLinks
            .AsNoTracking()
            .Where(l => l.ClientProfile.UserId == clientUserId && l.ConnectionState != ConnectionState.Ended)
            .Select(l => (long?)l.Id)
            .ToListAsync(ct);

        // Collect invite IDs addressed to this client's email
        // Invites addressed to a coaching account are silent rows — a coach-role caller sees none.
        var isCoachCaller = User.IsInRole(AppRoles.Trainer) || User.IsInRole(AppRoles.Nutritionist);

        var clientInviteIds = emailClaim is not null && !isCoachCaller
            ? await db.PendingInvites
                .AsNoTracking()
                .Where(i => i.Email == emailClaim)
                .Select(i => (long?)i.Id)
                .ToListAsync(ct)
            : [];

        var query = db.PhotoDiaryRequests
            .AsNoTracking()
            .Where(r =>
                (r.LinkId != null && clientLinkIds.Contains(r.LinkId)) ||
                (r.PendingInviteId != null && clientInviteIds.Contains(r.PendingInviteId)));

        if (req.Status.HasValue)
            query = query.Where(r => r.Status == req.Status.Value);

        if (req.PlanId.HasValue)
            query = query.Where(r => r.PlanId == req.PlanId.Value);

        var totalCount = await query.CountAsync(ct);

        HttpContext.Response.Headers["X-Total-Count"] = totalCount.ToString();

        var items = await query
            .OrderByDescending(r => r.CreatedAt)
            .Skip((req.Page - 1) * req.PageSize)
            .Take(req.PageSize)
            .Select(r => new ClientPhotoDiaryRequestSummary
            {
                Id = r.Id,
                ProfessionalId = r.ProfessionalId,
                ProfessionalPublicId = r.Link != null
                    ? r.Link.ProfessionalProfile.PublicId
                    : r.PendingInvite!.ProfessionalProfile.PublicId,
                ProfessionalName = r.Link != null
                    ? r.Link.ProfessionalProfile.User.FirstName + " " + r.Link.ProfessionalProfile.User.LastName
                    : r.PendingInvite!.ProfessionalProfile.User.FirstName + " " + r.PendingInvite.ProfessionalProfile.User.LastName,
                ProfessionalRole = r.Link != null ? r.Link.ProfessionalRole : (UserRole?)null,
                LinkId = r.LinkId,
                PendingInviteId = r.PendingInviteId,
                PlanId = r.PlanId,
                DurationDays = r.DurationDays,
                Mode = r.Mode,
                Message = r.Message,
                Status = r.Status,
                DismissReason = r.DismissReason,
                AcceptedAt = r.AcceptedAt,
                CompletedAt = r.CompletedAt,
                CreatedAt = r.CreatedAt,
                UpdatedAt = r.UpdatedAt,
            })
            .ToListAsync(ct);

        await Send.OkAsync(new ListClientRequestsResponse { Items = items }, ct);
    }
}
