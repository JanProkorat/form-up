namespace FormUp.Application.Features.AdminCoaches.Shared;

/// <summary>
/// Audit action names written by the admin coach actions (each at most 50 characters). Every row is filed under
/// the coach's <c>ApplicationUser.Id</c>.
/// </summary>
internal static class AdminCoachAuditActions
{
    /// <summary>An admin gave a plan to the coach for free.</summary>
    public const string GivePlanFree = "AdminGiveCoachPlanFree";

    /// <summary>An admin cancelled the coach's renewal.</summary>
    public const string CancelRenewal = "AdminCancelCoachRenewal";

    /// <summary>An admin assigned the coach's subscription (written by the subscription endpoint).</summary>
    public const string AssignSubscription = "SetCoachSubscription";

    /// <summary>The coach kept their account (written by the coach's own endpoint).</summary>
    public const string KeepAccount = "KeepCoachAccount";

    /// <summary>The coach added a role (written by the coach's own endpoint).</summary>
    public const string AddRole = "AddRole";

    /// <summary>The coach removed a coach role (written by the coach's own endpoint).</summary>
    public const string RemoveRole = "RemoveCoachRole";
}
