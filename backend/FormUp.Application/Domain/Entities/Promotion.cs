using FormUp.Application.Domain.Common;
using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Domain.Entities;

/// <summary>
/// Our copy of a Stripe coupon (and, in code mode, its promotion code) that discounts a coach's Form Up plan. Kept for
/// display, eligibility and the used count; Stripe computes the real charge. Status is derived, never stored.
/// </summary>
public class Promotion : PublicTimestampableEntity
{
    /// <summary>Admin-facing name.</summary>
    public required string Name { get; set; }

    /// <summary>Percentage or fixed amount.</summary>
    public PromotionKind Kind { get; set; }

    /// <summary>Percent (1 to 100), or the amount in the smallest unit of <see cref="Currency"/>.</summary>
    public long Value { get; set; }

    /// <summary>ISO 4217 currency of a fixed amount; null for a percentage.</summary>
    public string? Currency { get; set; }

    /// <summary>Automatic at checkout, or claimed with a code.</summary>
    public PromotionMode Mode { get; set; }

    /// <summary>The code as the admin typed it; null unless <see cref="Mode"/> is <see cref="PromotionMode.Code"/>.</summary>
    public string? Code { get; set; }

    /// <summary>Upper-cased <see cref="Code"/>, for case-insensitive lookups.</summary>
    public string? CodeNormalized { get; set; }

    /// <summary>How long the discount lasts for a coach.</summary>
    public PromotionDuration Duration { get; set; }

    /// <summary>Months the discount lasts (2 to 36); set only when <see cref="Duration"/> is <see cref="PromotionDuration.Months"/>.</summary>
    public int? DurationMonths { get; set; }

    /// <summary>When the promotion becomes claimable.</summary>
    public DateTimeOffset ClaimFrom { get; set; }

    /// <summary>When the promotion stops being claimable; null for no end.</summary>
    public DateTimeOffset? ClaimUntil { get; set; }

    /// <summary>Codes of the plans the promotion covers.</summary>
    public List<string> PlanCodes { get; set; } = [];

    /// <summary>
    /// True when Stripe discounts the second-role add-on line too: the coupon has no product restriction because every
    /// paid plan was selected. When false only the plan line is discounted.
    /// </summary>
    public bool CoversSecondRoleAddOn { get; set; }

    /// <summary>Total number of coaches who may claim it; null for no limit.</summary>
    public int? MaxUses { get; set; }

    /// <summary>True when only coaches who never paid before may claim it.</summary>
    public bool NewCoachesOnly { get; set; }

    /// <summary>True when a coach may claim it once.</summary>
    public bool OneUsePerCoach { get; set; }

    /// <summary>The Stripe coupon id.</summary>
    public required string StripeCouponId { get; set; }

    /// <summary>The Stripe promotion code id; null unless <see cref="Mode"/> is <see cref="PromotionMode.Code"/>.</summary>
    public string? StripePromotionCodeId { get; set; }

    /// <summary>The coaches who claimed it.</summary>
    public List<PromotionRedemption> Redemptions { get; set; } = [];
}
