namespace FormUp.Application.Features.AdminCoaches.CancelCoachRenewal;

/// <summary>
/// Request for cancelling a coach's renewal.
/// </summary>
public class CancelCoachRenewalRequest
{
    /// <summary>Route parameter — the coach's user id; matches <c>{CoachId}</c> in the route exactly.</summary>
    public Guid CoachId { get; set; }

    /// <summary>Why the renewal is cancelled, at most 300 characters. Stored in the coach's history.</summary>
    public string Reason { get; set; } = string.Empty;
}
