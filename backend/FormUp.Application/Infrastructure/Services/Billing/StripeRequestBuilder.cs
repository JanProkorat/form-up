using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using Microsoft.AspNetCore.WebUtilities;
using Stripe;
using Stripe.Checkout;

namespace FormUp.Application.Infrastructure.Services.Billing;

/// <summary>
/// Builds Stripe request options as plain functions, so every rule (no trial, promotion codes, tax, CZK, locale, invoice
/// note) is unit-tested without a network call.
/// </summary>
public static class StripeRequestBuilder
{
    /// <summary>The invoice note shown when the seller is not a VAT payer.</summary>
    public const string NotVatPayerFooter = "Nejsem plátce DPH";

    /// <summary>Prefix of the lookup key that identifies a second-role add-on price.</summary>
    public const string AddOnLookupPrefix = "formup-addon-";

    /// <summary>Prefix of the id of a plan's stable Stripe product.</summary>
    public const string PlanProductPrefix = "formup-plan-";

    /// <summary>Subscription metadata key that remembers the coach role bought by a second-role add-on awaiting payment.</summary>
    public const string PendingSecondRoleKey = "formup_pending_second_role";

    private const string RestorePricesKey = "formup_restore_prices";

    private const string RestoreCancelKey = "formup_restore_cancel";

    private const string DefaultLocale = "cs";

    private static readonly string[] SupportedLocales = ["cs", "en", "de"];

    /// <summary>The Stripe locale for a user's language; Czech when unknown.</summary>
    /// <param name="language">The user's language code, or null.</param>
    public static string Locale(string? language)
    {
        var code = language?.Trim().ToLowerInvariant();
        code = code is { Length: > 2 } ? code[..2] : code;

        return code is not null && SupportedLocales.Contains(code) ? code : DefaultLocale;
    }

    /// <summary>The Stripe interval name of a billing cadence.</summary>
    /// <param name="interval">The cadence.</param>
    public static string IntervalName(BillingInterval interval) => interval == BillingInterval.Annual ? "year" : "month";

    /// <summary>The tax rate ids to put on every charged line, or null when the seller charges no VAT.</summary>
    /// <param name="options">Billing settings.</param>
    public static List<string>? TaxRates(BillingOptions options) =>
        options.TaxMode == BillingTaxMode.Vat21 && !string.IsNullOrWhiteSpace(options.VatTaxRateId)
            ? [options.VatTaxRateId]
            : null;

    /// <summary>
    /// Sets the tax rates on a Checkout line only when there are some. Stripe.net sends an explicitly assigned null as an
    /// empty <c>tax_rates=</c>, which Stripe rejects; an unassigned property is not sent at all.
    /// </summary>
    /// <param name="item">The line.</param>
    /// <param name="taxRates">Tax rate ids, or null when the seller charges no VAT.</param>
    public static SessionLineItemOptions WithTaxRates(SessionLineItemOptions item, List<string>? taxRates)
    {
        if (taxRates is not null)
        {
            item.TaxRates = taxRates;
        }

        return item;
    }

    /// <inheritdoc cref="WithTaxRates(SessionLineItemOptions, List{string})"/>
    public static InvoiceSubscriptionDetailsItemOptions WithTaxRates(
        InvoiceSubscriptionDetailsItemOptions item, List<string>? taxRates)
    {
        if (taxRates is not null)
        {
            item.TaxRates = taxRates;
        }

        return item;
    }

    /// <inheritdoc cref="WithTaxRates(SessionLineItemOptions, List{string})"/>
    public static SubscriptionItemOptions WithTaxRates(SubscriptionItemOptions item, List<string>? taxRates)
    {
        if (taxRates is not null)
        {
            item.TaxRates = taxRates;
        }

        return item;
    }

    /// <inheritdoc cref="WithTaxRates(SessionLineItemOptions, List{string})"/>
    public static SubscriptionSchedulePhaseItemOptions WithTaxRates(
        SubscriptionSchedulePhaseItemOptions item, List<string>? taxRates)
    {
        if (taxRates is not null)
        {
            item.TaxRates = taxRates;
        }

        return item;
    }

    /// <summary>
    /// The lookup key of a plan's second-role add-on price at one amount, on one product. The product is part of the key, so
    /// an add-on price created before add-ons moved onto the plan's product is never reused and a new one is created under
    /// the plan's product on next use.
    /// </summary>
    /// <param name="planCode">The plan code.</param>
    /// <param name="productId">The provider product of the plan's price, which the add-on price is created on.</param>
    /// <param name="amountMinorUnits">The add-on price.</param>
    /// <param name="currency">ISO 4217 currency.</param>
    /// <param name="interval">Billing cadence.</param>
    public static string AddOnLookupKey(
        string planCode, string productId, long amountMinorUnits, string currency, BillingInterval interval) =>
        $"{AddOnLookupPrefix}{planCode}-{currency.ToLowerInvariant()}-{amountMinorUnits}-{IntervalName(interval)}-{productId}";

    /// <summary>A customer with the coach's locale and, for a non-VAT-payer, the invoice footer note.</summary>
    /// <param name="userId">The coach's user id, kept as metadata.</param>
    /// <param name="email">The coach's email.</param>
    /// <param name="name">Display name, or null.</param>
    /// <param name="language">The coach's language, or null.</param>
    /// <param name="options">Billing settings.</param>
    public static CustomerCreateOptions Customer(
        Guid userId, string email, string? name, string? language, BillingOptions options) => new()
    {
        Email = email,
        Name = name,
        PreferredLocales = [Locale(language)],
        Metadata = new Dictionary<string, string> { ["userId"] = userId.ToString() },
        InvoiceSettings = options.TaxMode == BillingTaxMode.None
            ? new CustomerInvoiceSettingsOptions { Footer = NotVatPayerFooter }
            : null,
    };

    /// <summary>
    /// A subscription Checkout session: no trial, billing address, company name and tax id collection, the coach's
    /// subscription tagged with their user and the plan. Promotion codes are allowed unless an automatic coupon is
    /// applied, because Stripe refuses both together.
    /// </summary>
    /// <param name="customerId">The provider customer id.</param>
    /// <param name="userId">The coach's user id, kept as metadata.</param>
    /// <param name="planCode">The plan code, kept as metadata.</param>
    /// <param name="planPriceId">The provider price id of the plan.</param>
    /// <param name="addOnPriceId">The provider price id of the add-on line, or null.</param>
    /// <param name="couponId">Automatic promotion coupon id, or null.</param>
    /// <param name="locale">The Stripe locale.</param>
    /// <param name="flow">Where the coach started; appended to both return URLs as <c>flow</c>.</param>
    /// <param name="options">Billing settings.</param>
    public static SessionCreateOptions Checkout(
        string customerId,
        Guid userId,
        string planCode,
        string planPriceId,
        string? addOnPriceId,
        string? couponId,
        string locale,
        CheckoutFlow flow,
        BillingOptions options)
    {
        var taxRates = TaxRates(options);
        var lineItems = new List<SessionLineItemOptions>
        {
            WithTaxRates(new SessionLineItemOptions { Price = planPriceId, Quantity = 1 }, taxRates),
        };

        if (addOnPriceId is not null)
        {
            lineItems.Add(WithTaxRates(new SessionLineItemOptions { Price = addOnPriceId, Quantity = 1 }, taxRates));
        }

        var metadata = new Dictionary<string, string>
        {
            ["userId"] = userId.ToString(),
            ["planCode"] = planCode,
        };

        return new SessionCreateOptions
        {
            Mode = "subscription",
            Customer = customerId,
            ClientReferenceId = userId.ToString(),
            LineItems = lineItems,
            AllowPromotionCodes = couponId is null ? true : null,
            Discounts = couponId is null ? null : [new SessionDiscountOptions { Coupon = couponId }],
            BillingAddressCollection = "required",
            NameCollection = new SessionNameCollectionOptions
            {
                Business = new SessionNameCollectionBusinessOptions { Enabled = true, Optional = true },
            },
            TaxIdCollection = new SessionTaxIdCollectionOptions { Enabled = true },
            CustomerUpdate = new SessionCustomerUpdateOptions { Address = "auto", Name = "auto" },
            Locale = locale,
            SuccessUrl = ReturnUrl(options.CheckoutSuccessUrl, flow),
            CancelUrl = ReturnUrl(options.CheckoutCancelUrl, flow),
            Metadata = metadata,
            SubscriptionData = new SessionSubscriptionDataOptions { Metadata = metadata },
        };
    }

    /// <summary>The value of the <c>flow</c> query parameter for a checkout flow.</summary>
    /// <param name="flow">The flow.</param>
    public static string FlowName(CheckoutFlow flow) => flow == CheckoutFlow.SignUp ? "signup" : "settings";

    /// <summary>
    /// A configured return URL with the checkout flow appended as <c>flow</c>. The base never comes from a request; a null
    /// base (billing not configured) stays null.
    /// </summary>
    /// <param name="configuredUrl">The configured success or cancel URL.</param>
    /// <param name="flow">The flow.</param>
    public static string? ReturnUrl(string? configuredUrl, CheckoutFlow flow) =>
        configuredUrl is null ? null : QueryHelpers.AddQueryString(configuredUrl, "flow", FlowName(flow));

    /// <summary>
    /// An invoice preview of adding the second-role add-on line to a subscription, with the proration that would be
    /// invoiced now. Existing items are untouched, as in the real update.
    /// </summary>
    /// <param name="customerId">The provider customer id.</param>
    /// <param name="subscriptionId">The provider subscription id.</param>
    /// <param name="addOnPriceId">The provider price id of the add-on line.</param>
    /// <param name="options">Billing settings.</param>
    public static InvoiceCreatePreviewOptions SecondRolePreview(
        string customerId, string subscriptionId, string addOnPriceId, BillingOptions options) => new()
    {
        Customer = customerId,
        Subscription = subscriptionId,
        SubscriptionDetails = new InvoiceSubscriptionDetailsOptions
        {
            Items =
            [
                WithTaxRates(new InvoiceSubscriptionDetailsItemOptions { Price = addOnPriceId, Quantity = 1 }, TaxRates(options)),
            ],
            ProrationBehavior = "always_invoice",
        },
    };

    /// <summary>
    /// A subscription update that adds the second-role add-on line, invoices the proration immediately and fails instead of
    /// leaving the subscription half-changed when the payment does not go through.
    /// </summary>
    /// <param name="addOnPriceId">The provider price id of the add-on line.</param>
    /// <param name="options">Billing settings.</param>
    public static SubscriptionUpdateOptions SecondRoleCharge(string addOnPriceId, BillingOptions options) => new()
    {
        Items = [WithTaxRates(new SubscriptionItemOptions { Price = addOnPriceId, Quantity = 1 }, TaxRates(options))],
        ProrationBehavior = "always_invoice",
        PaymentBehavior = "error_if_incomplete",
    };

    /// <summary>
    /// The same add-on charge as <see cref="SecondRoleCharge"/>, held as a pending update: when the card needs authentication
    /// Stripe keeps the subscription as it was and leaves the invoice open for the coach to pay on its hosted page. The latest
    /// invoice is expanded to read that page's URL.
    /// </summary>
    /// <param name="addOnPriceId">The provider price id of the add-on line.</param>
    /// <param name="options">Billing settings.</param>
    public static SubscriptionUpdateOptions SecondRoleChargePending(string addOnPriceId, BillingOptions options)
    {
        // UNVERIFIED: pending_if_incomplete accepts a new add-on item with tax_rates and always_invoice, and exposes latest_invoice.hosted_invoice_url — https://docs.stripe.com/billing/subscriptions/pending-updates
        var update = SecondRoleCharge(addOnPriceId, options);
        update.PaymentBehavior = "pending_if_incomplete";
        update.Expand = ["latest_invoice"];

        return update;
    }

    /// <summary>
    /// An immediate upgrade: swaps the plan line (and adds, replaces or removes the second-role add-on line), invoices the
    /// proration now, clears a scheduled cancellation, and fails instead of leaving the subscription half-changed when the
    /// payment does not go through.
    /// </summary>
    /// <param name="planItemId">The provider id of the subscription's plan line.</param>
    /// <param name="planPriceId">The provider price id of the target plan.</param>
    /// <param name="addOnItemId">The provider id of the subscription's add-on line, or null when it has none.</param>
    /// <param name="currentAddOnPriceId">The provider price id on the add-on line now, or null.</param>
    /// <param name="addOnPriceId">The provider price id the add-on line must have after the change, or null for no add-on.</param>
    /// <param name="options">Billing settings.</param>
    public static SubscriptionUpdateOptions PlanUpgrade(
        string planItemId,
        string planPriceId,
        string? addOnItemId,
        string? currentAddOnPriceId,
        string? addOnPriceId,
        BillingOptions options) =>
        PlanUpgradeWith("error_if_incomplete", planItemId, planPriceId, addOnItemId, currentAddOnPriceId, addOnPriceId, options);

    /// <summary>
    /// The same upgrade as <see cref="PlanUpgrade"/>, held as a pending update: the proration is invoiced now, and when the
    /// card needs authentication Stripe keeps the old plan and leaves the invoice open for the coach to pay on its hosted page.
    /// The latest invoice is expanded to read that page's URL.
    /// </summary>
    /// <param name="planItemId">The provider id of the subscription's plan line.</param>
    /// <param name="planPriceId">The provider price id of the target plan.</param>
    /// <param name="addOnItemId">The provider id of the subscription's add-on line, or null when it has none.</param>
    /// <param name="currentAddOnPriceId">The provider price id on the add-on line now, or null.</param>
    /// <param name="addOnPriceId">The provider price id the add-on line must have after the change, or null for no add-on.</param>
    /// <param name="options">Billing settings.</param>
    public static SubscriptionUpdateOptions PlanUpgradePending(
        string planItemId,
        string planPriceId,
        string? addOnItemId,
        string? currentAddOnPriceId,
        string? addOnPriceId,
        BillingOptions options)
    {
        // UNVERIFIED: pending_if_incomplete accepts item tax_rates, a deleted add-on line and cancel_at_period_end=false together — https://docs.stripe.com/billing/subscriptions/pending-updates
        var update = PlanUpgradeWith("pending_if_incomplete", planItemId, planPriceId, addOnItemId, currentAddOnPriceId, addOnPriceId, options);
        update.Expand = ["latest_invoice"];

        return update;
    }

    /// <summary>
    /// The metadata that snapshots a released schedule before a pending upgrade, so an upgrade that expires unpaid can put it
    /// back. A key with nothing to keep is sent empty, which removes a snapshot left by an earlier attempt.
    /// </summary>
    /// <param name="intent">What the released schedule promised.</param>
    public static Dictionary<string, string> RestoreSnapshot(ScheduleIntent intent) => new()
    {
        [RestorePricesKey] = intent.PendingPriceIds is { Count: > 0 } prices ? string.Join(',', prices) : string.Empty,
        [RestoreCancelKey] = intent.CancelRenewal ? "true" : string.Empty,
    };

    /// <summary>The metadata that remembers the role a pending second-role add-on buys.</summary>
    /// <param name="role">Trainer or Nutritionist.</param>
    public static Dictionary<string, string> PendingSecondRole(string role) => new() { [PendingSecondRoleKey] = role };

    /// <summary>The metadata that removes the remembered role (an empty value deletes the key at Stripe).</summary>
    public static Dictionary<string, string> ClearedPendingSecondRole() => new() { [PendingSecondRoleKey] = string.Empty };

    /// <summary>The metadata that removes a restore snapshot.</summary>
    public static Dictionary<string, string> ClearedRestoreSnapshot() => new()
    {
        [RestorePricesKey] = string.Empty,
        [RestoreCancelKey] = string.Empty,
    };

    /// <summary>True when the subscription's metadata holds any key of a restore snapshot.</summary>
    /// <param name="metadata">The subscription's metadata.</param>
    public static bool HasRestoreSnapshot(IReadOnlyDictionary<string, string>? metadata) =>
        metadata is not null
        && (metadata.ContainsKey(RestorePricesKey) || metadata.ContainsKey(RestoreCancelKey));

    /// <summary>Reads a snapshot written by <see cref="RestoreSnapshot"/>; null when the subscription holds none.</summary>
    /// <param name="metadata">The subscription's metadata.</param>
    public static ScheduleIntent? ReadRestoreSnapshot(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null || !HasRestoreSnapshot(metadata))
        {
            return null;
        }

        var prices = metadata.GetValueOrDefault(RestorePricesKey, string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new ScheduleIntent(
            prices.Length > 0 ? prices : null,
            metadata.GetValueOrDefault(RestoreCancelKey) == "true");
    }

    private static SubscriptionUpdateOptions PlanUpgradeWith(
        string paymentBehavior,
        string planItemId,
        string planPriceId,
        string? addOnItemId,
        string? currentAddOnPriceId,
        string? addOnPriceId,
        BillingOptions options)
    {
        var taxRates = TaxRates(options);

        return new SubscriptionUpdateOptions
        {
            Items = PlanChangeLines(
                planItemId,
                planPriceId,
                addOnItemId,
                currentAddOnPriceId,
                addOnPriceId,
                (itemId, priceId, deleted) => deleted
                    ? new SubscriptionItemOptions { Id = itemId, Deleted = true }
                    : WithTaxRates(new SubscriptionItemOptions { Id = itemId, Price = priceId, Quantity = 1 }, taxRates)),
            ProrationBehavior = "always_invoice",
            PaymentBehavior = paymentBehavior,
            CancelAtPeriodEnd = false,
        };
    }

    /// <summary>
    /// An invoice preview of the same change <see cref="PlanUpgrade"/> sends, with the proration that would be invoiced now.
    /// </summary>
    /// <param name="customerId">The provider customer id.</param>
    /// <param name="subscriptionId">The provider subscription id.</param>
    /// <param name="planItemId">The provider id of the subscription's plan line.</param>
    /// <param name="planPriceId">The provider price id of the target plan.</param>
    /// <param name="addOnItemId">The provider id of the subscription's add-on line, or null when it has none.</param>
    /// <param name="currentAddOnPriceId">The provider price id on the add-on line now, or null.</param>
    /// <param name="addOnPriceId">The provider price id the add-on line must have after the change, or null for no add-on.</param>
    /// <param name="options">Billing settings.</param>
    public static InvoiceCreatePreviewOptions PlanUpgradePreview(
        string customerId,
        string subscriptionId,
        string planItemId,
        string planPriceId,
        string? addOnItemId,
        string? currentAddOnPriceId,
        string? addOnPriceId,
        BillingOptions options)
    {
        var taxRates = TaxRates(options);

        return new InvoiceCreatePreviewOptions
        {
            Customer = customerId,
            Subscription = subscriptionId,
            SubscriptionDetails = new InvoiceSubscriptionDetailsOptions
            {
                Items = PlanChangeLines(
                    planItemId,
                    planPriceId,
                    addOnItemId,
                    currentAddOnPriceId,
                    addOnPriceId,
                    (itemId, priceId, deleted) => deleted
                        ? new InvoiceSubscriptionDetailsItemOptions { Id = itemId, Deleted = true }
                        : WithTaxRates(new InvoiceSubscriptionDetailsItemOptions { Id = itemId, Price = priceId, Quantity = 1 }, taxRates)),
                ProrationBehavior = "always_invoice",
            },
        };
    }

    /// <summary>A Customer Portal session that returns to the configured URL.</summary>
    /// <param name="customerId">The provider customer id.</param>
    /// <param name="locale">The Stripe locale.</param>
    /// <param name="options">Billing settings.</param>
    public static Stripe.BillingPortal.SessionCreateOptions Portal(string customerId, string locale, BillingOptions options) => new()
    {
        Customer = customerId,
        Locale = locale,
        ReturnUrl = options.PortalReturnUrl,
    };

    /// <summary>
    /// The id of a plan's one Stripe product, derived from the immutable plan code. Every price of the plan and its add-on
    /// prices live on this product, so a coupon limited to it keeps covering the plan after a price change.
    /// </summary>
    /// <param name="planCode">The plan code.</param>
    public static string PlanProductId(string planCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planCode);

        return $"{PlanProductPrefix}{planCode}";
    }

    /// <summary>The create options of a plan's stable product, under the id from <see cref="PlanProductId"/>.</summary>
    /// <param name="planCode">The plan code.</param>
    public static ProductCreateOptions PlanProduct(string planCode) => new()
    {
        Id = PlanProductId(planCode),
        Name = $"Form Up {planCode}",
        Metadata = new Dictionary<string, string> { ["planCode"] = planCode },
    };

    /// <summary>A recurring price for a plan, created on the plan's existing product.</summary>
    /// <param name="planCode">The plan code.</param>
    /// <param name="productId">The plan's stable product id.</param>
    /// <param name="amountMinorUnits">The price in the smallest currency unit; must be positive.</param>
    /// <param name="currency">ISO 4217 currency.</param>
    /// <param name="interval">Billing cadence.</param>
    public static PriceCreateOptions Price(
        string planCode, string productId, long amountMinorUnits, string currency, BillingInterval interval)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amountMinorUnits);
        ArgumentException.ThrowIfNullOrEmpty(productId);

        return new PriceCreateOptions
        {
            Currency = currency.ToLowerInvariant(),
            UnitAmount = amountMinorUnits,
            Recurring = new PriceRecurringOptions { Interval = IntervalName(interval) },
            Product = productId,
            Metadata = new Dictionary<string, string> { ["planCode"] = planCode },
        };
    }

    /// <summary>
    /// A recurring second-role add-on price, found again later by its lookup key. It is created on the plan's own product,
    /// so a coupon limited to that product discounts the plan line and the add-on line alike.
    /// </summary>
    /// <param name="planCode">The plan code.</param>
    /// <param name="productId">The provider product of the plan's price.</param>
    /// <param name="amountMinorUnits">The add-on price; must be positive.</param>
    /// <param name="currency">ISO 4217 currency.</param>
    /// <param name="interval">Billing cadence.</param>
    public static PriceCreateOptions AddOnPrice(
        string planCode, string productId, long amountMinorUnits, string currency, BillingInterval interval)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amountMinorUnits);
        ArgumentException.ThrowIfNullOrEmpty(productId);

        return new PriceCreateOptions
        {
            Currency = currency.ToLowerInvariant(),
            UnitAmount = amountMinorUnits,
            Recurring = new PriceRecurringOptions { Interval = IntervalName(interval) },
            Product = productId,
            Nickname = "Second role",
            LookupKey = AddOnLookupKey(planCode, productId, amountMinorUnits, currency, interval),
            Metadata = new Dictionary<string, string> { ["planCode"] = planCode, ["kind"] = "second-role-add-on" },
        };
    }

    /// <summary>
    /// A coupon for a promotion. The first month is Stripe's "once" (the first invoice), several months is "repeating".
    /// Max uses and the claim end are enforced by Stripe at Checkout; a product list restricts the discount to those lines.
    /// </summary>
    /// <param name="spec">The promotion's coupon terms.</param>
    /// <param name="productIds">Provider product ids the discount is limited to, or null for no restriction.</param>
    public static CouponCreateOptions Coupon(CouponSpec spec, List<string>? productIds)
    {
        var options = new CouponCreateOptions
        {
            Name = spec.Name,
            Duration = spec.Duration switch
            {
                PromotionDuration.FirstMonth => "once",
                PromotionDuration.Months => "repeating",
                _ => "forever",
            },
            DurationInMonths = spec.Duration == PromotionDuration.Months ? spec.DurationMonths : null,
            MaxRedemptions = spec.MaxUses,
            RedeemBy = spec.ClaimUntil?.UtcDateTime,
            AppliesTo = productIds is null ? null : new CouponAppliesToOptions { Products = productIds },
        };

        if (spec.Kind == PromotionKind.Percent)
        {
            options.PercentOff = spec.Value;
        }
        else
        {
            options.AmountOff = spec.Value;
            options.Currency = spec.Currency?.ToLowerInvariant();
        }

        return options;
    }

    /// <summary>
    /// A customer-facing promotion code for a coupon, optionally limited to customers who never paid before.
    /// </summary>
    /// <param name="couponId">The provider coupon id.</param>
    /// <param name="code">The code text.</param>
    /// <param name="expiresAt">Last moment the code works, or null.</param>
    /// <param name="firstTimeOnly">True to refuse customers who paid before.</param>
    public static PromotionCodeCreateOptions PromotionCode(
        string couponId, string code, DateTimeOffset? expiresAt, bool firstTimeOnly) => new()
    {
        Promotion = new PromotionCodePromotionOptions { Type = "coupon", Coupon = couponId },
        Code = code,
        ExpiresAt = expiresAt?.UtcDateTime,
        Restrictions = firstTimeOnly ? new PromotionCodeRestrictionsOptions { FirstTimeTransaction = true } : null,
    };

    /// <summary>
    /// Converts planned schedule phases to Stripe phases. Only the first phase carries a start (Stripe keeps the current
    /// phase's own); the open last phase runs one billing interval and then the schedule releases. Every phase sends an
    /// empty discount list.
    /// </summary>
    /// <param name="phases">The planned phases.</param>
    /// <param name="interval">Billing cadence of the subscription.</param>
    /// <param name="taxRates">Tax rate ids for every item, or null.</param>
    public static List<SubscriptionSchedulePhaseOptions> SchedulePhases(
        IReadOnlyList<SchedulePhasePlan> phases, BillingInterval interval, List<string>? taxRates)
    {
        var result = new List<SubscriptionSchedulePhaseOptions>();

        for (var i = 0; i < phases.Count; i++)
        {
            var phase = phases[i];
            var options = new SubscriptionSchedulePhaseOptions
            {
                Items = phase.PriceIds
                    .Select(priceId => WithTaxRates(new SubscriptionSchedulePhaseItemOptions { Price = priceId, Quantity = 1 }, taxRates))
                    .ToList(),
                Discounts = [],
                ProrationBehavior = "none",
            };

            if (i == 0)
            {
                options.StartDate = phase.Start.UtcDateTime;
            }

            if (phase.End is { } end)
            {
                options.EndDate = end.UtcDateTime;
            }
            else
            {
                options.Duration = new SubscriptionSchedulePhaseDurationOptions
                {
                    Interval = IntervalName(interval),
                    IntervalCount = 1,
                };
            }

            result.Add(options);
        }

        return result;
    }

    private static List<T> PlanChangeLines<T>(
        string planItemId,
        string planPriceId,
        string? addOnItemId,
        string? currentAddOnPriceId,
        string? addOnPriceId,
        Func<string?, string?, bool, T> make)
    {
        var lines = new List<T> { make(planItemId, planPriceId, false) };

        if (addOnPriceId is not null && addOnItemId is null)
        {
            lines.Add(make(null, addOnPriceId, false));
        }
        else if (addOnPriceId is not null && currentAddOnPriceId != addOnPriceId)
        {
            lines.Add(make(addOnItemId, addOnPriceId, false));
        }
        else if (addOnPriceId is null && addOnItemId is not null)
        {
            lines.Add(make(addOnItemId, null, true));
        }

        return lines;
    }
}
