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

    /// <summary>Id of the reusable forever 100 % coupon behind "given free".</summary>
    public const string GiftCouponId = "formup-gift-100";

    /// <summary>Prefix of the lookup key that identifies a second-role add-on price.</summary>
    public const string AddOnLookupPrefix = "formup-addon-";

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
            new() { Price = planPriceId, Quantity = 1, TaxRates = taxRates },
        };

        if (addOnPriceId is not null)
        {
            lineItems.Add(new SessionLineItemOptions { Price = addOnPriceId, Quantity = 1, TaxRates = taxRates });
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
                new InvoiceSubscriptionDetailsItemOptions { Price = addOnPriceId, Quantity = 1, TaxRates = TaxRates(options) },
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
        Items = [new SubscriptionItemOptions { Price = addOnPriceId, Quantity = 1, TaxRates = TaxRates(options) }],
        ProrationBehavior = "always_invoice",
        PaymentBehavior = "error_if_incomplete",
    };

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

    /// <summary>A recurring price for a plan; the product is created with it.</summary>
    /// <param name="planCode">The plan code.</param>
    /// <param name="amountMinorUnits">The price in the smallest currency unit; must be positive.</param>
    /// <param name="currency">ISO 4217 currency.</param>
    /// <param name="interval">Billing cadence.</param>
    public static PriceCreateOptions Price(string planCode, long amountMinorUnits, string currency, BillingInterval interval)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amountMinorUnits);

        return new PriceCreateOptions
        {
            Currency = currency.ToLowerInvariant(),
            UnitAmount = amountMinorUnits,
            Recurring = new PriceRecurringOptions { Interval = IntervalName(interval) },
            ProductData = new PriceProductDataOptions { Name = $"Form Up {planCode}" },
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

    /// <summary>The reusable forever 100 % coupon.</summary>
    public static CouponCreateOptions GiftCoupon() => new()
    {
        Id = GiftCouponId,
        PercentOff = 100,
        Duration = "forever",
        Name = "Form Up given plan",
    };

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
    /// phase's own); the open last phase runs one billing interval and then the schedule releases. A phase without a coupon
    /// sends an empty discount list so a previous gift cannot carry over.
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
                    .Select(priceId => new SubscriptionSchedulePhaseItemOptions { Price = priceId, Quantity = 1, TaxRates = taxRates })
                    .ToList(),
                Discounts = phase.CouponId is null
                    ? []
                    : [new SubscriptionSchedulePhaseDiscountOptions { Coupon = phase.CouponId }],
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
}
