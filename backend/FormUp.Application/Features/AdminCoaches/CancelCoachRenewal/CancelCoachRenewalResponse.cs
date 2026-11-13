namespace FormUp.Application.Features.AdminCoaches.CancelCoachRenewal;

/// <summary>
/// The coach's subscription after the renewal was cancelled.
/// </summary>
public class CancelCoachRenewalResponse
{
    /// <summary>The coach's user id.</summary>
    public Guid CoachId { get; set; }

    /// <summary>When the paid period, and so the plan, ends; null when unknown.</summary>
    public DateTimeOffset? CurrentPeriodEndsAt { get; set; }

    /// <summary>True when the renewal was already cancelled, so this call changed nothing and wrote no history entry.</summary>
    public bool AlreadyCancelled { get; set; }
}
