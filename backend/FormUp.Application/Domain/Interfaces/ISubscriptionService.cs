using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Services.Billing;

namespace FormUp.Application.Domain.Interfaces;

/// <summary>
/// The payment provider seam: everything the backend asks of Stripe, so a merchant of record can replace it later.
/// Implementations throw <c>BillingProviderException</c> when the provider call fails.
/// </summary>
public interface ISubscriptionService
{
    /// <summary>
    /// True when billing calls can run. When false, checkout and portal answer 503. Giving a plan, cancelling, ending or disabling a live subscription answers 503.
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
    /// The provider's own promotion-code field is never shown; the only discount is <paramref name="couponId"/>, which the
    /// caller resolved from an automatic promotion or a code the coach typed in our app.
    /// </summary>
    /// <param name="customerId">The provider customer id.</param>
    /// <param name="userId">The coach's <c>ApplicationUser.Id</c>, stored as subscription metadata.</param>
    /// <param name="planCode">The plan code, stored as subscription metadata.</param>
    /// <param name="planPriceExternalId">The provider price id of the plan's open price row.</param>
    /// <param name="addOnAmountMinorUnits">The second-role add-on price, or null for no add-on line.</param>
    /// <param name="currency">ISO 4217 currency of the add-on price.</param>
    /// <param name="interval">Billing cadence of the add-on price.</param>
    /// <param name="couponId">The coupon to attach for this interval (automatic winner or the typed code's), or null for none.</param>
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
    /// Moves an existing subscription to another plan price, immediately or scheduled for the next renewal. An immediate change invoices the prorated
    /// difference now and also clears a scheduled cancellation; when the payment does not go through nothing is applied,
    /// a released schedule is restored and the call throws.
    /// </summary>
    /// <param name="subscriptionId">The provider subscription id.</param>
    /// <param name="planCode">The target plan code.</param>
    /// <param name="planPriceExternalId">The provider price id of the target price row.</param>
    /// <param name="addOnAmountMinorUnits">The second-role add-on price, or null when the subscription has no add-on.</param>
    /// <param name="currency">ISO 4217 currency of the add-on price.</param>
    /// <param name="interval">Billing cadence of the add-on price.</param>
    /// <param name="atNextRenewal">True to bill the renewal at the new price instead of changing now.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="idempotencyKey">For an immediate change: repeating a call with the same key returns the first result instead of charging twice. Ignored for a scheduled change.</param>
    /// <param name="pendingPhaseCouponId">For a scheduled change: the coupon the new price carries from the renewal on, or null for no discount in that phase.</param>
    Task ChangePlanAsync(
        string subscriptionId,
        string planCode,
        string planPriceExternalId,
        long? addOnAmountMinorUnits,
        string currency,
        BillingInterval interval,
        bool atNextRenewal,
        CancellationToken ct,
        string? idempotencyKey = null,
        string? pendingPhaseCouponId = null);

    /// <summary>
    /// Moves a subscription to a dearer plan price now and charges the prorated difference. A card that needs no
    /// authentication is charged and the upgrade applied at once. When the card needs authentication the upgrade is held as
    /// a provider pending update and the outcome carries the hosted payment page; the provider applies it once the coach
    /// pays, and our row is not changed here. Repeating the call for the same target while an update is pending returns the
    /// same page without a second charge. A declined card or any other failure restores a released schedule and throws.
    /// </summary>
    /// <param name="subscriptionId">The provider subscription id.</param>
    /// <param name="planCode">The target plan code.</param>
    /// <param name="planPriceExternalId">The provider price id of the target price row.</param>
    /// <param name="addOnAmountMinorUnits">The second-role add-on price, or null when the subscription has no add-on.</param>
    /// <param name="currency">ISO 4217 currency of the add-on price.</param>
    /// <param name="interval">Billing cadence of the add-on price.</param>
    /// <param name="idempotencyKey">Repeating a call with the same key returns the first result instead of charging twice.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="discount">What the same update does to the subscription's discount; null leaves it alone. Used when the interval changes.</param>
    Task<PlanUpgradeOutcome> UpgradePlanNowAsync(
        string subscriptionId,
        string planCode,
        string planPriceExternalId,
        long? addOnAmountMinorUnits,
        string currency,
        BillingInterval interval,
        string idempotencyKey,
        CancellationToken ct,
        SubscriptionDiscountChange? discount = null);

    /// <summary>
    /// Cleans up after a pending upgrade expired unpaid: puts a scheduled downgrade back from the snapshot taken before the
    /// upgrade (only when <paramref name="keepScheduledChange"/> and nothing else holds the subscription) and removes the
    /// snapshot. Does nothing while another pending update is still on the subscription. Repeating the call is safe.
    /// </summary>
    /// <param name="subscriptionId">The provider subscription id.</param>
    /// <param name="keepScheduledChange">True when our row still has a scheduled plan change to bring back.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RestoreAfterExpiredUpgradeAsync(string subscriptionId, bool keepScheduledChange, CancellationToken ct);

    /// <summary>
    /// Drops the scheduled price change of a subscription so it renews unchanged.
    /// Charges nothing. Does nothing when no schedule is on the subscription.
    /// </summary>
    /// <param name="subscriptionId">The provider subscription id.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ClearScheduledPriceChangeAsync(string subscriptionId, CancellationToken ct);

    /// <summary>
    /// Previews what moving an existing subscription to a dearer plan now would charge today, prorated, with tax as
    /// configured. Changes nothing at the provider.
    /// </summary>
    /// <param name="customerId">The provider customer id.</param>
    /// <param name="subscriptionId">The provider subscription id.</param>
    /// <param name="planCode">The target plan code.</param>
    /// <param name="planPriceExternalId">The provider price id of the target price row.</param>
    /// <param name="addOnAmountMinorUnits">The second-role add-on price, or null when the subscription has no add-on.</param>
    /// <param name="currency">ISO 4217 currency of the add-on price.</param>
    /// <param name="interval">Billing cadence of the add-on price.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="discount">What the real change would do to the discount, so the preview matches; null leaves it alone.</param>
    Task<PlanChangePreview> PreviewPlanChangeAsync(
        string customerId,
        string subscriptionId,
        string planCode,
        string planPriceExternalId,
        long? addOnAmountMinorUnits,
        string currency,
        BillingInterval interval,
        CancellationToken ct,
        SubscriptionDiscountChange? discount = null);

    /// <summary>
    /// Makes a coupon the subscription's only discount. The subscription's discount list is replaced, never added to, so
    /// repeating the call or switching codes cannot stack discounts.
    /// </summary>
    /// <param name="subscriptionId">The provider subscription id.</param>
    /// <param name="couponId">The provider coupon id for the subscription's billing interval.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ApplyCouponAsync(string subscriptionId, string couponId, CancellationToken ct);

    /// <summary>Creates a discount coupon and returns its id.</summary>
    /// <param name="spec">What the coupon discounts, for how long, and the limits the provider can enforce.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<string> CreateCouponAsync(CouponSpec spec, CancellationToken ct);

    /// <summary>
    /// Creates a customer-facing promotion code for a coupon and returns its id. No longer called for new promotions: codes
    /// are typed in our own app and resolved to a coupon there. Kept with the deactivation below for promotions made before.
    /// </summary>
    /// <param name="couponId">The provider coupon id.</param>
    /// <param name="code">The code text customers type.</param>
    /// <param name="expiresAt">Last moment the code works, or null for no end.</param>
    /// <param name="firstTimeOnly">True to refuse customers who paid before.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<string> CreatePromotionCodeAsync(
        string couponId, string code, DateTimeOffset? expiresAt, bool firstTimeOnly, CancellationToken ct);

    /// <summary>
    /// Deactivates a promotion code so nobody can redeem it any more. The coupon and discounts already on subscriptions
    /// are untouched. Deactivating an already inactive code succeeds, so a retry is safe.
    /// </summary>
    /// <param name="promotionCodeId">The provider promotion code id.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeactivatePromotionCodeAsync(string promotionCodeId, CancellationToken ct);

    /// <summary>
    /// Deletes a coupon that no subscription uses yet; the cleanup after a promotion code could not be created.
    /// </summary>
    /// <param name="couponId">The provider coupon id.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeleteCouponAsync(string couponId, CancellationToken ct);

    /// <summary>
    /// Reads the discount a subscription carries now, or null when it has none.
    /// </summary>
    /// <param name="subscriptionId">The provider subscription id.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<SubscriptionDiscount?> GetSubscriptionDiscountAsync(string subscriptionId, CancellationToken ct);

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
    /// Ends the subscription at the close of the current period instead of renewing it. A scheduled plan change on the
    /// subscription is dropped, because the coach is leaving.
    /// </summary>
    /// <param name="subscriptionId">The provider subscription id.</param>
    /// <param name="ct">Cancellation token.</param>
    Task CancelAtPeriodEndAsync(string subscriptionId, CancellationToken ct);

    /// <summary>
    /// Ends the subscription right now, with no credit balance and no final invoice, then refunds the unused days of the
    /// current paid period to the card (never more than the latest paid invoice collected; nothing for a subscription that
    /// was past due, unpaid or incomplete). A schedule on the subscription ends with it. Nothing is refunded before the cancel
    /// succeeded; when the refund fails the call throws with the subscription already canceled, and repeating it skips the
    /// cancel and refunds once (what was already refunded is subtracted), so a retry after a failed refund or save is safe.
    /// </summary>
    /// <param name="subscriptionId">The provider subscription id.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<CancelRefundResult> CancelNowAsync(string subscriptionId, CancellationToken ct);

    /// <summary>
    /// Undoes a scheduled end of the subscription: it renews again. Clears both the cancel-at-period-end flag and any
    /// cancel-at date. Charges nothing; repeating the call is safe.
    /// </summary>
    /// <param name="subscriptionId">The provider subscription id.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ResumeRenewalAsync(string subscriptionId, CancellationToken ct);

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
    /// declined nothing is applied. A card that needs authentication leaves the add-on pending behind the provider's hosted
    /// payment page, and another open pending update (such as an upgrade) blocks the charge; the outcome says which. Any
    /// other provider failure throws.
    /// </summary>
    /// <param name="subscriptionId">The provider subscription id.</param>
    /// <param name="planCode">The plan code the add-on belongs to.</param>
    /// <param name="addOnAmountMinorUnits">The add-on price per billing interval.</param>
    /// <param name="currency">ISO 4217 currency of the add-on price.</param>
    /// <param name="interval">Billing cadence of the add-on price.</param>
    /// <param name="idempotencyKey">Repeating a call with the same key returns the first result instead of charging twice.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="roleToAdd">
    /// The coach role (Trainer or Nutritionist) this charge buys. Remembered on the subscription while the add-on awaits
    /// payment, so the webhook can add the role once it is paid; null remembers nothing.
    /// </param>
    Task<AddOnChargeResult> ChargeSecondRoleAddOnAsync(
        string subscriptionId,
        string planCode,
        long addOnAmountMinorUnits,
        string currency,
        BillingInterval interval,
        string idempotencyKey,
        CancellationToken ct,
        string? roleToAdd = null);

    /// <summary>
    /// Removes the role remembered by <see cref="ChargeSecondRoleAddOnAsync"/> from the subscription. A provider failure throws.
    /// </summary>
    /// <param name="subscriptionId">The provider subscription id.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ClearPendingSecondRoleAsync(string subscriptionId, CancellationToken ct);

    /// <summary>
    /// Permanently deletes the provider customer. The provider keeps its own invoice and charge records. Deleting an
    /// already-deleted or missing customer succeeds, so a retry is safe. Any other provider failure throws.
    /// </summary>
    /// <param name="customerId">The provider customer id.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeleteCustomerAsync(string customerId, CancellationToken ct);
}
