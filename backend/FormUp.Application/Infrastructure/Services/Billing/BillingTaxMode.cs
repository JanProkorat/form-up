namespace FormUp.Application.Infrastructure.Services.Billing;

/// <summary>
/// How VAT is handled on Stripe subscriptions and invoices. Selected by configuration, never by code.
/// </summary>
public enum BillingTaxMode
{
    /// <summary>The seller is not a VAT payer: no tax is charged and invoices carry the non-VAT-payer note.</summary>
    None,

    /// <summary>The seller charges 21 % Czech VAT through the Stripe tax rate named by <see cref="BillingOptions.VatTaxRateId"/>.</summary>
    Vat21,
}
