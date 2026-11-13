namespace FormUp.Application.Domain.Enums;

/// <summary>
/// Lifecycle state of a client-professional link. Values are persisted as integers: never renumber.
/// </summary>
public enum ConnectionState
{
    /// <summary>
    /// The collaboration is live.
    /// </summary>
    Connected = 0,

    /// <summary>
    /// The collaboration has ended.
    /// </summary>
    Ended = 1,

    /// <summary>
    /// The collaboration is live and the coach chose to keep this client after their paid plan lapsed.
    /// </summary>
    FreePlanKept = 2,

    /// <summary>
    /// The collaboration is live but the coach chose to release this client after their paid plan lapsed.
    /// </summary>
    FreePlanReleased = 3,
}
