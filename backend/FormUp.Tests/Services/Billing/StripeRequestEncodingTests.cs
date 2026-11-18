using System.Net;
using System.Text;
using FluentAssertions;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Infrastructure.Services.Billing;
using Stripe;

namespace FormUp.Tests.Services.Billing;

/// <summary>
/// Sends the options built by <see cref="StripeRequestBuilder"/> through the real Stripe.net encoder into a capture
/// handler, so the form body Stripe would receive is asserted. No network. Stripe.net sends an explicitly assigned
/// null as an empty value, which Stripe rejects, so an absent key is the contract.
/// </summary>
public class StripeRequestEncodingTests
{
    private const string PlaceholderApiKey = "sk_test_placeholder";
    private const string CannedJson = "{\"id\":\"obj_1\"}";
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static BillingOptions Options(BillingTaxMode taxMode) => new()
    {
        SecretKey = "configured",
        TaxMode = taxMode,
        VatTaxRateId = taxMode == BillingTaxMode.Vat21 ? "txr_test" : null,
        CheckoutSuccessUrl = "https://app.example/billing/success",
        CheckoutCancelUrl = "https://app.example/billing/cancel",
        PortalReturnUrl = "https://app.example/billing",
    };

    private static (StripeClient Client, CaptureHandler Handler) NewClient()
    {
        var handler = new CaptureHandler();
        var client = new StripeClient(PlaceholderApiKey, httpClient: new SystemNetHttpClient(new HttpClient(handler)));

        return (client, handler);
    }

    private static void AssertNoTaxRatesAndNoEmptyValues(List<KeyValuePair<string, string>> form)
    {
        form.Should().NotContain(pair => pair.Key.Contains("tax_rates"));
        form.Should().NotContain(pair => pair.Value.Length == 0, "an empty value is an 'unset' that Stripe rejects on create");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("promo_coupon")]
    public async Task Checkout_TaxModeNone_SendsNoTaxRatesAndNoEmptyValues(string? couponId)
    {
        var (client, handler) = NewClient();
        var options = StripeRequestBuilder.Checkout(
            "cus_1", UserId, "pro", "price_plan", "price_addon", couponId, "cs", CheckoutFlow.Settings, Options(BillingTaxMode.None));

        await client.V1.Checkout.Sessions.CreateAsync(options, cancellationToken: TestContext.Current.CancellationToken);

        AssertNoTaxRatesAndNoEmptyValues(handler.Form);
    }

    [Fact]
    public async Task Checkout_Vat21_PutsTheRateOnEveryLine()
    {
        var (client, handler) = NewClient();
        var options = StripeRequestBuilder.Checkout(
            "cus_1", UserId, "pro", "price_plan", "price_addon", null, "cs", CheckoutFlow.Settings, Options(BillingTaxMode.Vat21));

        await client.V1.Checkout.Sessions.CreateAsync(options, cancellationToken: TestContext.Current.CancellationToken);

        handler.Form.Should().Contain(new KeyValuePair<string, string>("line_items[0][tax_rates][0]", "txr_test"));
        handler.Form.Should().Contain(new KeyValuePair<string, string>("line_items[1][tax_rates][0]", "txr_test"));
    }

    [Fact]
    public async Task Customer_TaxModeNone_SendsNoEmptyValues()
    {
        var (client, handler) = NewClient();
        var options = StripeRequestBuilder.Customer(UserId, "coach@example.test", null, null, Options(BillingTaxMode.None));

        await client.V1.Customers.CreateAsync(options, cancellationToken: TestContext.Current.CancellationToken);

        AssertNoTaxRatesAndNoEmptyValues(handler.Form);
    }

    [Fact]
    public async Task Customer_Vat21_SendsNoEmptyValues()
    {
        var (client, handler) = NewClient();
        var options = StripeRequestBuilder.Customer(UserId, "coach@example.test", null, null, Options(BillingTaxMode.Vat21));

        await client.V1.Customers.CreateAsync(options, cancellationToken: TestContext.Current.CancellationToken);

        AssertNoTaxRatesAndNoEmptyValues(handler.Form);
    }

    [Fact]
    public async Task SecondRoleCharge_TaxModeNone_SendsNoTaxRatesAndNoEmptyValues()
    {
        var (client, handler) = NewClient();
        var options = StripeRequestBuilder.SecondRoleCharge("price_addon", Options(BillingTaxMode.None));

        await client.V1.Subscriptions.UpdateAsync("sub_1", options, cancellationToken: TestContext.Current.CancellationToken);

        AssertNoTaxRatesAndNoEmptyValues(handler.Form);
    }

    [Fact]
    public async Task SecondRoleCharge_Vat21_PutsTheRateOnTheItem()
    {
        var (client, handler) = NewClient();
        var options = StripeRequestBuilder.SecondRoleCharge("price_addon", Options(BillingTaxMode.Vat21));

        await client.V1.Subscriptions.UpdateAsync("sub_1", options, cancellationToken: TestContext.Current.CancellationToken);

        handler.Form.Should().Contain(new KeyValuePair<string, string>("items[0][tax_rates][0]", "txr_test"));
    }

    [Fact]
    public async Task SecondRoleChargePending_TaxModeNone_SendsNoTaxRatesNoEmptyValues_ButTheExpandAndTheHold()
    {
        var (client, handler) = NewClient();
        var options = StripeRequestBuilder.SecondRoleChargePending("price_addon", Options(BillingTaxMode.None));

        await client.V1.Subscriptions.UpdateAsync("sub_1", options, cancellationToken: TestContext.Current.CancellationToken);

        AssertNoTaxRatesAndNoEmptyValues(handler.Form);
        handler.Form.Should().Contain(new KeyValuePair<string, string>("payment_behavior", "pending_if_incomplete"));
        handler.Form.Should().Contain(new KeyValuePair<string, string>("expand[0]", "latest_invoice"));
    }

    [Fact]
    public async Task SecondRoleChargePending_Vat21_PutsTheRateOnTheItem()
    {
        var (client, handler) = NewClient();
        var options = StripeRequestBuilder.SecondRoleChargePending("price_addon", Options(BillingTaxMode.Vat21));

        await client.V1.Subscriptions.UpdateAsync("sub_1", options, cancellationToken: TestContext.Current.CancellationToken);

        handler.Form.Should().Contain(new KeyValuePair<string, string>("items[0][tax_rates][0]", "txr_test"));
    }

    [Fact]
    public async Task SecondRolePreview_TaxModeNone_SendsNoTaxRatesAndNoEmptyValues()
    {
        var (client, handler) = NewClient();
        var options = StripeRequestBuilder.SecondRolePreview("cus_1", "sub_1", "price_addon", Options(BillingTaxMode.None));

        await client.V1.Invoices.CreatePreviewAsync(options, cancellationToken: TestContext.Current.CancellationToken);

        AssertNoTaxRatesAndNoEmptyValues(handler.Form);
    }

    [Fact]
    public async Task SecondRolePreview_Vat21_PutsTheRateOnTheItem()
    {
        var (client, handler) = NewClient();
        var options = StripeRequestBuilder.SecondRolePreview("cus_1", "sub_1", "price_addon", Options(BillingTaxMode.Vat21));

        await client.V1.Invoices.CreatePreviewAsync(options, cancellationToken: TestContext.Current.CancellationToken);

        handler.Form.Should().Contain(new KeyValuePair<string, string>("subscription_details[items][0][tax_rates][0]", "txr_test"));
    }

    [Fact]
    public async Task PlanUpgrade_TaxModeNone_SendsNoTaxRates_ButTheCancellationClear()
    {
        var (client, handler) = NewClient();
        var options = StripeRequestBuilder.PlanUpgrade("si_plan", "price_new", null, null, "price_addon", Options(BillingTaxMode.None));

        await client.V1.Subscriptions.UpdateAsync("sub_1", options, cancellationToken: TestContext.Current.CancellationToken);

        AssertNoTaxRatesAndNoEmptyValues(handler.Form);
        handler.Form.Should().Contain(new KeyValuePair<string, string>("cancel_at_period_end", "false"));
    }

    [Fact]
    public async Task PlanUpgrade_Vat21_PutsTheRateOnEveryChargedItem()
    {
        var (client, handler) = NewClient();
        var options = StripeRequestBuilder.PlanUpgrade("si_plan", "price_new", null, null, "price_addon", Options(BillingTaxMode.Vat21));

        await client.V1.Subscriptions.UpdateAsync("sub_1", options, cancellationToken: TestContext.Current.CancellationToken);

        handler.Form.Should().Contain(new KeyValuePair<string, string>("items[0][tax_rates][0]", "txr_test"));
        handler.Form.Should().Contain(new KeyValuePair<string, string>("items[1][tax_rates][0]", "txr_test"));
    }

    [Fact]
    public async Task PlanUpgradePending_TaxModeNone_SendsNoTaxRatesNoEmptyValues_ButTheExpandAndTheHold()
    {
        var (client, handler) = NewClient();
        var options = StripeRequestBuilder.PlanUpgradePending("si_plan", "price_new", null, null, "price_addon", Options(BillingTaxMode.None));

        await client.V1.Subscriptions.UpdateAsync("sub_1", options, cancellationToken: TestContext.Current.CancellationToken);

        AssertNoTaxRatesAndNoEmptyValues(handler.Form);
        handler.Form.Should().Contain(new KeyValuePair<string, string>("payment_behavior", "pending_if_incomplete"));
        handler.Form.Should().Contain(new KeyValuePair<string, string>("expand[0]", "latest_invoice"));
    }

    [Fact]
    public async Task PlanUpgradePending_Vat21_PutsTheRateOnEveryChargedItem()
    {
        var (client, handler) = NewClient();
        var options = StripeRequestBuilder.PlanUpgradePending("si_plan", "price_new", null, null, "price_addon", Options(BillingTaxMode.Vat21));

        await client.V1.Subscriptions.UpdateAsync("sub_1", options, cancellationToken: TestContext.Current.CancellationToken);

        handler.Form.Should().Contain(new KeyValuePair<string, string>("items[0][tax_rates][0]", "txr_test"));
        handler.Form.Should().Contain(new KeyValuePair<string, string>("items[1][tax_rates][0]", "txr_test"));
    }

    [Fact]
    public async Task RestoreSnapshot_SendsEveryKey_WithEmptyValuesAsUnsets()
    {
        var (client, handler) = NewClient();
        var options = new SubscriptionUpdateOptions { Metadata = StripeRequestBuilder.RestoreSnapshot(new ScheduleIntent(["price_a"])) };

        await client.V1.Subscriptions.UpdateAsync("sub_1", options, cancellationToken: TestContext.Current.CancellationToken);

        handler.Form.Should().Contain(new KeyValuePair<string, string>("metadata[formup_restore_prices]", "price_a"));
        handler.Form.Should().Contain(new KeyValuePair<string, string>("metadata[formup_restore_cancel]", string.Empty));
    }

    [Fact]
    public async Task PlanUpgradePreview_TaxModeNone_SendsNoTaxRatesAndNoEmptyValues()
    {
        var (client, handler) = NewClient();
        var options = StripeRequestBuilder.PlanUpgradePreview(
            "cus_1", "sub_1", "si_plan", "price_new", "si_addon", "price_addon_old", null, Options(BillingTaxMode.None));

        await client.V1.Invoices.CreatePreviewAsync(options, cancellationToken: TestContext.Current.CancellationToken);

        AssertNoTaxRatesAndNoEmptyValues(handler.Form);
        handler.Form.Should().Contain(new KeyValuePair<string, string>("subscription_details[items][1][deleted]", "true"));
    }

    [Theory]
    [InlineData(BillingTaxMode.None)]
    [InlineData(BillingTaxMode.Vat21)]
    public async Task SchedulePhases_TaxRatesFollowTheMode(BillingTaxMode taxMode)
    {
        var (client, handler) = NewClient();
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var phases = StripeRequestBuilder.SchedulePhases(
            [new SchedulePhasePlan(start, start.AddMonths(1), ["price_plan", "price_addon"])],
            BillingInterval.Monthly,
            StripeRequestBuilder.TaxRates(Options(taxMode)));

        await client.V1.SubscriptionSchedules.UpdateAsync(
            "sub_sched_1",
            new SubscriptionScheduleUpdateOptions { Phases = phases },
            cancellationToken: TestContext.Current.CancellationToken);

        if (taxMode == BillingTaxMode.None)
        {
            handler.Form.Should().NotContain(pair => pair.Key.Contains("tax_rates"));
        }
        else
        {
            handler.Form.Should().Contain(new KeyValuePair<string, string>("phases[0][items][0][tax_rates][0]", "txr_test"));
            handler.Form.Should().Contain(new KeyValuePair<string, string>("phases[0][items][1][tax_rates][0]", "txr_test"));
        }
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public List<KeyValuePair<string, string>> Form { get; private set; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);

            Form = body.Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2))
                .Select(parts => new KeyValuePair<string, string>(Decode(parts[0]), parts.Length > 1 ? Decode(parts[1]) : string.Empty))
                .ToList();

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(CannedJson, Encoding.UTF8, "application/json"),
            };
        }

        private static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));
    }
}
