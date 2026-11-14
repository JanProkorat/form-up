namespace FormUp.Application.Features.Billing.ConfirmSecondRoleAddOn;

/// <summary>
/// The outcome of paying the second-role add-on.
/// </summary>
public class ConfirmSecondRoleAddOnResponse
{
    /// <summary>True once the add-on is paid; the client then adds the role through <c>POST /users/me/roles</c>.</summary>
    public bool PaysSecondRoleAddOn { get; set; }
}
