namespace FormUp.Application.Features.AdminOverview.GetAdminAttention;

/// <summary>
/// The "needs attention" block of the admin overview: failed payments, cancelling coaches, gifts about to end and
/// promotions about to run out.
/// </summary>
public class GetAdminAttentionResponse
{
    /// <summary>Number of paying coaches whose last payment failed.</summary>
    public int PaymentFailedCount { get; set; }

    /// <summary>Up to three of those coaches, ordered by last name, first name and id.</summary>
    public List<AdminAttentionCoachDto> PaymentFailedCoaches { get; set; } = [];

    /// <summary>Number of paying coaches who cancelled their renewal.</summary>
    public int CancellingCount { get; set; }

    /// <summary>Earliest paid-period end among the cancelling coaches; null when there is none.</summary>
    public DateTimeOffset? CancellingEarliestPeriodEndsAt { get; set; }

    /// <summary>Latest paid-period end among the cancelling coaches; null when there is none.</summary>
    public DateTimeOffset? CancellingLatestPeriodEndsAt { get; set; }

    /// <summary>Number of given-free plans that end within the next 14 days (open-ended gifts excluded).</summary>
    public int GivenFreeEndingSoonCount { get; set; }

    /// <summary>Active promotions whose claim window closes within 14 days or whose uses are at least 90 % taken.</summary>
    public List<AdminAttentionPromotionDto> PromotionsEndingSoon { get; set; } = [];
}

/// <summary>
/// A coach named in the attention block.
/// </summary>
public class AdminAttentionCoachDto
{
    /// <summary>The coach's user id, the same id the Coaches list uses.</summary>
    public Guid CoachId { get; set; }

    /// <summary>Display name.</summary>
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// A promotion named in the attention block.
/// </summary>
public class AdminAttentionPromotionDto
{
    /// <summary>Public id of the promotion.</summary>
    public Guid PromotionId { get; set; }

    /// <summary>Admin-facing name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>When the claim window closes; null for no end.</summary>
    public DateTimeOffset? ClaimUntil { get; set; }

    /// <summary>How many coaches claimed it.</summary>
    public int UsedCount { get; set; }

    /// <summary>Use limit; null for no limit.</summary>
    public int? MaxUses { get; set; }
}
