using FormUp.Application.Domain.Common;

namespace FormUp.Application.Domain.Entities;

/// <summary>
/// One paid Stripe invoice of a coach subscription, kept for revenue reporting. Holds amounts and the coach reference
/// only: no card data, name or email. The row outlives the coach (the reference is nulled on deletion).
/// </summary>
public class PaidInvoice : TimestampableEntity
{
    /// <summary>
    /// The provider's invoice id. Unique.
    /// </summary>
    public required string ExternalInvoiceId { get; set; }

    /// <summary>
    /// The coach who paid; null once the coach's profile is deleted.
    /// </summary>
    public long? ProfessionalProfileId { get; set; }

    /// <summary>
    /// The coach's profile, when it still exists.
    /// </summary>
    public ProfessionalProfile? ProfessionalProfile { get; set; }

    /// <summary>
    /// Amount paid in minor currency units.
    /// </summary>
    public long AmountPaid { get; set; }

    /// <summary>
    /// Total discount applied to the invoice in minor currency units; 0 when none.
    /// </summary>
    public long DiscountAmount { get; set; }

    /// <summary>
    /// Lowercase ISO currency code as the provider sends it.
    /// </summary>
    public required string Currency { get; set; }

    /// <summary>
    /// Start of the service period the invoice pays for (UTC).
    /// </summary>
    public DateTimeOffset PeriodStart { get; set; }

    /// <summary>
    /// End of the service period the invoice pays for (UTC).
    /// </summary>
    public DateTimeOffset PeriodEnd { get; set; }

    /// <summary>
    /// When the invoice was paid (UTC).
    /// </summary>
    public DateTimeOffset PaidAt { get; set; }
}
