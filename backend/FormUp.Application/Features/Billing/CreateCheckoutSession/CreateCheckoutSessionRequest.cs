using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Features.Billing.CreateCheckoutSession;

/// <summary>
/// Request for starting a Stripe Checkout for the caller's own subscription.
/// </summary>
public class CreateCheckoutSessionRequest
{
    /// <summary>Code of the paid plan to buy. Never the Free plan.</summary>
    public string PlanCode { get; set; } = string.Empty;

    /// <summary>Ignored: the server bills the second-role add-on from the coach's active roles. Kept so existing clients still bind.</summary>
    public bool IncludeSecondRoleAddOn { get; set; }

    /// <summary>Where the coach started, so the return URL lands on the right page. Never a URL.</summary>
    public CheckoutFlow Flow { get; set; } = CheckoutFlow.Settings;
}
