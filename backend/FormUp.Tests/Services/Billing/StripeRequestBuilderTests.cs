using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Services.Billing;
using FluentAssertions;

namespace FormUp.Tests.Services.Billing;

/// <summary>
/// The Stripe request options carry the billing rules: no trial, promotion codes, billing details, tax, CZK, locale and
/// the invoice note. Built as plain functions, so nothing here touches the network.
/// </summary>
public class StripeRequestBuilderTests
{
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static BillingOptions Options(BillingTaxMode taxMode = BillingTaxMode.None) => new()
    {
        SecretKey = "configured",
        TaxMode = taxMode,
        VatTaxRateId = taxMode == BillingTaxMode.Vat21 ? "txr_test" : null,
        CheckoutSuccessUrl = "https://app.example/billing/success",
        CheckoutCancelUrl = "https://app.example/billing/cancel",
        PortalReturnUrl = "https://app.example/billing",
    };

    [Theory]
    [InlineData(null, "cs")]
    [InlineData("", "cs")]
    [InlineData("en", "en")]
    [InlineData("de-AT", "de")]
    [InlineData("CS", "cs")]
    [InlineData("fr", "cs")]
    public void Locale_MapsLanguageToASupportedStripeLocale(string? language, string expected)
    {
        StripeRequestBuilder.Locale(language).Should().Be(expected);
    }

    [Fact]
    public void Customer_NonVatPayer_CarriesTheInvoiceNoteAndLocale()
    {
        var customer = StripeRequestBuilder.Customer(UserId, "coach@example.test", "Coach", "de", Options());

        customer.InvoiceSettings!.Footer.Should().Be("Nejsem plátce DPH");
        customer.PreferredLocales.Should().Equal("de");
        customer.Metadata.Should().Contain("userId", UserId.ToString());
    }

    [Fact]
    public void Customer_VatPayer_HasNoNonVatPayerNote()
    {
        var customer = StripeRequestBuilder.Customer(UserId, "coach@example.test", null, null, Options(BillingTaxMode.Vat21));

        customer.InvoiceSettings.Should().BeNull();
        customer.PreferredLocales.Should().Equal("cs");
    }

    [Fact]
    public void Checkout_BuildsASubscriptionWithNoTrialAndBillingDetailsCollection()
    {
        var session = StripeRequestBuilder.Checkout("cus_1", UserId, "pro", "price_plan", null, null, "cs", CheckoutFlow.Settings, Options());

        session.Mode.Should().Be("subscription");
        session.Customer.Should().Be("cus_1");
        session.AllowPromotionCodes.Should().BeTrue();
        session.Discounts.Should().BeNull();
        session.BillingAddressCollection.Should().Be("required");
        session.TaxIdCollection!.Enabled.Should().BeTrue();
        session.NameCollection!.Business!.Enabled.Should().BeTrue();
        session.CustomerUpdate!.Address.Should().Be("auto");
        session.SubscriptionData!.TrialPeriodDays.Should().BeNull();
        session.SubscriptionData.TrialEnd.Should().BeNull();
        session.Locale.Should().Be("cs");
        session.SuccessUrl.Should().Be("https://app.example/billing/success?flow=settings");
        session.CancelUrl.Should().Be("https://app.example/billing/cancel?flow=settings");
        session.SubscriptionData.Metadata.Should().Contain("planCode", "pro");
        session.LineItems.Should().ContainSingle().Which.Price.Should().Be("price_plan");
    }

    [Fact]
    public void Checkout_SignUpFlow_IsAppendedToBothReturnUrls()
    {
        var session = StripeRequestBuilder.Checkout("cus_1", UserId, "pro", "price_plan", null, null, "cs", CheckoutFlow.SignUp, Options());

        session.SuccessUrl.Should().Be("https://app.example/billing/success?flow=signup");
        session.CancelUrl.Should().Be("https://app.example/billing/cancel?flow=signup");
    }

    [Theory]
    [InlineData("https://app.example/billing/success?session={CHECKOUT_SESSION_ID}", "https://app.example/billing/success?session={CHECKOUT_SESSION_ID}&flow=settings")]
    [InlineData("https://app.example/billing?x=1#done", "https://app.example/billing?x=1&flow=settings#done")]
    public void ReturnUrl_KeepsTheConfiguredQueryAndFragment(string configured, string expected)
    {
        StripeRequestBuilder.ReturnUrl(configured, CheckoutFlow.Settings).Should().Be(expected);
    }

    [Fact]
    public void SecondRolePreview_AddsTheAddOnLineAndInvoicesTheProrationNow()
    {
        var preview = StripeRequestBuilder.SecondRolePreview("cus_1", "sub_1", "price_addon", Options(BillingTaxMode.Vat21));

        preview.Customer.Should().Be("cus_1");
        preview.Subscription.Should().Be("sub_1");
        preview.SubscriptionDetails!.ProrationBehavior.Should().Be("always_invoice");
        var item = preview.SubscriptionDetails.Items.Should().ContainSingle().Subject;
        item.Price.Should().Be("price_addon");
        item.Id.Should().BeNull("a new line, existing items stay");
        item.TaxRates.Should().Equal("txr_test");
    }

    [Fact]
    public void SecondRoleCharge_FailsInsteadOfLeavingTheSubscriptionHalfChanged()
    {
        var update = StripeRequestBuilder.SecondRoleCharge("price_addon", Options());

        update.PaymentBehavior.Should().Be("error_if_incomplete");
        update.ProrationBehavior.Should().Be("always_invoice");
        update.Items.Should().ContainSingle().Which.Price.Should().Be("price_addon");
    }

    [Fact]
    public void Checkout_WithAddOn_AddsASecondLine()
    {
        var session = StripeRequestBuilder.Checkout("cus_1", UserId, "pro", "price_plan", "price_addon", null, "cs", CheckoutFlow.Settings, Options());

        session.LineItems.Select(line => line.Price).Should().Equal("price_plan", "price_addon");
    }

    [Fact]
    public void Checkout_WithAutomaticPromotion_AppliesTheCouponInsteadOfPromotionCodes()
    {
        var session = StripeRequestBuilder.Checkout("cus_1", UserId, "pro", "price_plan", null, "coupon_1", "cs", CheckoutFlow.Settings, Options());

        session.Discounts.Should().ContainSingle().Which.Coupon.Should().Be("coupon_1");
        session.AllowPromotionCodes.Should().BeNull("Stripe refuses discounts together with allow_promotion_codes");
    }

    [Fact]
    public void Checkout_TaxRateComesFromConfiguration()
    {
        var withVat = StripeRequestBuilder.Checkout("cus_1", UserId, "pro", "price_plan", "price_addon", null, "cs", CheckoutFlow.Settings, Options(BillingTaxMode.Vat21));
        var withoutVat = StripeRequestBuilder.Checkout("cus_1", UserId, "pro", "price_plan", "price_addon", null, "cs", CheckoutFlow.Settings, Options());

        withVat.LineItems.Should().OnlyContain(line => line.TaxRates!.SequenceEqual(new[] { "txr_test" }));
        withoutVat.LineItems.Should().OnlyContain(line => line.TaxRates == null);
    }

    [Fact]
    public void Portal_ReturnsToTheConfiguredUrl()
    {
        var portal = StripeRequestBuilder.Portal("cus_1", "en", Options());

        portal.Customer.Should().Be("cus_1");
        portal.ReturnUrl.Should().Be("https://app.example/billing");
        portal.Locale.Should().Be("en");
    }

    [Theory]
    [InlineData(BillingInterval.Monthly, "month")]
    [InlineData(BillingInterval.Annual, "year")]
    public void Price_IsARecurringCzkAmountInTheBillingInterval(BillingInterval interval, string expected)
    {
        var price = StripeRequestBuilder.Price("pro", 49900, "CZK", interval);

        price.Currency.Should().Be("czk");
        price.UnitAmount.Should().Be(49900);
        price.Recurring!.Interval.Should().Be(expected);
        price.ProductData!.Name.Should().Be("Form Up pro");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Price_ZeroOrNegative_IsRefused(long amount)
    {
        var build = () => StripeRequestBuilder.Price("free", amount, "CZK", BillingInterval.Monthly);

        build.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void AddOnPrice_CanBeFoundAgainByLookupKey()
    {
        var price = StripeRequestBuilder.AddOnPrice("pro", 9900, "CZK", BillingInterval.Monthly);

        price.LookupKey.Should().Be("formup-addon-pro-czk-9900-month");
        price.LookupKey.Should().StartWith(StripeRequestBuilder.AddOnLookupPrefix);
    }

    [Fact]
    public void GiftCoupon_IsAForeverHundredPercentCoupon()
    {
        var coupon = StripeRequestBuilder.GiftCoupon();

        coupon.PercentOff.Should().Be(100);
        coupon.Duration.Should().Be("forever");
        coupon.Id.Should().Be(StripeRequestBuilder.GiftCouponId);
    }

    [Fact]
    public void SchedulePhases_FirstPhaseStartsNow_LastRunsOneIntervalAndNoGiftCarriesOver()
    {
        var start = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        var end = start.AddDays(10);
        var phases = StripeRequestBuilder.SchedulePhases(
            [
                new SchedulePhasePlan(start, end, ["price_old"], "formup-gift-100"),
                new SchedulePhasePlan(end, null, ["price_old"], null),
            ],
            BillingInterval.Annual,
            null);

        phases[0].StartDate.Should().NotBeNull();
        phases[0].EndDate.Should().NotBeNull();
        phases[0].Discounts.Should().ContainSingle().Which.Coupon.Should().Be("formup-gift-100");
        phases[1].StartDate.Should().BeNull();
        phases[1].Duration!.Interval.Should().Be("year");
        phases[1].Duration!.IntervalCount.Should().Be(1);
        phases[1].Discounts.Should().BeEmpty("an empty list clears the gift discount");
    }

    [Theory]
    [InlineData("sk", "https://a.example/s", "https://a.example/c", "https://a.example/p", BillingTaxMode.None, null, true)]
    [InlineData(null, "https://a.example/s", "https://a.example/c", "https://a.example/p", BillingTaxMode.None, null, false)]
    [InlineData("sk", "not a url", "https://a.example/c", "https://a.example/p", BillingTaxMode.None, null, false)]
    [InlineData("sk", "javascript:alert(1)", "https://a.example/c", "https://a.example/p", BillingTaxMode.None, null, false)]
    [InlineData("sk", "https://a.example/s", "https://a.example/c", "https://a.example/p", BillingTaxMode.Vat21, null, false)]
    [InlineData("sk", "https://a.example/s", "https://a.example/c", "https://a.example/p", BillingTaxMode.Vat21, "txr_1", true)]
    public void BillingOptions_IsConfigured_NeedsKeyUrlsAndTaxRate(
        string? key, string success, string cancel, string portal, BillingTaxMode taxMode, string? taxRate, bool expected)
    {
        var options = new BillingOptions
        {
            SecretKey = key,
            CheckoutSuccessUrl = success,
            CheckoutCancelUrl = cancel,
            PortalReturnUrl = portal,
            TaxMode = taxMode,
            VatTaxRateId = taxRate,
        };

        options.IsConfigured.Should().Be(expected);
    }
}
