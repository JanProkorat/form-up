using System.Net;
using Stripe;

namespace FormUp.Application.Seed.WalkSeed;

/// <summary>
/// <see cref="IWalkStripeGateway"/> over the Stripe API. Only ever built with a test-mode key (the runner's guard).
/// </summary>
internal sealed class WalkStripeGateway(string secretKey) : IWalkStripeGateway
{
    private const string PlainCardToken = "pm_card_visa";

    // UNVERIFIED: pm_card_authenticationRequired is the test token for card 4000002760003184, which needs 3D Secure on
    // every payment — https://docs.stripe.com/testing#authentication-and-setup
    private const string ThreeDSecureCardToken = "pm_card_authenticationRequired";

    private readonly StripeClient stripe = new(secretKey);

    /// <inheritdoc />
    public async Task<WalkStripeSubscription> CreatePaidSubscriptionAsync(
        string walkKey, string email, string priceId, bool requireThreeDSecureOnLaterPayments, CancellationToken ct)
    {
        // No userId metadata on purpose: the billing webhook then never matches this customer to a coach by metadata,
        // so a late event cannot rewrite the row the seed writes.
        var customer = await stripe.V1.Customers.CreateAsync(
            new CustomerCreateOptions
            {
                Email = email,
                Name = $"Walk {walkKey}",
                Metadata = new Dictionary<string, string> { ["walkSeed"] = "true", ["walkKey"] = walkKey },
            },
            cancellationToken: ct);

        var plainCardId = await AttachAndMakeDefaultAsync(customer.Id, PlainCardToken, ct);

        // UNVERIFIED: off-session creation with a default card that needs no authentication charges the first invoice
        // at once and returns an active subscription — https://docs.stripe.com/api/subscriptions/create
        var created = await stripe.V1.Subscriptions.CreateAsync(
            new SubscriptionCreateOptions
            {
                Customer = customer.Id,
                Items = [new SubscriptionItemOptions { Price = priceId }],
                DefaultPaymentMethod = plainCardId,
                OffSession = true,
            },
            cancellationToken: ct);

        var subscription = await stripe.V1.Subscriptions.GetAsync(created.Id, cancellationToken: ct);

        if (subscription.Status != "active")
        {
            throw new InvalidOperationException(
                $"The Stripe sandbox subscription for walk account '{walkKey}' is '{subscription.Status}', not active.");
        }

        if (requireThreeDSecureOnLaterPayments)
        {
            var threeDSecureCardId = await AttachAndMakeDefaultAsync(customer.Id, ThreeDSecureCardToken, ct);

            await stripe.V1.Subscriptions.UpdateAsync(
                subscription.Id,
                new SubscriptionUpdateOptions { DefaultPaymentMethod = threeDSecureCardId },
                cancellationToken: ct);
        }

        var periodEnd = subscription.Items.Data.Max(item => item.CurrentPeriodEnd);

        return new WalkStripeSubscription(
            customer.Id, subscription.Id, new DateTimeOffset(DateTime.SpecifyKind(periodEnd, DateTimeKind.Utc)));
    }

    /// <inheritdoc />
    public async Task RetireAsync(string? customerId, string? subscriptionId, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(subscriptionId))
        {
            await IgnoringNotFoundAsync(() => stripe.V1.Subscriptions.CancelAsync(
                subscriptionId,
                new SubscriptionCancelOptions { InvoiceNow = false, Prorate = false },
                cancellationToken: ct));
        }

        if (!string.IsNullOrEmpty(customerId))
        {
            await IgnoringNotFoundAsync(() => stripe.V1.Customers.DeleteAsync(customerId, cancellationToken: ct));
        }
    }

    /// <summary>
    /// Attaches a test card token to the customer and makes the attached card the customer's default. The attach
    /// answers with a new payment method id, and that id (not the token) is what the defaults take.
    /// </summary>
    private async Task<string> AttachAndMakeDefaultAsync(string customerId, string cardToken, CancellationToken ct)
    {
        var attached = await stripe.V1.PaymentMethods.AttachAsync(
            cardToken, new PaymentMethodAttachOptions { Customer = customerId }, cancellationToken: ct);

        await stripe.V1.Customers.UpdateAsync(
            customerId,
            new CustomerUpdateOptions
            {
                InvoiceSettings = new CustomerInvoiceSettingsOptions { DefaultPaymentMethod = attached.Id },
            },
            cancellationToken: ct);

        return attached.Id;
    }

    // UNVERIFIED: cancelling an already-canceled subscription and deleting a deleted customer both answer 404 —
    // https://docs.stripe.com/api/subscriptions/cancel
    private static async Task IgnoringNotFoundAsync<T>(Func<Task<T>> call)
    {
        try
        {
            await call();
        }
        catch (StripeException ex) when (ex.HttpStatusCode == HttpStatusCode.NotFound)
        {
            // Already gone at Stripe: the goal of the call is met.
        }
    }
}
