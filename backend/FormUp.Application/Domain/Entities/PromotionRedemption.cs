using FormUp.Application.Domain.Common;

namespace FormUp.Application.Domain.Entities;

/// <summary>
/// A coach who claimed a <see cref="Promotion"/>. One row per coach and promotion, written from the provider's
/// subscription event, so the used count counts coaches.
/// </summary>
public class PromotionRedemption : TimestampableEntity
{
    /// <summary>The promotion claimed.</summary>
    public required long PromotionId { get; set; }

    /// <summary>Navigation to the promotion claimed.</summary>
    public Promotion Promotion { get; set; } = null!;

    /// <summary>The coach who claimed it.</summary>
    public required long ProfessionalProfileId { get; set; }

    /// <summary>Navigation to the coach.</summary>
    public ProfessionalProfile ProfessionalProfile { get; set; } = null!;

    /// <summary>When the provider event that showed the claim was applied.</summary>
    public DateTimeOffset RedeemedAt { get; set; }
}
