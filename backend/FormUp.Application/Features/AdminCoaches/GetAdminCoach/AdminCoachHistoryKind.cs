namespace FormUp.Application.Features.AdminCoaches.GetAdminCoach;

/// <summary>
/// What happened in one entry of a coach's history.
/// </summary>
public enum AdminCoachHistoryKind
{
    /// <summary>The coach joined.</summary>
    Joined,

    /// <summary>An admin gave a plan for free.</summary>
    GivenFree,

    /// <summary>An admin assigned the subscription.</summary>
    PlanAssigned,

    /// <summary>An admin cancelled the renewal.</summary>
    RenewalCancelled,

    /// <summary>The coach account was disabled, by the coach or an admin.</summary>
    AccountDisabled,

    /// <summary>The coach kept their account after disabling it.</summary>
    AccountKept,

    /// <summary>The coach added a role.</summary>
    RoleAdded,

    /// <summary>The coach removed a role.</summary>
    RoleRemoved,
}
