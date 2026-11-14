namespace FormUp.Application.Features.Billing.CreatePortalSession;

/// <summary>
/// The Stripe Customer Portal page to send the coach to.
/// </summary>
public class CreatePortalSessionResponse
{
    /// <summary>URL of the portal session.</summary>
    public string Url { get; set; } = string.Empty;
}
