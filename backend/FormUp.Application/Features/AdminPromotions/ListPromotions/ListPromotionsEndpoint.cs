using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.AdminPromotions.Shared;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.AdminPromotions.ListPromotions;

/// <summary>
/// Lists the promotions for the admin Promotions page. The catalog is small, so every promotion is loaded with one
/// grouped count of its redemptions and the status filter runs on the derived status.
/// </summary>
/// <param name="db">Application database context.</param>
/// <param name="timeProvider">Clock.</param>
internal sealed class ListPromotionsEndpoint(IApplicationDbContext db, TimeProvider timeProvider)
    : Endpoint<ListPromotionsRequest, ListPromotionsResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/admin/promotions");
        Roles(AppRoles.Admin);
        Summary(s =>
        {
            s.Summary = "List promotions";
            s.Description = "Returns the plan promotions with their derived status (Scheduled before the claim window opens, Ended " +
                            "after it closes or once the max uses are reached, otherwise Active) and how many coaches used each. " +
                            "Filter by status; All when omitted. Newest claim-from first.";
            s.Response<ListPromotionsResponse>(StatusCodes.Status200OK, "The promotions of the tab");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest, "Invalid status filter");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or invalid credentials");
            s.Response(StatusCodes.Status403Forbidden, "Caller is not an Admin");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(ListPromotionsRequest req, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        var promotions = await db.Promotions
            .AsNoTracking()
            .ToListAsync(ct);
        var usedCounts = await db.PromotionRedemptions
            .AsNoTracking()
            .GroupBy(r => r.PromotionId)
            .Select(group => new { PromotionId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.PromotionId, row => row.Count, ct);

        var items = promotions
            .Select(promotion => PromotionDto.FromEntity(promotion, usedCounts.GetValueOrDefault(promotion.Id), now))
            .Where(dto => Matches(req.Status, dto.Status))
            .OrderByDescending(dto => dto.ClaimFrom)
            .ThenByDescending(dto => dto.CreatedAt)
            .ToList();

        await Send.OkAsync(new ListPromotionsResponse { Items = items }, ct);
    }

    private static bool Matches(PromotionStatusFilter filter, PromotionStatus status) => filter switch
    {
        PromotionStatusFilter.Active => status == PromotionStatus.Active,
        PromotionStatusFilter.Scheduled => status == PromotionStatus.Scheduled,
        PromotionStatusFilter.Ended => status == PromotionStatus.Ended,
        _ => true,
    };
}
