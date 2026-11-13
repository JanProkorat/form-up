namespace FormUp.Application.Domain.Enums;

/// <summary>
/// The clients-page tabs served by <c>GET /trainer/clients</c>. Pending invitations are not a
/// tab here — they come from a different endpoint.
/// </summary>
public enum ClientListTab
{
    /// <summary>Every link, archived ones included.</summary>
    All,

    /// <summary>Live links inside the plan in force.</summary>
    Active,

    /// <summary>Live links outside the plan in force (over its client cap).</summary>
    OutsidePlan,

    /// <summary>Ended links.</summary>
    Archived
}
