using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Features.ClientMeasurements.Shared;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.ClientMeasurements.GetMeasurements;

/// <summary>
/// Retrieves a paginated list of body measurements for the authenticated client.
/// </summary>
/// <param name="db">Database context.</param>
public class GetMeasurementsEndpoint(IApplicationDbContext db) : Endpoint<GetMeasurementsRequest, GetMeasurementsResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/client/measurements");
        Roles(AppRoles.Client);
        Summary(s =>
        {
            s.Summary = "List body measurements";
            s.Description = "Returns a paginated list of body measurements for the authenticated client with optional date filtering.";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(GetMeasurementsRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);

        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var clientId = Guid.Parse(userId);

        var clientProfile = await db.ClientProfiles
            .FirstOrDefaultAsync(cp => cp.UserId == clientId, ct);

        if (clientProfile is null)
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

        HttpContext.Response.Headers["X-Total-Count"] = totalCount.ToString();

        await Send.OkAsync(new GetMeasurementsResponse
        {
            Items = items,
            TotalCount = totalCount,
            Page = req.Page,
            PageSize = req.PageSize
        }, ct);
    }
}
