namespace FormUp.Application.Domain.Enums;

/// <summary>
/// Which billing cadences a <see cref="Entities.Promotion"/> discounts. Stored as an integer; <see cref="Both"/> is 0 so a
/// promotion created before the cadence existed keeps covering monthly and annual alike.
/// </summary>
public enum PromotionBillingPeriod
{
    /// <summary>Monthly and annual subscriptions.</summary>
    Both = 0,

    /// <summary>Monthly subscriptions only.</summary>
    Monthly = 1,

    /// <summary>Annual subscriptions only.</summary>
    Annual = 2
}
