using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Domain.Services;

/// <summary>
/// Finds a promotion by the code a coach typed and checks that this coach, plan and billing interval qualify. Shared by the
/// checkout and by applying a code to a live subscription, so the two cannot disagree. It takes plain values rather than a
/// subscription row, because a coach at checkout has no paid row yet. Run it before any call to the payment provider.
/// </summary>
public static class PromotionEligibility
{
    /// <summary>
    /// The code-mode promotion with this code that no admin ended and whose claim window has not closed; the latest
    /// claim-from when several promotions once shared the code. Null when there is none.
    /// </summary>
    /// <param name="db">Application database context.</param>
    /// <param name="code">The code as typed; compared ignoring case.</param>
    /// <param name="now">The current instant.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<Promotion?> FindByCodeAsync(
        IApplicationDbContext db, string code, DateTimeOffset now, CancellationToken ct)
    {
        var normalized = code.ToUpperInvariant();
        var sameCode = await db.Promotions
            .AsNoTracking()
            .Where(p => p.Mode == PromotionMode.Code && p.CodeNormalized == normalized && p.EndedAt == null)
            .ToListAsync(ct);

        return sameCode
            .Where(p => !PromotionRules.HasClaimWindowClosed(p, now))
            .OrderByDescending(p => p.ClaimFrom)
            .ThenBy(p => p.Id)
            .FirstOrDefault();
    }

    /// <summary>
    /// Runs the rules in a fixed order and returns the first one the promotion fails, or null when the coach qualifies.
    /// </summary>
    /// <param name="db">Application database context.</param>
    /// <param name="promotion">The promotion to test.</param>
    /// <param name="planCode">The plan the coach is on or is buying.</param>
    /// <param name="interval">The billing interval of that plan.</param>
    /// <param name="profileId">The coach's profile id.</param>
    /// <param name="firstPaidAt">When the coach first paid, or null for a coach who never did.</param>
    /// <param name="now">The current instant.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<PromotionRefusal?> CheckAsync(
        IApplicationDbContext db,
        Promotion promotion,
        string planCode,
        BillingInterval interval,
        long profileId,
        DateTimeOffset? firstPaidAt,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (now < promotion.ClaimFrom)
        {
            return new PromotionRefusal(ErrorCodes.PromotionCodeNotStarted, "The promotion has not started yet.");
        }

        if (!PromotionRules.CoversPlan(promotion, planCode))
        {
            return new PromotionRefusal(ErrorCodes.PromotionCodePlanNotEligible, "The promotion does not cover your plan.");
        }

        if (!PromotionRules.CoversInterval(promotion, interval))
        {
            return new PromotionRefusal(
                ErrorCodes.PromotionCodeIntervalNotEligible, "The promotion does not cover this billing interval.");
        }

        var usedCount = await db.PromotionRedemptions.CountAsync(r => r.PromotionId == promotion.Id, ct);

        if (PromotionRules.IsUsedUp(promotion, usedCount))
        {
            return new PromotionRefusal(ErrorCodes.PromotionCodeUsedUp, "Every use of the promotion is taken.");
        }

        if (promotion.NewCoachesOnly && firstPaidAt is not null)
        {
            return new PromotionRefusal(ErrorCodes.PromotionCodeNewCoachesOnly, "The promotion is for new coaches only.");
        }

        if (promotion.OneUsePerCoach
            && await db.PromotionRedemptions.AnyAsync(
                r => r.PromotionId == promotion.Id && r.ProfessionalProfileId == profileId, ct))
        {
            return new PromotionRefusal(ErrorCodes.PromotionCodeAlreadyUsed, "You already used this promotion.");
        }

        return null;
    }
}
