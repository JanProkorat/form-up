using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Domain.Services;

/// <summary>
/// Picks the one automatic promotion that applies to a coach. Automatic promotions may overlap; among those the coach
/// qualifies for, the latest claim-from wins and the lowest id breaks a tie. The winner is per coach, not per plan: a
/// plan the winner does not cover gets no promotion, never a fallback to another one.
/// </summary>
public static class PromotionSelector
{
    /// <summary>
    /// Picks the winning automatic promotion among the candidates the coach qualifies for. Pure, so the rule is tested
    /// without a database.
    /// </summary>
    /// <param name="candidates">Automatic promotions with how many coaches claimed each.</param>
    /// <param name="redeemedPromotionIds">Ids of the promotions this coach already claimed.</param>
    /// <param name="isNewCoach">True when the coach never paid before.</param>
    /// <param name="now">The current instant.</param>
    public static Promotion? Pick(
        IEnumerable<(Promotion Promotion, int UsedCount)> candidates,
        IReadOnlySet<long> redeemedPromotionIds,
        bool isNewCoach,
        DateTimeOffset now) =>
        candidates
            .Where(c => c.Promotion.Mode == PromotionMode.Automatic)
            .Where(c => PromotionRules.StatusAt(c.Promotion, c.UsedCount, now) == PromotionStatus.Active)
            .Where(c => !c.Promotion.NewCoachesOnly || isNewCoach)
            .Where(c => !c.Promotion.OneUsePerCoach || !redeemedPromotionIds.Contains(c.Promotion.Id))
            .OrderByDescending(c => c.Promotion.ClaimFrom)
            .ThenBy(c => c.Promotion.Id)
            .Select(c => c.Promotion)
            .FirstOrDefault();

    /// <summary>
    /// Loads what <see cref="Pick"/> needs and returns the coach's winning automatic promotion, or null.
    /// </summary>
    /// <param name="db">Application database context.</param>
    /// <param name="profileId">The coach's profile id, or null when the caller has none (treated as a new coach).</param>
    /// <param name="now">The current instant.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<Promotion?> PickForCoachAsync(
        IApplicationDbContext db, long? profileId, DateTimeOffset now, CancellationToken ct)
    {
        var open = await db.Promotions
            .AsNoTracking()
            .Where(p => p.Mode == PromotionMode.Automatic && p.ClaimFrom <= now && (p.ClaimUntil == null || p.ClaimUntil >= now))
            .ToListAsync(ct);

        if (open.Count == 0)
        {
            return null;
        }

        var ids = open.Select(p => p.Id).ToList();
        var usedCounts = await db.PromotionRedemptions
            .AsNoTracking()
            .Where(r => ids.Contains(r.PromotionId))
            .GroupBy(r => r.PromotionId)
            .Select(group => new { PromotionId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.PromotionId, row => row.Count, ct);

        var redeemed = new HashSet<long>();
        var isNewCoach = true;

        if (profileId is { } id)
        {
            redeemed = (await db.PromotionRedemptions
                .AsNoTracking()
                .Where(r => r.ProfessionalProfileId == id)
                .Select(r => r.PromotionId)
                .ToListAsync(ct)).ToHashSet();

            isNewCoach = !await db.CoachSubscriptions
                .AsNoTracking()
                .AnyAsync(cs => cs.ProfessionalProfileId == id && cs.FirstPaidAt != null, ct);
        }

        return Pick(open.Select(p => (p, usedCounts.GetValueOrDefault(p.Id))), redeemed, isNewCoach, now);
    }
}
