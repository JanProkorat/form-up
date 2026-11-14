using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Domain.Interfaces;

/// <summary>
/// The payment provider seam: everything the backend asks of Stripe, so a merchant of record can replace it later.
/// Implementations throw <c>BillingProviderException</c> when the provider call fails.
/// </summary>
public interface ISubscriptionService
{
    /// <summary>
    /// True when billing calls can run. When false, checkout and portal answer 503 and admin plan edits skip the provider.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>Creates a provider customer for a coach and returns its id.</summary>
    /// <param name="userId">The coach's <c>ApplicationUser.Id</c>, stored as customer metadata.</param>
    /// <param name="email">The coach's email.</param>
    /// <param name="name">Display name, or null.</param>
    /// <param name="language">The coach's language (cs, en, de), or null for Czech.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<string> CreateCustomerAsync(Guid userId, string email, string? name, string? language, CancellationToken ct);

    /// <summary>
    /// Creates a hosted Checkout session for a plan and an optional second-role add-on line (no trial) and returns its URL.
    /// Promotion codes are allowed unless <paramref name="couponId"/> applies an automatic promotion.
    /// </summary>
    /// <param name="customerId">The provider customer id.</param>
    /// <param name="userId">The coach's <c>ApplicationUser.Id</c>, stored as subscription metadata.</param>
    /// <param name="planCode">The plan code, stored as subscription metadata.</param>
    /// <param name="planPriceExternalId">The provider price id of the plan's open price row.</param>
    /// <param name="addOnAmountMinorUnits">The second-role add-on price, or null for no add-on line.</param>
    /// <param name="currency">ISO 4217 currency of the add-on price.</param>
    /// <param name="interval">Billing cadence of the add-on price.</param>
    /// <param name="couponId">Automatic promotion coupon id, or null.</param>
    /// <param name="flow">Where the coach started, appended to the configured return URLs as a query parameter.</param>
    /// <param name="language">The coach's language (cs, en, de) for the hosted page, or null for Czech.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<string> CreateCheckoutSessionAsync(
        string customerId,
        Guid userId,
        string planCode,
        string planPriceExternalId,
        long? addOnAmountMinorUnits,
        string currency,
        BillingInterval interval,
        string? couponId,
        CheckoutFlow flow,
        string? language,
        CancellationToken ct);

    /// <summary>Creates a Customer Portal session and returns its URL.</summary>
    /// <param name="customerId">The provider customer id.</param>
    /// <param name="language">The coach's language (cs, en, de) for the hosted page, or null for Czech.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<string> CreatePortalSessionAsync(string customerId, string? language, CancellationToken ct);

    /// <summary>
    /// Moves an existing subscription to another plan price, immediately or scheduled for the next renewal. A scheduled
    /// change is merged with any given-free schedule already on the subscription.
    /// </summary>
    /// <param name="subscriptionId">The provider subscription id.</param>
    /// <param name="planCode">The target plan code.</param>
    /// <param name="planPriceExternalId">The provider price id of the target price row.</param>
    /// <param name="addOnAmountMinorUnits">The second-role add-on price, or null when the subscription has no add-on.</param>
    /// <param name="currency">ISO 4217 currency of the add-on price.</param>
    /// <param name="interval">Billing cadence of the add-on price.</param>
    /// <param name="atNextRenewal">True to bill the renewal at the new price instead of changing now.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ChangePlanAsync(
        string subscriptionId,
        string planCode,
        string planPriceExternalId,
        long? addOnAmountMinorUnits,
        string currency,
        BillingInterval interval,
        bool atNextRenewal,
        CancellationToken ct);

    /// <summary>
    /// Applies a customer-entered promotion code to an existing subscription. Returns false when the code is unknown or inactive.
    /// </summary>
    /// <param name="subscriptionId">The provider subscription id.</param>
    /// <param name="promotionCode">The code as the customer typed it.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> ApplyPromotionCodeAsync(string subscriptionId, string promotionCode, CancellationToken ct);

    /// <summary>
    /// Creates a recurring provider price for a plan price row and returns its id. Calling it again with the same
    /// arguments soon after returns the same price, so a retry after a failed save does not duplicate it.
    /// Never call it for a zero price.
    /// </summary>
    /// <param name="planCode">The plan code.</param>
    /// <param name="amountMinorUnits">The price in the smallest currency unit; must be positive.</param>
    /// <param name="currency">ISO 4217 currency.</param>
    /// <param name="interval">Billing cadence.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<string> CreatePriceAsync(
        string planCode, long amountMinorUnits, string currency, BillingInterval interval, CancellationToken ct);

    /// <summary>
    /// Makes an existing subscription free: a 100 % discount that ends on <paramref name="freeUntil"/> (never when null).
    /// Replaces any discount already on the subscription, so repeating the call cannot stack discounts.
    /// </summary>
    /// <param name="subscriptionId">The provider subscription id.</param>
    /// <param name="freeUntil">End of the gift, or null for none.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ApplyGivenFreeAsync(string subscriptionId, DateTimeOffset? freeUntil, CancellationToken ct);

    /// <summary>
    /// Ends the subscription at the close of the current period instead of renewing it. A scheduled plan change on the
    /// subscription is dropped, because the coach is leaving.
    /// </summary>
    /// <param name="subscriptionId">The provider subscription id.</param>
    /// <param name="ct">Cancellation token.</param>
    Task CancelAtPeriodEndAsync(string subscriptionId, CancellationToken ct);

    /// <summary>
    /// Previews what adding the second-role add-on to an existing subscription would charge today, prorated, with tax as
    /// configured. Changes nothing at the provider.
    /// </summary>
    /// <param name="customerId">The provider customer id.</param>
    /// <param name="subscriptionId">The provider subscription id.</param>
    /// <param name="planCode">The plan code the add-on belongs to.</param>
    /// <param name="addOnAmountMinorUnits">The add-on price per billing interval.</param>
    /// <param name="currency">ISO 4217 currency of the add-on price.</param>
    /// <param name="interval">Billing cadence of the add-on price.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<SecondRoleAddOnPreview> PreviewSecondRoleAddOnAsync(
        string customerId,
        string subscriptionId,
        string planCode,
        long addOnAmountMinorUnits,
        string currency,
        BillingInterval interval,
        CancellationToken ct);

    /// <summary>
    /// Adds the second-role add-on to an existing subscription and charges the prorated amount now. When the card is
    /// declined or needs authentication nothing is applied and the outcome says which; any other provider failure throws.
    /// </summary>
    /// <param name="subscriptionId">The provider subscription id.</param>
    /// <param name="planCode">The plan code the add-on belongs to.</param>
    /// <param name="addOnAmountMinorUnits">The add-on price per billing interval.</param>
    /// <param name="currency">ISO 4217 currency of the add-on price.</param>
    /// <param name="interval">Billing cadence of the add-on price.</param>
    /// <param name="idempotencyKey">Repeating a call with the same key returns the first result instead of charging twice.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<AddOnChargeOutcome> ChargeSecondRoleAddOnAsync(
        string subscriptionId,
        string planCode,
        long addOnAmountMinorUnits,
        string currency,
        BillingInterval interval,
        string idempotencyKey,
        CancellationToken ct);
}
