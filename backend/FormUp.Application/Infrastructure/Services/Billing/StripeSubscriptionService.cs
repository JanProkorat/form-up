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
        CancellationToken ct) =>
        RunAsync("change-plan", async stripe =>
        {
            var subscription = await GetSubscriptionAsync(stripe, subscriptionId, ct);
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

            if (atNextRenewal)
            {
                var newPriceIds = addOnPriceId is null ? [planPriceExternalId] : new List<string> { planPriceExternalId, addOnPriceId };
                var phases = StripeSchedulePlanner.Build(
                    state.Start, state.End, state.CurrentPriceIds, state.Intent with { PendingPriceIds = newPriceIds });

                await ApplyPhasesAsync(stripe, subscriptionId, state.ScheduleId, phases, IntervalOf(subscription), ct);

                return true;
            }

            if (state.ScheduleId is not null)
            {
                await stripe.V1.SubscriptionSchedules.ReleaseAsync(
                    state.ScheduleId, new SubscriptionScheduleReleaseOptions(), cancellationToken: ct);
            }

            var taxRates = StripeRequestBuilder.TaxRates(settings);
            var items = new List<SubscriptionItemOptions>
            {
                new() { Id = planItem.Id, Price = planPriceExternalId, Quantity = 1, TaxRates = taxRates },
            };

            if (addOnPriceId is not null && addOnItem is null)
            {
                items.Add(new SubscriptionItemOptions { Price = addOnPriceId, Quantity = 1, TaxRates = taxRates });
            }
            else if (addOnPriceId is not null && addOnItem!.Price.Id != addOnPriceId)
            {
                items.Add(new SubscriptionItemOptions { Id = addOnItem.Id, Price = addOnPriceId, Quantity = 1, TaxRates = taxRates });
            }
            else if (addOnPriceId is null && addOnItem is not null)
            {
                items.Add(new SubscriptionItemOptions { Id = addOnItem.Id, Deleted = true });
            }

            await stripe.V1.Subscriptions.UpdateAsync(
                subscriptionId,
                new SubscriptionUpdateOptions { Items = items, ProrationBehavior = "create_prorations" },
                cancellationToken: ct);

            if (state.Intent.GiftCouponId is not null)
            {
                await ApplyGiftAsync(stripe, subscriptionId, state.Intent.GiftCouponId, state.Intent.GiftEnd, ct);
            }

            return true;
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
            var price = await stripe.V1.Prices.CreateAsync(
                StripeRequestBuilder.Price(planCode, amountMinorUnits, currency, interval),
                new RequestOptions
                {
                    IdempotencyKey = $"formup-price-{planCode}-{currency.ToLowerInvariant()}-{amountMinorUnits}-{StripeRequestBuilder.IntervalName(interval)}",
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

            await ApplyGiftAsync(stripe, subscriptionId, couponId, freeUntil, ct);

            return true;
        });
    }

    /// <inheritdoc />
    public Task CancelAtPeriodEndAsync(string subscriptionId, CancellationToken ct) =>
        RunAsync("cancel-at-period-end", async stripe =>
        {
            var subscription = await GetSubscriptionAsync(stripe, subscriptionId, ct);

            // A subscription managed by a schedule cannot take the flag itself; the coach is leaving, so the schedule goes.
            if (subscription.ScheduleId is { } scheduleId)
            {
                await stripe.V1.SubscriptionSchedules.ReleaseAsync(
                    scheduleId, new SubscriptionScheduleReleaseOptions(), cancellationToken: ct);
            }

            await stripe.V1.Subscriptions.UpdateAsync(
                subscriptionId, new SubscriptionUpdateOptions { CancelAtPeriodEnd = true }, cancellationToken: ct);

            return true;
        });

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
    public Task<AddOnChargeOutcome> ChargeSecondRoleAddOnAsync(
        string subscriptionId,
        string planCode,
        long addOnAmountMinorUnits,
        string currency,
        BillingInterval interval,
        string idempotencyKey,
        CancellationToken ct) =>
        RunAsync("charge-second-role", async stripe =>
        {
            var addOnPriceId = await EnsureAddOnPriceOnSubscriptionPlanAsync(
                stripe, subscriptionId, planCode, addOnAmountMinorUnits, currency, interval, ct);

            try
            {
                await stripe.V1.Subscriptions.UpdateAsync(
                    subscriptionId,
                    StripeRequestBuilder.SecondRoleCharge(addOnPriceId, settings),
                    new RequestOptions { IdempotencyKey = idempotencyKey },
                    ct);

                return AddOnChargeOutcome.Charged;
            }
            catch (StripeException ex) when (IsCardError(ex))
            {
                logger.LogInformation(
                    "Second-role charge was not completed: {StatusCode} {ErrorCode}", ex.HttpStatusCode, ex.StripeError?.Code);

                return ex.StripeError?.Code is "subscription_payment_intent_requires_action" or "authentication_required"
                    ? AddOnChargeOutcome.AuthenticationRequired
                    : AddOnChargeOutcome.Declined;
            }
        });

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

    private static async Task<Subscription> GetSubscriptionAsync(StripeClient stripe, string subscriptionId, CancellationToken ct) =>
        await stripe.V1.Subscriptions.GetAsync(
            subscriptionId, new SubscriptionGetOptions { Expand = ["discounts"] }, cancellationToken: ct);

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
        StripeClient stripe, string subscriptionId, string couponId, DateTimeOffset? freeUntil, CancellationToken ct)
    {
        var subscription = await GetSubscriptionAsync(stripe, subscriptionId, ct);
        var state = await ReadStateAsync(stripe, subscription, ct);
        var phases = StripeSchedulePlanner.Build(
            state.Start,
            state.End,
            state.CurrentPriceIds,
            state.Intent with { GiftCouponId = couponId, GiftEnd = freeUntil });

        if (phases.Count > 0)
        {
            await ApplyPhasesAsync(stripe, subscriptionId, state.ScheduleId, phases, IntervalOf(subscription), ct);

            return;
        }

        if (state.ScheduleId is not null)
        {
            await stripe.V1.SubscriptionSchedules.ReleaseAsync(
                state.ScheduleId, new SubscriptionScheduleReleaseOptions(), cancellationToken: ct);
        }

        // Setting the full list replaces any discount already there, so a retry cannot stack a second one.
        await stripe.V1.Subscriptions.UpdateAsync(
            subscriptionId,
            new SubscriptionUpdateOptions { Discounts = [new SubscriptionDiscountOptions { Coupon = couponId }] },
            cancellationToken: ct);
    }

    private async Task ApplyPhasesAsync(
        StripeClient stripe,
        string subscriptionId,
        string? scheduleId,
        IReadOnlyList<SchedulePhasePlan> phases,
        BillingInterval interval,
        CancellationToken ct)
    {
        scheduleId ??= (await stripe.V1.SubscriptionSchedules.CreateAsync(
            new SubscriptionScheduleCreateOptions { FromSubscription = subscriptionId }, cancellationToken: ct)).Id;

        await stripe.V1.SubscriptionSchedules.UpdateAsync(
            scheduleId,
            new SubscriptionScheduleUpdateOptions
            {
                EndBehavior = "release",
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
                StripeSchedulePlanner.Read(phases),
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
            new ScheduleIntent(null, giftCouponId, null),
            items.Min(item => item.CurrentPeriodStart),
            items.Max(item => item.CurrentPeriodEnd),
            items.Select(item => item.Price.Id).ToList(),
            null);
    }

    private sealed record SubscriptionState(
        ScheduleIntent Intent,
        DateTimeOffset Start,
        DateTimeOffset End,
        IReadOnlyList<string> CurrentPriceIds,
        string? ScheduleId);
}
