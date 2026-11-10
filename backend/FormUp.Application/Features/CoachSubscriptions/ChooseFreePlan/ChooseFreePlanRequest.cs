namespace FormUp.Application.Features.CoachSubscriptions.ChooseFreePlan;

/// <summary>
/// The coach's choice of the Free plan after an admin-given plan ended.
/// </summary>
public class ChooseFreePlanRequest
{
    /// <summary>
    /// Public ids of the clients to keep on the Free plan: unique, and exactly as many as the Free plan's cap.
    /// Required when the coach has more live clients than the cap; ignored otherwise.
    /// </summary>
    public List<Guid>? KeepClientIds { get; set; }
}
