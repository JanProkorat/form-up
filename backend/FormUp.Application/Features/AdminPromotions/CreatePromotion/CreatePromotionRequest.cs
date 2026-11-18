using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Features.AdminPromotions.CreatePromotion;

/// <summary>
/// Request for creating a promotion.
/// </summary>
public class CreatePromotionRequest
{
    /// <summary>Admin-facing name, up to 200 characters.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Percentage or fixed amount.</summary>
    public PromotionKind Kind { get; set; }

    /// <summary>Percent (1 to 100), or the amount in the smallest unit of <see cref="Currency"/>.</summary>
    public long Value { get; set; }

    /// <summary>Currency of a fixed amount; only CZK is accepted. Ignored for a percentage.</summary>
    public string? Currency { get; set; }

    /// <summary>Automatic at checkout, or claimed with a code.</summary>
    public PromotionMode Mode { get; set; }

    /// <summary>The code, letters and digits only; required in code mode and absent in automatic mode.</summary>
    public string? Code { get; set; }

    /// <summary>How long the discount lasts for a coach.</summary>
    public PromotionDuration Duration { get; set; }

    /// <summary>Months the discount lasts, 2 to 36; required for <see cref="PromotionDuration.Months"/>, absent otherwise.</summary>
    public int? DurationMonths { get; set; }

    /// <summary>When the promotion becomes claimable.</summary>
    public DateTimeOffset ClaimFrom { get; set; }

    /// <summary>When the promotion stops being claimable; null for no end. Must be after <see cref="ClaimFrom"/>.</summary>
    public DateTimeOffset? ClaimUntil { get; set; }

    /// <summary>Codes of the paid plans the promotion covers; at least one.</summary>
    public List<string> PlanCodes { get; set; } = [];

    /// <summary>Most coaches who may claim it, at least 1; null for no limit.</summary>
    public int? MaxUses { get; set; }

    /// <summary>True to limit it to coaches who never paid before.</summary>
    public bool NewCoachesOnly { get; set; }

    /// <summary>True to let a coach claim it once.</summary>
    public bool OneUsePerCoach { get; set; }
}
