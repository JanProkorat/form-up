using FormUp.Application.Domain.Enums;
using Stripe;

namespace FormUp.Application.Infrastructure.Services.Billing;

/// <summary>
/// Pure reads of Stripe webhook objects: status mapping, period end and the subscription an invoice belongs to.
/// </summary>
public static class StripeWebhookMapper
{
    /// <summary>
    /// Maps a Stripe subscription status to the local one. Returns null for a status with no local meaning, so the
    /// caller keeps the current status.
    /// </summary>
    /// <param name="stripeStatus">The Stripe status string.</param>
    public static SubscriptionStatus? ToStatus(string? stripeStatus) => stripeStatus switch
    {
        "active" => SubscriptionStatus.Active,
        "trialing" => SubscriptionStatus.Trialing,
        "past_due" or "unpaid" => SubscriptionStatus.PastDue,
        "canceled" or "incomplete_expired" => SubscriptionStatus.Canceled,
        "incomplete" => SubscriptionStatus.Incomplete,
        _ => null,
    };

    /// <summary>The latest end of a current billing period across the subscription's items, or null with no items.</summary>
    /// <param name="subscription">The Stripe subscription.</param>
    public static DateTimeOffset? LatestPeriodEnd(Subscription subscription) =>
        subscription.Items?.Data is { Count: > 0 } items
            ? ToUtc(items.Max(item => item.CurrentPeriodEnd))
            : null;

    /// <summary>The id of the subscription an invoice bills, or null for a one-off invoice.</summary>
    /// <param name="invoice">The Stripe invoice.</param>
    public static string? SubscriptionIdOf(Invoice invoice) => invoice.Parent?.SubscriptionDetails?.SubscriptionId;

    /// <summary>
    /// The service period an invoice pays for: the span of its non-proration subscription lines, else of all subscription
    /// lines, else the invoice-level period (which is the billing run, not the service period).
    /// </summary>
    /// <param name="invoice">The Stripe invoice.</param>
    public static (DateTimeOffset Start, DateTimeOffset End) BillingPeriodOf(Invoice invoice)
    {
        // UNVERIFIED: the webhook payload carries every invoice line, with no paging — https://docs.stripe.com/api/invoices/object
        var lines = invoice.Lines?.Data ?? [];
        var subscriptionLines = lines.Where(line => line.Parent?.Type == "subscription_item_details").ToList();
        var regularLines = subscriptionLines.Where(line => line.Parent?.SubscriptionItemDetails?.Proration == false).ToList();
        var chosen = regularLines.Count > 0 ? regularLines : subscriptionLines;

        if (chosen.Count == 0)
        {
            return (ToUtc(invoice.PeriodStart), ToUtc(invoice.PeriodEnd));
        }

        return (ToUtc(chosen.Min(line => line.Period.Start)), ToUtc(chosen.Max(line => line.Period.End)));
    }

    /// <summary>The total discount of an invoice in minor units, summed over its discount entries; 0 when none.</summary>
    /// <param name="invoice">The Stripe invoice.</param>
    public static long TotalDiscountOf(Invoice invoice) => invoice.TotalDiscountAmounts?.Sum(discount => discount.Amount) ?? 0;

    /// <summary>Reads a Stripe timestamp as UTC.</summary>
    /// <param name="value">The Stripe timestamp.</param>
    public static DateTimeOffset ToUtc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
