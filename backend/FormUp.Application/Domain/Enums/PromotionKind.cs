namespace FormUp.Application.Domain.Enums;

/// <summary>
/// How a <see cref="Entities.Promotion"/> takes money off the bill.
/// </summary>
public enum PromotionKind
{
    /// <summary>A percentage of the bill; the value is 1 to 100.</summary>
    Percent,

    /// <summary>A fixed amount per invoice; the value is in the smallest unit of the promotion's currency.</summary>
    FixedAmount
}
