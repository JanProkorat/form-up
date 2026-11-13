using FormUp.Application.Domain.Common;

namespace FormUp.Application.Domain.Entities;

/// <summary>
/// One price a <see cref="SubscriptionPlan"/> has been sold at. A plan has exactly one open row
/// (<see cref="ValidTo"/> is null); a new row is written only when the price, the second-role
/// add-on price or the currency changes, so coaches already on the plan can stay on the row
/// they pay.
/// </summary>
public class SubscriptionPlanPrice : TimestampableEntity
{
    /// <summary>
    /// Foreign key to the plan this price belongs to.
    /// </summary>
    public required long SubscriptionPlanId { get; set; }

    /// <summary>
    /// Navigation property to the plan this price belongs to.
    /// </summary>
    public SubscriptionPlan SubscriptionPlan { get; set; } = null!;

    /// <summary>
    /// Plan price, expressed in the smallest unit of <see cref="Currency"/>.
    /// </summary>
    public long PriceMinorUnits { get; set; }

    /// <summary>
    /// Price of the second-role add-on in the same unit. Null when the plan includes both roles.
    /// </summary>
    public long? SecondRoleAddOnPriceMinorUnits { get; set; }

    /// <summary>
    /// ISO 4217 currency code for the prices on this row.
    /// </summary>
    public required string Currency { get; set; }

    /// <summary>
    /// External payment-provider price identifier for this row. Populated once billing ships.
    /// </summary>
    public string? ExternalPriceId { get; set; }

    /// <summary>
    /// When this price started to apply (UTC).
    /// </summary>
    public DateTime ValidFrom { get; set; }

    /// <summary>
    /// When this price stopped being the plan's current price (UTC). Null on the open row.
    /// </summary>
    public DateTime? ValidTo { get; set; }
}
