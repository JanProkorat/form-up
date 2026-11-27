namespace FormUp.Application.Domain.Enums;

/// <summary>
/// How long a <see cref="Entities.Promotion"/> discounts a coach's bill once claimed.
/// </summary>
public enum PromotionDuration
{
    /// <summary>The first invoice only.</summary>
    FirstMonth,

    /// <summary>A fixed number of months, given by <see cref="Entities.Promotion.DurationMonths"/>.</summary>
    Months,

    /// <summary>For as long as the subscription lasts.</summary>
    Forever
}
