namespace FormUp.Application.Features.AdminCoaches.DisableCoachAccountByAdmin;

/// <summary>
/// Outcome of disabling a coach account as an admin.
/// </summary>
public class DisableCoachAccountByAdminResponse
{
    /// <summary>The coach's user id.</summary>
    public Guid CoachId { get; set; }

    /// <summary>When the coach account stops being active (UTC). At or before now means it already ended.</summary>
    public DateTime ActiveUntil { get; set; }

    /// <summary>Coach roles removed by this call; empty while the disable is only pending.</summary>
    public List<string> RolesRemoved { get; set; } = [];
}
