using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Domain.Services;

/// <summary>
/// The derived state of a <see cref="Promotion"/>: status and plan coverage. One place, so the admin list, checkout,
/// the plans read and the code endpoint agree.
/// </summary>
public static class PromotionRules
{
    /// <summary>
    /// Scheduled before the claim window opens; Ended after it closes or once every use is taken; otherwise Active.
    /// </summary>
    /// <param name="promotion">The promotion.</param>
    /// <param name="usedCount">How many coaches claimed it.</param>
    /// <param name="now">The current instant.</param>
    public static PromotionStatus StatusAt(Promotion promotion, int usedCount, DateTimeOffset now)
    {
        if (HasClaimWindowClosed(promotion, now) || IsUsedUp(promotion, usedCount))
        {
            return PromotionStatus.Ended;
        }

        return now < promotion.ClaimFrom ? PromotionStatus.Scheduled : PromotionStatus.Active;
    }

    /// <summary>True when the claim window has a close and it has passed.</summary>
    /// <param name="promotion">The promotion.</param>
    /// <param name="now">The current instant.</param>
    public static bool HasClaimWindowClosed(Promotion promotion, DateTimeOffset now) =>
        promotion.ClaimUntil is { } until && now > until;

    /// <summary>True when the promotion has a use limit and the coaches who claimed it reach it.</summary>
    /// <param name="promotion">The promotion.</param>
    /// <param name="usedCount">How many coaches claimed it.</param>
    public static bool IsUsedUp(Promotion promotion, int usedCount) =>
        promotion.MaxUses is { } max && usedCount >= max;

    /// <summary>True when the plan is one the promotion covers.</summary>
    /// <param name="promotion">The promotion.</param>
    /// <param name="planCode">The plan code.</param>
    public static bool CoversPlan(Promotion promotion, string planCode) =>
        promotion.PlanCodes.Contains(planCode, StringComparer.OrdinalIgnoreCase);
}
