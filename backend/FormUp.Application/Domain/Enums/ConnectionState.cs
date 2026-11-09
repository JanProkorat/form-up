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
}
