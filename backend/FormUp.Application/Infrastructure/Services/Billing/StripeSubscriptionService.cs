using System.Net;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using Microsoft.Extensions.Options;
using Stripe;

namespace FormUp.Application.Infrastructure.Services.Billing;

/// <summary>
/// <see cref="ISubscriptionService"/> on Stripe. Request shapes come from <see cref="StripeRequestBuilder"/> and schedule
/// phases from <see cref="StripeSchedulePlanner"/>; this class only sequences the calls and maps failures to
/// <see cref="BillingProviderException"/>.
/// </summary>
/// <param name="options">Billing settings.</param>
/// <param name="timeProvider">Clock.</param>
/// <param name="logger">Logger; never receives request or response payloads.</param>
public sealed class StripeSubscriptionService(
    IOptions<BillingOptions> options,
    TimeProvider timeProvider,
    ILogger<StripeSubscriptionService> logger) : ISubscriptionService
{
    private readonly BillingOptions settings = options.Value;
    private StripeClient? client;

    /// <summary>Creates the service on a given Stripe client, so tests can capture the requests.</summary>
    /// <param name="options">Billing settings.</param>
    /// <param name="timeProvider">Clock.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="stripeClient">The client every call goes through.</param>
    internal StripeSubscriptionService(
        IOptions<BillingOptions> options,
        TimeProvider timeProvider,
        ILogger<StripeSubscriptionService> logger,
        StripeClient stripeClient) : this(options, timeProvider, logger)
    {
        client = stripeClient;
    }

    /// <inheritdoc />
    public bool IsConfigured => settings.IsConfigured;

    /// <inheritdoc />
    public Task<string> CreateCustomerAsync(Guid userId, string email, string? name, string? language, CancellationToken ct) =>
        RunAsync("create-customer", async stripe =>
        {
            var customer = await stripe.V1.Customers.CreateAsync(
                StripeRequestBuilder.Customer(userId, email, name, language, settings),
                new RequestOptions { IdempotencyKey = $"formup-customer-{userId}" },
                ct);

            return customer.Id;
        });

    /// <inheritdoc />
    public Task<string> CreateCheckoutSessionAsync(
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
        CancellationToken ct) =>
        RunAsync("create-checkout-session", async stripe =>
        {
            string? addOnPriceId = null;

            if (addOnAmountMinorUnits is { } amount)
            {
                var productId = await ProductOfPriceAsync(stripe, planPriceExternalId, ct);
                addOnPriceId = await EnsureAddOnPriceAsync(stripe, planCode, productId, amount, currency, interval, ct);
            }

            var session = await stripe.V1.Checkout.Sessions.CreateAsync(
                StripeRequestBuilder.Checkout(
                    customerId, userId, planCode, planPriceExternalId, addOnPriceId, couponId,
                    StripeRequestBuilder.Locale(language), flow, settings),
                cancellationToken: ct);

            return session.Url;
        });

    /// <inheritdoc />
    public Task<string> CreatePortalSessionAsync(string customerId, string? language, CancellationToken ct) =>
        RunAsync("create-portal-session", async stripe =>
        {
            var session = await stripe.V1.BillingPortal.Sessions.CreateAsync(
                StripeRequestBuilder.Portal(customerId, StripeRequestBuilder.Locale(language), settings),
                cancellationToken: ct);

            return session.Url;
        });

    /// <inheritdoc />
    public Task ChangePlanAsync(
        string subscriptionId,
        string planCode,
        string planPriceExternalId,
        long? addOnAmountMinorUnits,
        string currency,
        BillingInterval interval,
        bool atNextRenewal,
        CancellationToken ct,
        string? idempotencyKey = null) =>
        RunAsync("change-plan", async stripe =>
        {
            var change = await LoadPlanChangeAsync(
                stripe, subscriptionId, planCode, planPriceExternalId, addOnAmountMinorUnits, currency, interval, false, ct);

            if (atNextRenewal)
            {
                var newPriceIds = change.AddOnPriceId is null
                    ? [planPriceExternalId]
                    : new List<string> { planPriceExternalId, change.AddOnPriceId };
                var phases = StripeSchedulePlanner.Build(
                    change.State.Start,
                    change.State.End,
                    change.State.CurrentPriceIds,
                    change.State.Intent with { PendingPriceIds = newPriceIds },
                    IntervalOf(change.Subscription));

                await ApplyPhasesAsync(
                    stripe, subscriptionId, change.State.ScheduleId, phases, IntervalOf(change.Subscription), change.State.Intent.CancelRenewal, ct);

                return true;
            }

            await UpgradeNowAsync(stripe, subscriptionId, planPriceExternalId, change, idempotencyKey, allowPendingUpdate: false, ct);

            return true;
        });

    /// <inheritdoc />
    public Task<PlanUpgradeOutcome> UpgradePlanNowAsync(
        string subscriptionId,
        string planCode,
        string planPriceExternalId,
        long? addOnAmountMinorUnits,
        string currency,
        BillingInterval interval,
        string idempotencyKey,
        CancellationToken ct) =>
        RunAsync("upgrade-plan-now", async stripe =>
        {
            var change = await LoadPlanChangeAsync(
                stripe, subscriptionId, planCode, planPriceExternalId, addOnAmountMinorUnits, currency, interval, true, ct);

            // UNVERIFIED: a pending update lists its target items in pending_update.subscription_items and the open invoice it created is the latest_invoice — https://docs.stripe.com/billing/subscriptions/pending-updates
            if (change.Subscription.PendingUpdate is { } pending)
            {
                var sameTarget = (pending.SubscriptionItems ?? []).Any(item => item.Price?.Id == planPriceExternalId);
                var openInvoice = change.Subscription.LatestInvoice is { Status: "open" } invoice ? invoice : null;

                if (sameTarget && openInvoice?.HostedInvoiceUrl is { } existingUrl)
                {
                    return PlanUpgradeOutcome.AwaitingPayment(existingUrl);
                }

                // Another target replaces it: the old invoice must not stay payable next to the new one.
                if (openInvoice is not null)
                {
                    await stripe.V1.Invoices.VoidInvoiceAsync(openInvoice.Id, cancellationToken: ct);
                }
            }

            return await UpgradeNowAsync(stripe, subscriptionId, planPriceExternalId, change, idempotencyKey, allowPendingUpdate: true, ct);
        });

    /// <inheritdoc />
    public Task RestoreAfterExpiredUpgradeAsync(string subscriptionId, bool keepScheduledChange, CancellationToken ct) =>
        RunAsync("restore-after-expired-upgrade", async stripe =>
        {
            var subscription = await GetSubscriptionAsync(stripe, subscriptionId, ct);

            // A newer pending update still owns the snapshot; its own expiry brings it back.
            if (subscription.PendingUpdate is not null)
            {
                return true;
            }

            var snapshot = StripeRequestBuilder.ReadRestoreSnapshot(subscription.Metadata);

            if (keepScheduledChange
                && snapshot is not null
                && (snapshot.PendingPriceIds is not null || snapshot.CancelRenewal)
                && subscription.ScheduleId is null)
            {
                var state = await ReadStateAsync(stripe, subscription, ct);
                var phases = StripeSchedulePlanner.Build(state.Start, state.End, state.CurrentPriceIds, snapshot, IntervalOf(subscription));

                if (phases.Count > 0)
                {
                    await ApplyPhasesAsync(stripe, subscriptionId, null, phases, IntervalOf(subscription), snapshot.CancelRenewal, ct);
                }
                else if (snapshot.CancelRenewal)
                {
                    await SetCancelAtPeriodEndAsync(stripe, subscriptionId, ct);
                }
            }

            if (snapshot is not null)
            {
                await ClearRestoreSnapshotAsync(stripe, subscriptionId, ct);
            }

            return true;
        });

    /// <inheritdoc />
    public Task ClearScheduledPriceChangeAsync(string subscriptionId, CancellationToken ct) =>
        RunAsync("clear-scheduled-price-change", async stripe =>
        {
            var subscription = await GetSubscriptionAsync(stripe, subscriptionId, ct);
            var state = await ReadStateAsync(stripe, subscription, ct);

            if (state.ScheduleId is null)
            {
                return true;
            }

            var phases = StripeSchedulePlanner.Build(
                state.Start, state.End, state.CurrentPriceIds, state.Intent with { PendingPriceIds = null }, IntervalOf(subscription));

            if (phases.Count > 0)
            {
                await ApplyPhasesAsync(
                    stripe, subscriptionId, state.ScheduleId, phases, IntervalOf(subscription), state.Intent.CancelRenewal, ct);

                return true;
            }

            await stripe.V1.SubscriptionSchedules.ReleaseAsync(
                state.ScheduleId, new SubscriptionScheduleReleaseOptions(), cancellationToken: ct);

            // UNVERIFIED: releasing a schedule keeps the current items and drops the phase discounts, so a gift is put back — https://docs.stripe.com/api/subscription_schedules/release
            if (state.Intent.GiftCouponId is { } giftCouponId)
            {
                await SetSubscriptionDiscountAsync(stripe, subscriptionId, giftCouponId, ct);
            }

            // A release drops end_behavior=cancel, so the cancellation moves onto the subscription itself.
            if (state.Intent.CancelRenewal)
            {
                await SetCancelAtPeriodEndAsync(stripe, subscriptionId, ct);
            }

            return true;
        });

    /// <inheritdoc />
    public Task<PlanChangePreview> PreviewPlanChangeAsync(
        string customerId,
        string subscriptionId,
        string planCode,
        string planPriceExternalId,
        long? addOnAmountMinorUnits,
        string currency,
        BillingInterval interval,
        CancellationToken ct) =>
        RunAsync("preview-plan-change", async stripe =>
        {
            var subscription = await GetSubscriptionAsync(stripe, subscriptionId, ct);
            var planItem = subscription.Items.Data.FirstOrDefault(item => !IsAddOn(item))
                           ?? throw new BillingProviderException("The subscription has no plan item.");
            var addOnItem = subscription.Items.Data.FirstOrDefault(IsAddOn);
            string? addOnPriceId = null;

            if (addOnAmountMinorUnits is { } amount)
            {
                var productId = await ProductOfPriceAsync(stripe, planPriceExternalId, ct);
                addOnPriceId = await EnsureAddOnPriceAsync(stripe, planCode, productId, amount, currency, interval, ct);
            }

            // UNVERIFIED: an always_invoice preview holds only the proration lines, not the next period — https://docs.stripe.com/api/invoices/create_preview
            var invoice = await stripe.V1.Invoices.CreatePreviewAsync(
                StripeRequestBuilder.PlanUpgradePreview(
                    customerId, subscriptionId, planItem.Id, planPriceExternalId, addOnItem?.Id, addOnItem?.Price.Id, addOnPriceId, settings),
                cancellationToken: ct);
            var tax = (invoice.TotalTaxes ?? []).Sum(total => total.Amount);

            return new PlanChangePreview(invoice.Total, tax, currency);
        });

    /// <inheritdoc />
    public Task ApplyPromotionCodeAsync(string subscriptionId, string promotionCodeId, CancellationToken ct) =>
        RunAsync("apply-promotion-code", async stripe =>
        {
            // Setting the full list replaces the discount already there, so a repeat or a switch cannot stack two.
            await stripe.V1.Subscriptions.UpdateAsync(
                subscriptionId,
                new SubscriptionUpdateOptions
                {
                    Discounts = [new SubscriptionDiscountOptions { PromotionCode = promotionCodeId }],
                },
                cancellationToken: ct);

            return true;
        });

    /// <inheritdoc />
    public Task<string> CreateCouponAsync(CouponSpec spec, CancellationToken ct) =>
        RunAsync("create-coupon", async stripe =>
        {
            List<string>? productIds = null;

            if (spec.PlanPriceExternalIds is { Count: > 0 } priceIds)
            {
                productIds = [];

                foreach (var priceId in priceIds)
                {
                    var price = await stripe.V1.Prices.GetAsync(priceId, cancellationToken: ct);

                    if (!productIds.Contains(price.ProductId))
                    {
                        productIds.Add(price.ProductId);
                    }
                }
            }

            var coupon = await stripe.V1.Coupons.CreateAsync(
                StripeRequestBuilder.Coupon(spec, productIds),
                new RequestOptions { IdempotencyKey = spec.IdempotencyKey },
                ct);

            return coupon.Id;
        });

    /// <inheritdoc />
    public Task<string> CreatePromotionCodeAsync(
        string couponId, string code, DateTimeOffset? expiresAt, bool firstTimeOnly, CancellationToken ct) =>
        RunAsync("create-promotion-code", async stripe =>
        {
            var created = await stripe.V1.PromotionCodes.CreateAsync(
                StripeRequestBuilder.PromotionCode(couponId, code, expiresAt, firstTimeOnly), cancellationToken: ct);

            return created.Id;
        });

    /// <inheritdoc />
    public Task DeactivatePromotionCodeAsync(string promotionCodeId, CancellationToken ct) =>
        RunAsync("deactivate-promotion-code", async stripe =>
        {
            // Setting active=false on an inactive code is a no-op at Stripe, so a retry cannot fail.
            await stripe.V1.PromotionCodes.UpdateAsync(
                promotionCodeId, new PromotionCodeUpdateOptions { Active = false }, cancellationToken: ct);

            return true;
        });

    /// <inheritdoc />
    public Task DeleteCouponAsync(string couponId, CancellationToken ct) =>
        RunAsync("delete-coupon", async stripe =>
        {
            await stripe.V1.Coupons.DeleteAsync(couponId, cancellationToken: ct);

            return true;
        });

    /// <inheritdoc />
    public Task<SubscriptionDiscount?> GetSubscriptionDiscountAsync(string subscriptionId, CancellationToken ct) =>
        RunAsync("get-subscription-discount", async stripe =>
        {
            var subscription = await GetSubscriptionAsync(stripe, subscriptionId, ct);
            var discount = subscription.Discounts?.FirstOrDefault();

            return discount is null
                ? null
                : new SubscriptionDiscount(
                    discount.Source?.CouponId,
                    discount.PromotionCodeId,
                    discount.End is { } end ? StripeWebhookMapper.ToUtc(end) : null);
        });

    /// <inheritdoc />
    public Task<string> CreatePriceAsync(
        string planCode, long amountMinorUnits, string currency, BillingInterval interval, CancellationToken ct) =>
        RunAsync("create-price", async stripe =>
        {
            var productId = await EnsurePlanProductAsync(stripe, planCode, ct);
            var price = await stripe.V1.Prices.CreateAsync(
                StripeRequestBuilder.Price(planCode, productId, amountMinorUnits, currency, interval),
                new RequestOptions
                {
                    IdempotencyKey = $"formup-price-{productId}-{currency.ToLowerInvariant()}-{amountMinorUnits}-{StripeRequestBuilder.IntervalName(interval)}",
                },
                ct);

            return price.Id;
        });

    /// <inheritdoc />
    public Task ApplyGivenFreeAsync(string subscriptionId, DateTimeOffset? freeUntil, CancellationToken ct)
    {
        if (freeUntil is { } end && end <= timeProvider.GetUtcNow())
        {
            throw new ArgumentOutOfRangeException(nameof(freeUntil), "The end of a gift must be in the future.");
        }

        return RunAsync("apply-given-free", async stripe =>
        {
            var couponId = await EnsureGiftCouponAsync(stripe, ct);

            await ApplyGiftAsync(stripe, subscriptionId, couponId, freeUntil, false, ct);

            return true;
        });
    }

    /// <inheritdoc />
    public Task CancelAtPeriodEndAsync(string subscriptionId, CancellationToken ct) =>
        RunAsync("cancel-at-period-end", async stripe =>
        {
            var subscription = await GetSubscriptionAsync(stripe, subscriptionId, ct);

            // A subscription managed by a schedule cannot take the flag itself, and releasing the schedule would drop a gift's free
            // phase. The schedule is rebuilt to cancel instead, so the gift stays and the renewal still ends the subscription.
            if (subscription.ScheduleId is not null)
            {
                await CancelScheduleRenewalAsync(stripe, subscriptionId, subscription, ct);

                return true;
            }

            await SetCancelAtPeriodEndAsync(stripe, subscriptionId, ct);

            return true;
        });

    private async Task CancelScheduleRenewalAsync(
        StripeClient stripe, string subscriptionId, Subscription subscription, CancellationToken ct)
    {
        var state = await ReadStateAsync(stripe, subscription, ct);
        var phases = StripeSchedulePlanner.Build(
            state.Start, state.End, state.CurrentPriceIds, state.Intent with { CancelRenewal = true }, IntervalOf(subscription));

        if (phases.Count > 0)
        {
            await ApplyPhasesAsync(stripe, subscriptionId, state.ScheduleId, phases, IntervalOf(subscription), true, ct);

            return;
        }

        await stripe.V1.SubscriptionSchedules.ReleaseAsync(
            state.ScheduleId!, new SubscriptionScheduleReleaseOptions(), cancellationToken: ct);

        await SetCancelAtPeriodEndAsync(stripe, subscriptionId, ct);

        // UNVERIFIED: releasing a schedule keeps the current items and drops the phase discounts, so an endless gift is put back — https://docs.stripe.com/api/subscription_schedules/release
        if (state.Intent.GiftCouponId is { } giftCouponId)
        {
            await SetSubscriptionDiscountAsync(stripe, subscriptionId, giftCouponId, ct);
        }
    }

    /// <inheritdoc />
    public Task CancelNowAsync(string subscriptionId, CancellationToken ct) =>
        RunAsync("cancel-now", async stripe =>
        {
            var subscription = await GetSubscriptionAsync(stripe, subscriptionId, ct);

            if (subscription.Status == "canceled")
            {
                return true;
            }

            // Cancelling the schedule ends it and the subscription in one call; releasing first could lose the schedule if the cancel then failed.
            // Both flags are explicit: the schedule cancel options default them to true (credit for unused time), the subscription ones to false.
            // UNVERIFIED: with invoice_now and prorate false the schedule cancel also cancels its subscription at once — https://docs.stripe.com/api/subscription_schedules/cancel
            if (subscription.ScheduleId is { } scheduleId)
            {
                await stripe.V1.SubscriptionSchedules.CancelAsync(
                    scheduleId,
                    new SubscriptionScheduleCancelOptions { InvoiceNow = false, Prorate = false },
                    cancellationToken: ct);

                return true;
            }

            await stripe.V1.Subscriptions.CancelAsync(
                subscriptionId,
                new SubscriptionCancelOptions { InvoiceNow = false, Prorate = false },
                cancellationToken: ct);

            return true;
        });

    /// <inheritdoc />
    public Task ResumeRenewalAsync(string subscriptionId, CancellationToken ct) =>
        RunAsync("resume-renewal", async stripe =>
        {
            var subscription = await GetSubscriptionAsync(stripe, subscriptionId, ct);

            // A schedule that cancels is what stops the renewal, and the subscription itself carries no flag to clear.
            // Rebuilding it with the cancellation off makes it release instead; one update, so a failure leaves it as it was.
            if (subscription.ScheduleId is not null)
            {
                await ResumeScheduleRenewalAsync(stripe, subscriptionId, subscription, ct);

                return true;
            }

            // UNVERIFIED: an empty cancel_at form value unsets the date, and Stripe.net has no typed way to send it — https://docs.stripe.com/api/subscriptions/update
            await stripe.V1.Subscriptions.UpdateAsync(
                subscriptionId,
                new SubscriptionUpdateOptions
                {
                    CancelAtPeriodEnd = false,
                    ExtraParams = new Dictionary<string, object> { ["cancel_at"] = string.Empty },
                },
                cancellationToken: ct);

            return true;
        });

    private async Task ResumeScheduleRenewalAsync(
        StripeClient stripe, string subscriptionId, Subscription subscription, CancellationToken ct)
    {
        var state = await ReadStateAsync(stripe, subscription, ct);

        if (!state.Intent.CancelRenewal)
        {
            return;
        }

        var phases = StripeSchedulePlanner.Build(
            state.Start, state.End, state.CurrentPriceIds, state.Intent with { CancelRenewal = false }, IntervalOf(subscription));

        if (phases.Count > 0)
        {
            await ApplyPhasesAsync(stripe, subscriptionId, state.ScheduleId, phases, IntervalOf(subscription), false, ct);

            return;
        }

        await stripe.V1.SubscriptionSchedules.ReleaseAsync(
            state.ScheduleId!, new SubscriptionScheduleReleaseOptions(), cancellationToken: ct);

        // UNVERIFIED: releasing a schedule keeps the current items and drops the phase discounts, so a gift is put back — https://docs.stripe.com/api/subscription_schedules/release
        if (state.Intent.GiftCouponId is { } giftCouponId)
        {
            await SetSubscriptionDiscountAsync(stripe, subscriptionId, giftCouponId, ct);
        }
    }

    /// <inheritdoc />
    public Task<SecondRoleAddOnPreview> PreviewSecondRoleAddOnAsync(
        string customerId,
        string subscriptionId,
        string planCode,
        long addOnAmountMinorUnits,
        string currency,
        BillingInterval interval,
        CancellationToken ct) =>
        RunAsync("preview-second-role", async stripe =>
        {
            var addOnPriceId = await EnsureAddOnPriceOnSubscriptionPlanAsync(
                stripe, subscriptionId, planCode, addOnAmountMinorUnits, currency, interval, ct);
            var invoice = await stripe.V1.Invoices.CreatePreviewAsync(
                StripeRequestBuilder.SecondRolePreview(customerId, subscriptionId, addOnPriceId, settings),
                cancellationToken: ct);
            var tax = (invoice.TotalTaxes ?? []).Sum(total => total.Amount);

            return new SecondRoleAddOnPreview(invoice.Total, tax, currency);
        });

    /// <inheritdoc />
    public Task<AddOnChargeResult> ChargeSecondRoleAddOnAsync(
        string subscriptionId,
        string planCode,
        long addOnAmountMinorUnits,
        string currency,
        BillingInterval interval,
        string idempotencyKey,
        CancellationToken ct) =>
        RunAsync("charge-second-role", async stripe =>
        {
            var subscription = await GetSubscriptionAsync(stripe, subscriptionId, ct, expandLatestInvoice: true);

            if (subscription.PendingUpdate is { } pendingUpdate)
            {
                return ExistingPendingAddOn(pendingUpdate, subscription);
            }

            var productId = subscription.Items.Data.FirstOrDefault(item => !IsAddOn(item))?.Price?.ProductId
                            ?? throw new BillingProviderException("The subscription has no plan item.");
            var addOnPriceId = await EnsureAddOnPriceAsync(stripe, planCode, productId, addOnAmountMinorUnits, currency, interval, ct);

            try
            {
                await stripe.V1.Subscriptions.UpdateAsync(
                    subscriptionId,
                    StripeRequestBuilder.SecondRoleCharge(addOnPriceId, settings),
                    new RequestOptions { IdempotencyKey = idempotencyKey },
                    ct);

                return AddOnChargeResult.Charged;
            }
            catch (StripeException ex) when (IsCardError(ex))
            {
                logger.LogInformation(
                    "Second-role charge was not completed: {StatusCode} {ErrorCode}", ex.HttpStatusCode, ex.StripeError?.Code);

                return IsAuthenticationRequired(ex)
                    ? await HoldAddOnAsPendingAsync(stripe, subscriptionId, addOnPriceId, idempotencyKey, ct)
                    : AddOnChargeResult.Declined;
            }
        });

    /// <summary>
    /// A pending update left by an earlier add-on attempt returns its open invoice again, so a repeat creates no second
    /// invoice; any other pending update (an upgrade) is left alone and blocks the charge.
    /// </summary>
    private static AddOnChargeResult ExistingPendingAddOn(SubscriptionPendingUpdate pendingUpdate, Subscription subscription)
    {
        // UNVERIFIED: pending_update.subscription_items lists the items the update adds, with their price lookup key — https://docs.stripe.com/billing/subscriptions/pending-updates
        if (pendingUpdate.SubscriptionItems?.Any(IsAddOn) != true)
        {
            return AddOnChargeResult.PlanChangePending;
        }

        return subscription.LatestInvoice?.HostedInvoiceUrl is { } url
            ? AddOnChargeResult.AwaitingPayment(url)
            : throw new BillingProviderException("The pending add-on has no hosted payment page.");
    }

    private async Task<AddOnChargeResult> HoldAddOnAsPendingAsync(
        StripeClient stripe, string subscriptionId, string addOnPriceId, string idempotencyKey, CancellationToken ct)
    {
        // The first attempt applied and charged nothing, so repeating it as a pending update invoices once.
        var pending = await stripe.V1.Subscriptions.UpdateAsync(
            subscriptionId,
            StripeRequestBuilder.SecondRoleChargePending(addOnPriceId, settings),
            new RequestOptions { IdempotencyKey = $"{idempotencyKey}-pending" },
            ct);

        return pending.LatestInvoice?.HostedInvoiceUrl is { } url
            ? AddOnChargeResult.AwaitingPayment(url)
            : throw new BillingProviderException("The pending add-on has no hosted payment page.");
    }

    private static bool IsAuthenticationRequired(StripeException ex) =>
        IsCardError(ex) && ex.StripeError?.Code is "subscription_payment_intent_requires_action" or "authentication_required";

    private async Task<PlanChangeInputs> LoadPlanChangeAsync(
        StripeClient stripe,
        string subscriptionId,
        string planCode,
        string planPriceExternalId,
        long? addOnAmountMinorUnits,
        string currency,
        BillingInterval interval,
        bool expandLatestInvoice,
        CancellationToken ct)
    {
        var subscription = await GetSubscriptionAsync(stripe, subscriptionId, ct, expandLatestInvoice);
        var state = await ReadStateAsync(stripe, subscription, ct);
        var planItem = subscription.Items.Data.FirstOrDefault(item => !IsAddOn(item))
                       ?? throw new BillingProviderException("The subscription has no plan item.");
        var addOnItem = subscription.Items.Data.FirstOrDefault(IsAddOn);
        string? addOnPriceId = null;

        if (addOnAmountMinorUnits is { } amount)
        {
            var productId = await ProductOfPriceAsync(stripe, planPriceExternalId, ct);
            addOnPriceId = await EnsureAddOnPriceAsync(stripe, planCode, productId, amount, currency, interval, ct);
        }

        return new PlanChangeInputs(subscription, state, planItem, addOnItem, addOnPriceId);
    }

    /// <summary>
    /// Releases a scheduled change, then charges the upgrade at once. With <paramref name="allowPendingUpdate"/> a card that
    /// needs authentication leaves the upgrade pending behind Stripe's hosted payment page instead of failing; every other
    /// failure restores the released schedule and rethrows.
    /// </summary>
    private async Task<PlanUpgradeOutcome> UpgradeNowAsync(
        StripeClient stripe,
        string subscriptionId,
        string planPriceExternalId,
        PlanChangeInputs change,
        string? idempotencyKey,
        bool allowPendingUpdate,
        CancellationToken ct)
    {
        var (subscription, state, planItem, addOnItem, addOnPriceId) = change;

        if (state.ScheduleId is not null)
        {
            await stripe.V1.SubscriptionSchedules.ReleaseAsync(
                state.ScheduleId, new SubscriptionScheduleReleaseOptions(), cancellationToken: ct);
        }

        try
        {
            await stripe.V1.Subscriptions.UpdateAsync(
                subscriptionId,
                StripeRequestBuilder.PlanUpgrade(
                    planItem.Id, planPriceExternalId, addOnItem?.Id, addOnItem?.Price.Id, addOnPriceId, settings),
                idempotencyKey is null ? null : new RequestOptions { IdempotencyKey = idempotencyKey },
                ct);
        }
        catch (StripeException ex) when (allowPendingUpdate && IsAuthenticationRequired(ex))
        {
            logger.LogInformation(
                "Upgrade of {SubscriptionId} needs authentication: {StatusCode} {ErrorCode}", subscriptionId, ex.HttpStatusCode, ex.StripeError?.Code);

            try
            {
                return await HoldUpgradeAsPendingAsync(stripe, subscriptionId, planPriceExternalId, change, idempotencyKey, ct);
            }
            catch (Exception pendingFailure)
            {
                logger.LogWarning(pendingFailure, "Holding the upgrade of subscription {SubscriptionId} as pending failed", subscriptionId);

                if (state.ScheduleId is not null)
                {
                    await RestoreReleasedScheduleAsync(stripe, subscriptionId, subscription, state, ct);
                    await TryClearRestoreSnapshotAsync(stripe, subscriptionId, ct);
                }

                throw;
            }
        }
        catch (StripeException) when (state.ScheduleId is not null)
        {
            await RestoreReleasedScheduleAsync(stripe, subscriptionId, subscription, state, ct);

            throw;
        }

        if (state.Intent.GiftCouponId is not null)
        {
            await ApplyGiftAsync(stripe, subscriptionId, state.Intent.GiftCouponId, state.Intent.GiftEnd, state.Intent.CancelRenewal, ct);
        }
        else if (state.ScheduleId is not null && state.Intent.CancelRenewal)
        {
            // The release dropped the schedule's cancellation; a plain subscription keeps its own flag through the upgrade.
            await SetCancelAtPeriodEndAsync(stripe, subscriptionId, ct);
        }

        return PlanUpgradeOutcome.Applied;
    }

    private async Task<PlanUpgradeOutcome> HoldUpgradeAsPendingAsync(
        StripeClient stripe,
        string subscriptionId,
        string planPriceExternalId,
        PlanChangeInputs change,
        string? idempotencyKey,
        CancellationToken ct)
    {
        var (subscription, state, planItem, addOnItem, addOnPriceId) = change;

        // The schedule is already released and a later schedule phase change would discard the pending update, so what the
        // release dropped is parked in the subscription's metadata until the update is applied or expires. An older pending
        // update keeps the snapshot it already owns.
        if (subscription.PendingUpdate is null && (state.ScheduleId is not null || StripeRequestBuilder.HasRestoreSnapshot(subscription.Metadata)))
        {
            var intent = state.ScheduleId is null ? new ScheduleIntent(null, null, null) : state.Intent;

            await stripe.V1.Subscriptions.UpdateAsync(
                subscriptionId,
                new SubscriptionUpdateOptions { Metadata = StripeRequestBuilder.RestoreSnapshot(intent) },
                cancellationToken: ct);
        }

        // The proration was not paid by the first attempt, so repeating it as a pending update charges once.
        var pending = await stripe.V1.Subscriptions.UpdateAsync(
            subscriptionId,
            StripeRequestBuilder.PlanUpgradePending(
                planItem.Id, planPriceExternalId, addOnItem?.Id, addOnItem?.Price.Id, addOnPriceId, settings),
            new RequestOptions { IdempotencyKey = $"{idempotencyKey ?? $"formup-plan-upgrade-{subscriptionId}"}-pending" },
            ct);

        return pending.LatestInvoice?.HostedInvoiceUrl is { } url
            ? PlanUpgradeOutcome.AwaitingPayment(url)
            : throw new BillingProviderException("The pending upgrade has no hosted payment page.");
    }

    private async Task TryClearRestoreSnapshotAsync(StripeClient stripe, string subscriptionId, CancellationToken ct)
    {
        try
        {
            await ClearRestoreSnapshotAsync(stripe, subscriptionId, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Removing the restore snapshot of subscription {SubscriptionId} failed", subscriptionId);
        }
    }

    private static Task ClearRestoreSnapshotAsync(StripeClient stripe, string subscriptionId, CancellationToken ct) =>
        stripe.V1.Subscriptions.UpdateAsync(
            subscriptionId,
            new SubscriptionUpdateOptions { Metadata = StripeRequestBuilder.ClearedRestoreSnapshot() },
            cancellationToken: ct);

    private static bool IsCardError(StripeException ex) =>
        ex.HttpStatusCode == HttpStatusCode.PaymentRequired || ex.StripeError?.Type == "card_error";

    private static bool IsAddOn(SubscriptionItem item) =>
        item.Price?.LookupKey?.StartsWith(StripeRequestBuilder.AddOnLookupPrefix, StringComparison.Ordinal) == true;

    private static BillingInterval IntervalOf(Subscription subscription) =>
        subscription.Items.Data.FirstOrDefault()?.Price?.Recurring?.Interval == "year" ? BillingInterval.Annual : BillingInterval.Monthly;

    private async Task<T> RunAsync<T>(string operation, Func<StripeClient, Task<T>> call)
    {
        if (!settings.IsConfigured)
        {
            throw new BillingProviderException("Stripe is not configured.");
        }

        client ??= new StripeClient(settings.SecretKey);

        try
        {
            return await call(client);
        }
        catch (StripeException ex)
        {
            logger.LogWarning(
                ex, "Stripe call {Operation} failed with {StatusCode} {ErrorCode}", operation, ex.HttpStatusCode, ex.StripeError?.Code);

            throw new BillingProviderException($"Stripe call {operation} failed.", ex);
        }
    }

    private static async Task<Subscription> GetSubscriptionAsync(
        StripeClient stripe, string subscriptionId, CancellationToken ct, bool expandLatestInvoice = false) =>
        await stripe.V1.Subscriptions.GetAsync(
            subscriptionId,
            new SubscriptionGetOptions { Expand = expandLatestInvoice ? ["discounts", "latest_invoice"] : ["discounts"] },
            cancellationToken: ct);

    private static async Task<string> ProductOfPriceAsync(StripeClient stripe, string priceId, CancellationToken ct) =>
        (await stripe.V1.Prices.GetAsync(priceId, cancellationToken: ct)).ProductId;

    /// <summary>
    /// Finds or creates the add-on price on the product of the plan the subscription is on, read from its plan item. The
    /// add-on lives on the plan's product so a coupon limited to that product discounts both lines.
    /// </summary>
    private static async Task<string> EnsureAddOnPriceOnSubscriptionPlanAsync(
        StripeClient stripe,
        string subscriptionId,
        string planCode,
        long amount,
        string currency,
        BillingInterval interval,
        CancellationToken ct)
    {
        var subscription = await GetSubscriptionAsync(stripe, subscriptionId, ct);
        var productId = subscription.Items.Data.FirstOrDefault(item => !IsAddOn(item))?.Price?.ProductId
                        ?? throw new BillingProviderException("The subscription has no plan item.");

        return await EnsureAddOnPriceAsync(stripe, planCode, productId, amount, currency, interval, ct);
    }

    private static async Task<string> EnsureAddOnPriceAsync(
        StripeClient stripe,
        string planCode,
        string productId,
        long amount,
        string currency,
        BillingInterval interval,
        CancellationToken ct)
    {
        var lookupKey = StripeRequestBuilder.AddOnLookupKey(planCode, productId, amount, currency, interval);
        var existing = await stripe.V1.Prices.ListAsync(
            new PriceListOptions { LookupKeys = [lookupKey], Active = true, Limit = 1 }, cancellationToken: ct);

        if (existing.Data.Count > 0)
        {
            return existing.Data[0].Id;
        }

        var created = await stripe.V1.Prices.CreateAsync(
            StripeRequestBuilder.AddOnPrice(planCode, productId, amount, currency, interval),
            new RequestOptions { IdempotencyKey = lookupKey },
            ct);

        return created.Id;
    }

    /// <summary>
    /// Finds the plan's stable product by its deterministic id, creating it when missing. A create that loses a race
    /// (the id already exists) reads the winner instead of failing.
    /// </summary>
    private static async Task<string> EnsurePlanProductAsync(StripeClient stripe, string planCode, CancellationToken ct)
    {
        var productId = StripeRequestBuilder.PlanProductId(planCode);

        try
        {
            return (await stripe.V1.Products.GetAsync(productId, cancellationToken: ct)).Id;
        }
        catch (StripeException ex) when (ex.HttpStatusCode == HttpStatusCode.NotFound)
        {
            try
            {
                return (await stripe.V1.Products.CreateAsync(StripeRequestBuilder.PlanProduct(planCode), cancellationToken: ct)).Id;
            }
            catch (StripeException createEx) when (createEx.StripeError?.Code == "resource_already_exists")
            {
                return (await stripe.V1.Products.GetAsync(productId, cancellationToken: ct)).Id;
            }
        }
    }

    private static async Task<string> EnsureGiftCouponAsync(StripeClient stripe, CancellationToken ct)
    {
        try
        {
            return (await stripe.V1.Coupons.GetAsync(StripeRequestBuilder.GiftCouponId, cancellationToken: ct)).Id;
        }
        catch (StripeException ex) when (ex.HttpStatusCode == HttpStatusCode.NotFound)
        {
            return (await stripe.V1.Coupons.CreateAsync(StripeRequestBuilder.GiftCoupon(), cancellationToken: ct)).Id;
        }
    }

    private async Task ApplyGiftAsync(
        StripeClient stripe,
        string subscriptionId,
        string couponId,
        DateTimeOffset? freeUntil,
        bool cancelRenewal,
        CancellationToken ct)
    {
        var subscription = await GetSubscriptionAsync(stripe, subscriptionId, ct);
        var state = await ReadStateAsync(stripe, subscription, ct);
        var intent = state.Intent with
        {
            GiftCouponId = couponId,
            GiftEnd = freeUntil,
            CancelRenewal = state.Intent.CancelRenewal || cancelRenewal,
        };
        var phases = StripeSchedulePlanner.Build(state.Start, state.End, state.CurrentPriceIds, intent, IntervalOf(subscription));

        if (phases.Count > 0)
        {
            await ApplyPhasesAsync(stripe, subscriptionId, state.ScheduleId, phases, IntervalOf(subscription), intent.CancelRenewal, ct);

            return;
        }

        if (state.ScheduleId is not null)
        {
            await stripe.V1.SubscriptionSchedules.ReleaseAsync(
                state.ScheduleId, new SubscriptionScheduleReleaseOptions(), cancellationToken: ct);
        }

        await SetSubscriptionDiscountAsync(stripe, subscriptionId, couponId, ct);

        // A release drops end_behavior=cancel, and an endless gift needs no schedule, so the plain flag keeps the cancellation.
        if (intent.CancelRenewal && state.ScheduleId is not null)
        {
            await SetCancelAtPeriodEndAsync(stripe, subscriptionId, ct);
        }
    }

    private static Task SetCancelAtPeriodEndAsync(StripeClient stripe, string subscriptionId, CancellationToken ct) =>
        stripe.V1.Subscriptions.UpdateAsync(
            subscriptionId, new SubscriptionUpdateOptions { CancelAtPeriodEnd = true }, cancellationToken: ct);

    /// <summary>
    /// Makes the coupon the subscription's only discount. Setting the full list replaces any discount already there, so a
    /// retry cannot stack a second one.
    /// </summary>
    private static Task SetSubscriptionDiscountAsync(StripeClient stripe, string subscriptionId, string couponId, CancellationToken ct) =>
        stripe.V1.Subscriptions.UpdateAsync(
            subscriptionId,
            new SubscriptionUpdateOptions { Discounts = [new SubscriptionDiscountOptions { Coupon = couponId }] },
            cancellationToken: ct);

    /// <summary>
    /// Puts the schedule back after an upgrade that released it failed to charge, so a scheduled downgrade is not lost.
    /// Best effort: a failure here is logged with ids only and never replaces the original error.
    /// </summary>
    private async Task RestoreReleasedScheduleAsync(
        StripeClient stripe, string subscriptionId, Subscription subscription, SubscriptionState state, CancellationToken ct)
    {
        try
        {
            var phases = StripeSchedulePlanner.Build(state.Start, state.End, state.CurrentPriceIds, state.Intent, IntervalOf(subscription));

            if (phases.Count > 0)
            {
                await ApplyPhasesAsync(stripe, subscriptionId, null, phases, IntervalOf(subscription), state.Intent.CancelRenewal, ct);
            }
            else if (state.Intent.CancelRenewal)
            {
                await SetCancelAtPeriodEndAsync(stripe, subscriptionId, ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "Restoring the released schedule {ScheduleId} of subscription {SubscriptionId} failed", state.ScheduleId, subscriptionId);
        }
    }

    private async Task ApplyPhasesAsync(
        StripeClient stripe,
        string subscriptionId,
        string? scheduleId,
        IReadOnlyList<SchedulePhasePlan> phases,
        BillingInterval interval,
        bool cancelRenewal,
        CancellationToken ct)
    {
        scheduleId ??= (await stripe.V1.SubscriptionSchedules.CreateAsync(
            new SubscriptionScheduleCreateOptions { FromSubscription = subscriptionId }, cancellationToken: ct)).Id;

        await stripe.V1.SubscriptionSchedules.UpdateAsync(
            scheduleId,
            new SubscriptionScheduleUpdateOptions
            {
                EndBehavior = cancelRenewal ? "cancel" : "release",
                ProrationBehavior = "none",
                Phases = StripeRequestBuilder.SchedulePhases(phases, interval, StripeRequestBuilder.TaxRates(settings)),
            },
            cancellationToken: ct);
    }

    private static async Task<SubscriptionState> ReadStateAsync(StripeClient stripe, Subscription subscription, CancellationToken ct)
    {
        if (subscription.ScheduleId is { } scheduleId)
        {
            var schedule = await stripe.V1.SubscriptionSchedules.GetAsync(scheduleId, cancellationToken: ct);
            var currentStart = schedule.CurrentPhase?.StartDate ?? schedule.Phases[0].StartDate;
            var phases = schedule.Phases
                .Where(phase => phase.StartDate >= currentStart)
                .Select(phase => new SchedulePhasePlan(
                    phase.StartDate,
                    phase.EndDate,
                    phase.Items.Select(item => item.PriceId ?? item.Price.Id).ToList(),
                    phase.Discounts?.FirstOrDefault()?.CouponId))
                .ToList();

            return new SubscriptionState(
                StripeSchedulePlanner.Read(phases, schedule.EndBehavior == "cancel" || IsCancelling(subscription)),
                phases[0].Start,
                phases[0].End ?? subscription.Items.Data.Max(item => item.CurrentPeriodEnd),
                phases[0].PriceIds,
                scheduleId);
        }

        var items = subscription.Items.Data;
        var giftCouponId = (subscription.Discounts ?? [])
            .Select(discount => discount.Source?.CouponId)
            .FirstOrDefault(id => id == StripeRequestBuilder.GiftCouponId);

        return new SubscriptionState(
            new ScheduleIntent(null, giftCouponId, null, IsCancelling(subscription)),
            items.Min(item => item.CurrentPeriodStart),
            items.Max(item => item.CurrentPeriodEnd),
            items.Select(item => item.Price.Id).ToList(),
            null);
    }

    private static bool IsCancelling(Subscription subscription) => subscription.CancelAtPeriodEnd || subscription.CancelAt is not null;

    private sealed record PlanChangeInputs(
        Subscription Subscription,
        SubscriptionState State,
        SubscriptionItem PlanItem,
        SubscriptionItem? AddOnItem,
        string? AddOnPriceId);

    private sealed record SubscriptionState(
        ScheduleIntent Intent,
        DateTimeOffset Start,
        DateTimeOffset End,
        IReadOnlyList<string> CurrentPriceIds,
        string? ScheduleId);
}
