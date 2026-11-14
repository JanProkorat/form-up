using FormUp.Application.Domain.Extensions;
using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.PhotoDiaryRequests.ListTrainerRequests;

/// <summary>
/// GET /trainer/photo-diary-requests
/// Returns a paginated list of photo diary requests created by the authenticated professional.
/// </summary>
public class ListTrainerRequestsEndpoint(IApplicationDbContext db, TimeProvider timeProvider)
    : Endpoint<ListTrainerRequestsRequest, ListTrainerRequestsResponse>
{
    public override void Configure()
    {
        Get("/trainer/photo-diary-requests");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "List photo diary requests (trainer view)";
            s.Description = "Returns a paginated list of photo diary requests created by the authenticated professional.";
        });
    }

    public override async Task HandleAsync(ListTrainerRequestsRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);
        if (userId is null) { await Send.UnauthorizedAsync(ct); return; }
        var professionalId = Guid.Parse(userId);

        if (await this.RespondIfCoachFeatureNotEntitledAsync(
                professionalId, entitlements => entitlements.CanUseProgressPhotos,
                "The current subscription does not include progress photos.", ct))
        {
            return;
        }

        var query = db.PhotoDiaryRequests
            .AsNoTracking()
            .Where(r => r.ProfessionalId == professionalId);

        if (req.Status.HasValue)
            query = query.Where(r => r.Status == req.Status.Value);

        if (req.LinkId.HasValue)
            query = query.Where(r => r.LinkId == req.LinkId.Value);

        if (req.PendingInviteId.HasValue)
            query = query.Where(r => r.PendingInviteId == req.PendingInviteId.Value);

        if (req.PlanId.HasValue)
            query = query.Where(r => r.PlanId == req.PlanId.Value);

        // Already scoped to the caller's own requests above, so this cannot reach another coach's rows.
        if (req.ClientId.HasValue)
            query = query.Where(r => r.Link != null && r.Link.ClientProfile.PublicId == req.ClientId.Value);

        var totalCount = await query.CountAsync(ct);

        HttpContext.Response.Headers["X-Total-Count"] = totalCount.ToString();

        var items = await query
            .OrderByDescending(r => r.CreatedAt)
            .Skip((req.Page - 1) * req.PageSize)
            .Take(req.PageSize)
            .Select(r => new PhotoDiaryRequestSummary
            {
                Id = r.Id,
                ProfessionalId = r.ProfessionalId,
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

        // EndsAt and IsOpen are computed in memory, not translated to SQL.
        var now = timeProvider.GetUtcNow();

        foreach (var item in items)
        {
            item.EndsAt = item.Mode == PhotoDiaryMode.Bulk ? null : item.CreatedAt.AddDays(item.DurationDays);
            item.IsOpen = PhotoDiaryRequest.IsOpenStatus(item.Status) && (item.EndsAt is null || now < item.EndsAt);
        }

        await Send.OkAsync(new ListTrainerRequestsResponse { Items = items }, ct);
    }
}
