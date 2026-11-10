using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Features.ClientPhotos.Shared;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.ClientPhotos.GetMyPhotos;

/// <summary>
/// Returns a paginated, optionally month-grouped list of plan photos across ALL plans
/// for the currently authenticated client.
/// </summary>
/// <remarks>
/// <para>
/// Authorization: requires the <c>Client</c> role. The endpoint resolves the
/// caller's <c>ClientProfile</c> from the JWT <c>UserId</c> claim and scopes all
/// queries to that profile — no client ID is exposed in the URL.
/// </para>
/// <para>
/// Pagination: controlled by <c>page</c> / <c>pageSize</c> query params.
/// The <c>X-Total-Count</c> header carries the total count of matching photos
/// (or month groups when <c>groupByMonth=true</c>).
/// </para>
/// </remarks>
/// <param name="db">Relational database context (PostgreSQL via EF Core).</param>
/// <param name="blobStorage">Blob storage service — converts each stored BlobUrl into a
/// short-lived pre-signed read URL before the response leaves the process (F9).</param>
public class GetMyPhotosEndpoint(IApplicationDbContext db, IBlobStorageService blobStorage)
    : Endpoint<GetMyPhotosRequest, GetMyPhotosResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/client/me/photos");
        Roles(AppRoles.Client);
        Summary(s =>
        {
            s.Summary = "List my photos (client view)";
            s.Description = "Returns a paginated list of plan photos across all plans for the authenticated client. " +
                            "Supports category filter, date range filter, and optional month grouping. " +
                            "Total count (photos or groups) is in the X-Total-Count response header.";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(GetMyPhotosRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);

        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var applicationUserId = Guid.Parse(userId);

        // Resolve the client profile via the user ID from the JWT
        var clientProfile = await db.ClientProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(cp => cp.UserId == applicationUserId, ct);

        if (clientProfile is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // Build base query scoped to this client
        var query = db.PlanPhotos
            .AsNoTracking()
            .Where(p => p.ClientProfileId == clientProfile.Id);

        // Optional category filter
        if (req.Category.HasValue)
        {
            query = query.Where(p => p.Category == req.Category.Value);
        }

        // Optional date range filter on TakenAt (inclusive)
        // Query binding yields Local/Unspecified kinds; Npgsql only accepts UTC for timestamptz.
        if (req.From.HasValue)
        {
            var fromUtc = ToUtc(req.From.Value);
            query = query.Where(p => p.TakenAt >= fromUtc);
        }

        if (req.To.HasValue)
        {
            var toUtc = ToUtc(req.To.Value);
            query = query.Where(p => p.TakenAt <= toUtc);
        }

        if (req.GroupByMonth)
        {
            // Pull all matching photos for in-memory grouping.
            // GroupBy + pagination cannot be efficiently translated to SQL for
            // this shape, so we load a minimal projection and group in .NET.
            var allPhotos = await query
                .OrderByDescending(p => p.TakenAt)
                .Select(p => new ClientPhotoResponse
                {
                    Id = p.PublicId,
                    BlobUrl = p.BlobUrl,
                    Description = p.Description,
                    Category = p.Category,
                    PlanId = p.PlanId,
                    PlanType = p.PlanType,
                    MealLogId = p.MealLogId,
                    TakenAt = p.TakenAt,
                    UploadedByUserId = p.UploadedByUserId,
                    UploadedAt = p.DateCreated,
                    DiaryRequestId = p.DiaryRequestId,
                })
                .ToListAsync(ct);

            var groups = allPhotos
                .GroupBy(p => p.TakenAt.ToString("yyyy-MM"))
                .OrderByDescending(g => g.Key)
                .Select(g => new MonthGroupResponse
                {
                    YearMonth = g.Key,
                    Photos = g.ToList()
                })
                .ToList();

            var totalGroups = groups.Count;

            var pagedGroups = groups
                .Skip((req.Page - 1) * req.PageSize)
                .Take(req.PageSize)
                .ToList();

            // Sign only the photos actually being returned (post-pagination), not the full
            // in-memory grouping set — a stored BlobUrl is no longer publicly fetchable (F9).
            await SignPhotoUrlsAsync(pagedGroups.SelectMany(g => g.Photos), ct);

            HttpContext.Response.Headers["X-Total-Count"] = totalGroups.ToString();

            await Send.OkAsync(new GetMyPhotosResponse
            {
                Groups = pagedGroups
            }, ct);
        }
        else
        {
            var totalCount = await query.CountAsync(ct);

            var photos = await query
                .OrderByDescending(p => p.TakenAt)
                .Skip((req.Page - 1) * req.PageSize)
                .Take(req.PageSize)
                .Select(p => new ClientPhotoResponse
                {
                    Id = p.PublicId,
                    BlobUrl = p.BlobUrl,
                    Description = p.Description,
                    Category = p.Category,
                    PlanId = p.PlanId,
                    PlanType = p.PlanType,
                    MealLogId = p.MealLogId,
                    TakenAt = p.TakenAt,
                    UploadedByUserId = p.UploadedByUserId,
                    UploadedAt = p.DateCreated,
                    DiaryRequestId = p.DiaryRequestId,
                })
                .ToListAsync(ct);

            // A stored BlobUrl is no longer publicly fetchable — mint a short-lived read URL
            // for each photo before it leaves the process (F9).
            await SignPhotoUrlsAsync(photos, ct);

            HttpContext.Response.Headers["X-Total-Count"] = totalCount.ToString();

            await Send.OkAsync(new GetMyPhotosResponse
            {
                Photos = photos
            }, ct);
        }
    }

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Local => value.ToUniversalTime(),
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value,
    };

    /// <summary>
    /// Populates each photo's <see cref="ClientPhotoResponse.DisplayUrl"/> with a short-lived
    /// pre-signed read URL. Must run on every response path before <c>Send.OkAsync</c> — the
    /// bucket no longer grants public read on the <c>plan-photos/</c> prefix these photos live
    /// under. <see cref="ClientPhotoResponse.BlobUrl"/> is left untouched — it stays the
    /// canonical, permanent identity value.
    /// </summary>
    private async Task SignPhotoUrlsAsync(IEnumerable<ClientPhotoResponse> photos, CancellationToken ct)
    {
        foreach (var photo in photos)
        {
            photo.DisplayUrl = await blobStorage.GenerateReadUrlAsync(photo.BlobUrl, ct) ?? string.Empty;
        }
    }
}
