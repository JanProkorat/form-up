namespace FormUp.Application.Infrastructure.Services.Billing;

/// <summary>
/// Stripe billing settings bound from the <c>Stripe</c> configuration section. <see cref="SecretKey"/> and
/// <see cref="WebhookSecret"/> come only from environment variables (<c>Stripe__SecretKey</c>,
/// <c>Stripe__WebhookSecret</c>) or user-secrets, never from a committed file.
/// </summary>
public class BillingOptions
{
    /// <summary>Name of the configuration section the options bind from.</summary>
    public const string SectionName = "Stripe";

    /// <summary>Stripe secret API key. Empty when Stripe is not configured.</summary>
    public string? SecretKey { get; set; }

    /// <summary>Signing secret of the Stripe webhook endpoint. Empty when webhooks are not configured.</summary>
    public string? WebhookSecret { get; set; }

    /// <summary>VAT handling for checkout, plan changes, add-on charges and invoices.</summary>
    public BillingTaxMode TaxMode { get; set; } = BillingTaxMode.None;

    /// <summary>Id of the Stripe tax rate (21 % DPH) used when <see cref="TaxMode"/> is <see cref="BillingTaxMode.Vat21"/>.</summary>
    public string? VatTaxRateId { get; set; }

    /// <summary>Where Stripe Checkout sends the coach after a completed payment. Configured, never taken from a request.</summary>
    public string? CheckoutSuccessUrl { get; set; }

    /// <summary>Where Stripe Checkout sends the coach after cancelling. Configured, never taken from a request.</summary>
    public string? CheckoutCancelUrl { get; set; }

    /// <summary>Where the Customer Portal returns the coach to. Configured, never taken from a request.</summary>
    public string? PortalReturnUrl { get; set; }

    /// <summary>
    /// True when billing calls can run: a secret key, absolute http(s) redirect URLs, and a tax rate id when VAT is on.
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(SecretKey)
        && IsHttpUrl(CheckoutSuccessUrl)
        && IsHttpUrl(CheckoutCancelUrl)
        && IsHttpUrl(PortalReturnUrl)
        && (TaxMode == BillingTaxMode.None || !string.IsNullOrWhiteSpace(VatTaxRateId));

    private static bool IsHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
}
