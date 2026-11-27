namespace FormUp.Application.Features.Billing.ConfirmSecondRoleAddOn;

/// <summary>
/// The outcome of paying the second-role add-on.
/// </summary>
public class ConfirmSecondRoleAddOnResponse
{
    /// <summary>True once the add-on is paid; the client then adds the role through <c>POST /users/me/roles</c>.</summary>
    public bool PaysSecondRoleAddOn { get; set; }

    /// <summary>
    /// Stripe's hosted payment page when the card needs authentication: the add-on is not paid yet, and the role can be
    /// added once the payment goes through. Null otherwise.
    /// </summary>
    public string? PaymentUrl { get; set; }
}
