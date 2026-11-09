namespace FormUp.Application.Features.AdminCoaches.DisableCoachAccountByAdmin;

/// <summary>
/// Request for disabling a coach's account as an admin.
/// </summary>
public class DisableCoachAccountByAdminRequest
{
    /// <summary>Route parameter — the coach's user id; matches <c>{CoachId}</c> in the route exactly.</summary>
    public Guid CoachId { get; set; }

    /// <summary>Why the account is disabled, at most 300 characters. Stored in the coach's history.</summary>
    public string Reason { get; set; } = string.Empty;
}
