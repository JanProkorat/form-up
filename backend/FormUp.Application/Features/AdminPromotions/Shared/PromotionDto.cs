using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Services;

namespace FormUp.Application.Features.AdminPromotions.Shared;

/// <summary>
/// One promotion as the admin list and the create response show it.
/// </summary>
public class PromotionDto
{
    /// <summary>Public id of the promotion.</summary>
    public Guid PromotionId { get; set; }

    /// <summary>Admin-facing name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Percentage or fixed amount.</summary>
    public PromotionKind Kind { get; set; }

    /// <summary>Percent (1 to 100), or the amount in the smallest unit of <see cref="Currency"/>.</summary>
    public long Value { get; set; }

    /// <summary>ISO 4217 currency of a fixed amount; null for a percentage.</summary>
    public string? Currency { get; set; }

    /// <summary>Automatic at checkout, or claimed with a code.</summary>
    public PromotionMode Mode { get; set; }

    /// <summary>The code; null in automatic mode.</summary>
    public string? Code { get; set; }

    /// <summary>How long the discount lasts for a coach.</summary>
    public PromotionDuration Duration { get; set; }

    /// <summary>Months the discount lasts; set only for <see cref="PromotionDuration.Months"/>.</summary>
    public int? DurationMonths { get; set; }

    /// <summary>When the promotion becomes claimable.</summary>
    public DateTimeOffset ClaimFrom { get; set; }

    /// <summary>When the promotion stops being claimable; null for no end.</summary>
    public DateTimeOffset? ClaimUntil { get; set; }

    /// <summary>Codes of the plans the promotion covers.</summary>
    public List<string> PlanCodes { get; set; } = [];

    /// <summary>Most coaches who may claim it; null for no limit.</summary>
    public int? MaxUses { get; set; }

    /// <summary>How many coaches claimed it.</summary>
    public int UsedCount { get; set; }

    /// <summary>True when only coaches who never paid before may claim it.</summary>
    public bool NewCoachesOnly { get; set; }

    /// <summary>True when a coach may claim it once.</summary>
    public bool OneUsePerCoach { get; set; }

    /// <summary>Derived status: Scheduled, Active or Ended.</summary>
    public PromotionStatus Status { get; set; }

    /// <summary>When the promotion was created (UTC).</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Maps a promotion and its used count to the DTO.</summary>
    /// <param name="promotion">The promotion.</param>
    /// <param name="usedCount">How many coaches claimed it.</param>
    /// <param name="now">The current instant, for the derived status.</param>
    public static PromotionDto FromEntity(Promotion promotion, int usedCount, DateTimeOffset now) => new()
    {
        PromotionId = promotion.PublicId,
        Name = promotion.Name,
        Kind = promotion.Kind,
        Value = promotion.Value,
        Currency = promotion.Currency,
        Mode = promotion.Mode,
        Code = promotion.Code,
        Duration = promotion.Duration,
        DurationMonths = promotion.DurationMonths,
        ClaimFrom = promotion.ClaimFrom,
        ClaimUntil = promotion.ClaimUntil,
        PlanCodes = promotion.PlanCodes.ToList(),
        MaxUses = promotion.MaxUses,
        UsedCount = usedCount,
        NewCoachesOnly = promotion.NewCoachesOnly,
        OneUsePerCoach = promotion.OneUsePerCoach,
        Status = PromotionRules.StatusAt(promotion, usedCount, now),
        CreatedAt = promotion.DateCreated,
    };
}
