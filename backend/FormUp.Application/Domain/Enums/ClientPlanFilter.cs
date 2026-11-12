namespace FormUp.Application.Domain.Enums;

/// <summary>
/// Filters the clients list by the state of the client's training or nutrition plan.
/// Archived rows never match.
/// </summary>
public enum ClientPlanFilter
{
    /// <summary>The client has a current in-window plan (status Active).</summary>
    Active,

    /// <summary>The client has no current plan (status Paused).</summary>
    None,

    /// <summary>A current plan's window ends within 14 days.</summary>
    EndingSoon
}
