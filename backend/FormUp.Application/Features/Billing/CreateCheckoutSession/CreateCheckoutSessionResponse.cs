namespace FormUp.Application.Features.Billing.CreateCheckoutSession;

/// <summary>
/// The hosted Stripe Checkout page to send the coach to.
/// </summary>
public class CreateCheckoutSessionResponse
{
    /// <summary>URL of the Checkout session.</summary>
    public string Url { get; set; } = string.Empty;
}
