namespace FormUp.Application.Features.Billing.CreateCheckoutSession;

/// <summary>
/// Request for starting a Stripe Checkout for the caller's own subscription.
/// </summary>
public class CreateCheckoutSessionRequest
{
    /// <summary>Code of the paid plan to buy. Never the Free plan.</summary>
    public string PlanCode { get; set; } = string.Empty;

    /// <summary>True to add the second-role add-on line. Refused on a plan that already includes both roles.</summary>
    public bool IncludeSecondRoleAddOn { get; set; }
}
