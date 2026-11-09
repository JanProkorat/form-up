namespace FormUp.Application.Features.AdminCoaches.GetAdminCoach;

/// <summary>
/// Request for one coach's admin detail.
/// </summary>
public class GetAdminCoachRequest
{
    /// <summary>Route parameter — the coach's user id; matches <c>{CoachId}</c> in the route exactly.</summary>
    public Guid CoachId { get; set; }
}
