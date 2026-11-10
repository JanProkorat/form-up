using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Features.ClientMeasurements.GetMeasurements;
using FormUp.Application.Features.ClientMeasurements.Shared;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.ClientMeasurements.GetClientMeasurements;

/// <summary>
/// Allows a trainer or nutritionist to retrieve a client's body measurements.
/// Verifies an active professional-client link exists before granting access.
/// </summary>
/// <param name="db">Database context.</param>
/// <param name="audit">Audit logging service.</param>
/// <param name="linkAuthorizationService">Link capability service — measurements are not
/// domain-specific, so any active link (regardless of which domain(s) it grants) is sufficient.</param>
public class GetClientMeasurementsEndpoint(
    IApplicationDbContext db, IAuditService audit, IClientLinkAuthorizationService linkAuthorizationService)
    : Endpoint<GetClientMeasurementsRequest, GetMeasurementsResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/trainer/clients/{ClientId}/measurements");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Get a client's body measurements";
            s.Description = "Returns a paginated list of body measurements for the specified client. Requires an active trainer-client relationship.";
            s.Responses[404] = "Client not found or no active relationship";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(GetClientMeasurementsRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);

        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var trainerId = Guid.Parse(userId);

        var clientProfile = await db.ClientProfiles
            .FirstOrDefaultAsync(cp => cp.PublicId == req.ClientId, ct);

        if (clientProfile is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // Verify an active professional-client link exists. Measurements are not domain-specific
        // (neither training- nor nutrition-only) — the pre-migration check gated on IsActive
        // alone, with no capability-flag requirement, so any active link (regardless of which
        // domain(s) it grants) remains sufficient here.
        var capabilities = await linkAuthorizationService.GetCapabilitiesByClientPublicIdAsync(
            trainerId, req.ClientId, ct);

        if (capabilities is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var query = db.BodyMeasurements
            .Where(m => m.ClientProfileId == clientProfile.Id);

        if (req.From.HasValue)
        {
            var fromUtc = req.From.Value.ToUtcKind();
            query = query.Where(m => m.MeasuredAt >= fromUtc);
        }

        if (req.To.HasValue)
        {
            var toUtc = req.To.Value.ToUtcKind();
            query = query.Where(m => m.MeasuredAt <= toUtc);
        }

        var totalCount = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(m => m.MeasuredAt)
            .Skip((req.Page - 1) * req.PageSize)
            .Take(req.PageSize)
            .Select(m => MeasurementDto.FromEntity(m))
            .ToListAsync(ct);

        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();

        await audit.LogAsync(
            trainerId,
            "Read",
            nameof(BodyMeasurement),
            clientProfile.PublicId,
            ipAddress,
            ct: ct);

        await Send.OkAsync(new GetMeasurementsResponse
        {
            Items = items,
            TotalCount = totalCount,
            Page = req.Page,
            PageSize = req.PageSize
        }, ct);
    }
}
