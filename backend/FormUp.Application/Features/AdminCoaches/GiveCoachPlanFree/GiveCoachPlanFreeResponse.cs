namespace FormUp.Application.Features.AdminCoaches.GiveCoachPlanFree;

/// <summary>
/// The coach's plan after it was given for free.
/// </summary>
public class GiveCoachPlanFreeResponse
{
    /// <summary>The coach's user id.</summary>
    public Guid CoachId { get; set; }

    /// <summary>Code of the plan given.</summary>
    public string PlanCode { get; set; } = string.Empty;

    /// <summary>When the plan was given.</summary>
    public DateTimeOffset GivenFreeAt { get; set; }

    /// <summary>When the gift ends; null means it has no end.</summary>
    public DateTimeOffset? FreeUntil { get; set; }

    /// <summary>Amount refunded to the card in the smallest currency unit; null when nothing was refunded.</summary>
    public long? RefundedMinorUnits { get; set; }

    /// <summary>Upper-case ISO 4217 currency of the refund; null when nothing was refunded.</summary>
    public string? RefundCurrency { get; set; }
}
