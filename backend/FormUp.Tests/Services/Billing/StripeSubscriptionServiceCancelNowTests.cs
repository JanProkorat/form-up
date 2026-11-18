using System.Net;
using FluentAssertions;
using FormUp.Application.Infrastructure.Services.Billing;
using Microsoft.Extensions.Logging.Abstractions;
using Stripe;

namespace FormUp.Tests.Services.Billing;

/// <summary>
/// Drives <see cref="StripeSubscriptionService.CancelNowAsync"/> through the real Stripe.net client into a scripted
/// handler, so the requests (cancel without credit, then a refund of the unused share) are asserted. No network.
/// </summary>
public class StripeSubscriptionServiceCancelNowTests
{
    private const string SubscriptionId = "sub_1";
    private const string ScheduleId = "sub_sched_1";
    private const long PeriodStart = 1700000000;
    private const long TenDaysIn = PeriodStart + (10 * 86400);

    // The period is 30 days long and the subscription was cancelled on day 10, so two thirds of it is unused.
    private static string SubscriptionJson(string status, string? scheduleId, long? canceledAt = null) =>
        "{\"id\":\"sub_1\",\"object\":\"subscription\",\"status\":\"" + status + "\",\"schedule\":"
        + (scheduleId is null ? "null" : "\"" + scheduleId + "\"")
        + ",\"canceled_at\":" + (canceledAt?.ToString() ?? "null")
        + ",\"discounts\":[],\"items\":{\"object\":\"list\",\"has_more\":false,\"data\":[{\"id\":\"si_1\",\"object\":\"subscription_item\","
        + "\"current_period_start\":" + PeriodStart + ",\"current_period_end\":" + (PeriodStart + (30 * 86400))
        + ",\"price\":{\"id\":\"price_1\",\"object\":\"price\"}}]}}";

    private static string InvoicesJson(long amountPaid, long created = PeriodStart) =>
        "{\"object\":\"list\",\"has_more\":false,\"data\":[{\"id\":\"in_1\",\"object\":\"invoice\",\"status\":\"paid\",\"currency\":\"czk\","
        + "\"amount_paid\":" + amountPaid + ",\"created\":" + created + "}]}";

    private static string PaymentsJson(long amountPaid) =>
        "{\"object\":\"list\",\"has_more\":false,\"data\":[{\"id\":\"inpay_1\",\"object\":\"invoice_payment\",\"status\":\"paid\","
        + "\"amount_paid\":" + amountPaid + ",\"payment\":{\"type\":\"payment_intent\",\"payment_intent\":\"pi_1\"}}]}";

    private static string RefundsJson(long alreadyRefunded) =>
        alreadyRefunded == 0
            ? "{\"object\":\"list\",\"has_more\":false,\"data\":[]}"
            : "{\"object\":\"list\",\"has_more\":false,\"data\":[{\"id\":\"re_old\",\"object\":\"refund\",\"status\":\"succeeded\",\"amount\":" + alreadyRefunded + "}]}";

    private static StripeSubscriptionService NewService(ScriptedStripeHandler handler)
    {
        var client = new StripeClient("sk_test_placeholder", httpClient: new SystemNetHttpClient(new HttpClient(handler)));
        var options = new BillingOptions
        {
            SecretKey = "configured",
            TaxMode = BillingTaxMode.None,
            CheckoutSuccessUrl = "https://app.example/billing/success",
            CheckoutCancelUrl = "https://app.example/billing/cancel",
            PortalReturnUrl = "https://app.example/billing",
        };

        return new StripeSubscriptionService(
            Microsoft.Extensions.Options.Options.Create(options),
            NullLogger<StripeSubscriptionService>.Instance,
            client);
    }

    /// <summary>A live subscription that is first read active and, once cancelled, read again as canceled ten days in.</summary>
    private static ScriptedStripeHandler CancellableSubscription(string? scheduleId = null, string status = "active") =>
        new ScriptedStripeHandler(HttpStatusCode.OK, "{\"id\":\"obj_1\",\"object\":\"refund\"}")
            .Sequence(
                HttpMethod.Get,
                "/v1/subscriptions/sub_1",
                (HttpStatusCode.OK, SubscriptionJson(status, scheduleId)),
                (HttpStatusCode.OK, SubscriptionJson("canceled", scheduleId, TenDaysIn)));

    private static ScriptedStripeHandler WithPaidInvoice(ScriptedStripeHandler handler, long invoiced, long paid, long alreadyRefunded = 0) =>
        handler
            .Respond(HttpMethod.Get, "/v1/invoices", InvoicesJson(invoiced))
            .Respond(HttpMethod.Get, "/v1/invoice_payments", PaymentsJson(paid))
            .Respond(HttpMethod.Get, "/v1/refunds", RefundsJson(alreadyRefunded));

    /// <summary>Body fields and query parameters together: Stripe.net may send a DELETE's parameters in either place.</summary>
    private static Dictionary<string, string> ParametersOf(CapturedRequest request)
    {
        var parameters = request.Form.ToDictionary(pair => pair.Key, pair => pair.Value);

        foreach (var part in request.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            parameters[pair[0]] = pair.Length > 1 ? pair[1] : string.Empty;
        }

        return parameters;
    }

    private static Task<CancelRefundResult> CancelAsync(ScriptedStripeHandler handler) =>
        NewService(handler).CancelNowAsync(SubscriptionId, TestContext.Current.CancellationToken);

    [Fact]
    public async Task CancelNow_CancelsWithoutCreditOrFinalInvoice_ThenRefundsTheUnusedShareOfThePaidInvoice()
    {
        var handler = WithPaidInvoice(CancellableSubscription(), invoiced: 30000, paid: 30000);

        var result = await CancelAsync(handler);

        var cancel = handler.Requests.Should().ContainSingle(r => r.Method == HttpMethod.Delete).Subject;
        cancel.Path.Should().Be("/v1/subscriptions/sub_1");
        ParametersOf(cancel)["prorate"].Should().Be("false");
        ParametersOf(cancel)["invoice_now"].Should().Be("false");
        handler.Requests.Should().NotContain(r => r.Path.StartsWith("/v1/subscription_schedules", StringComparison.Ordinal));

        var refund = handler.Requests.Should().ContainSingle(r => r.Method == HttpMethod.Post && r.Path == "/v1/refunds").Subject;
        refund.Form.Should().Contain(new KeyValuePair<string, string>("payment_intent", "pi_1"));
        refund.Form.Should().Contain(new KeyValuePair<string, string>("amount", "20000"));
        refund.IdempotencyKey.Should().Be("formup-refund-sub_1");
        handler.Requests.IndexOf(refund).Should().BeGreaterThan(handler.Requests.IndexOf(cancel), "nothing is refunded before the cancel succeeded");
        result.Should().Be(new CancelRefundResult(20000, "czk"));
    }

    [Fact]
    public async Task CancelNow_WithASchedule_CancelsTheSchedule_WithoutReleasingItOrCancelingTheSubscriptionItself()
    {
        var handler = CancellableSubscription(ScheduleId)
            .Respond(HttpMethod.Post, "/v1/subscription_schedules/sub_sched_1/cancel", "{\"id\":\"sub_sched_1\",\"object\":\"subscription_schedule\"}")
            .Respond(HttpMethod.Get, "/v1/invoices", "{\"object\":\"list\",\"has_more\":false,\"data\":[]}");

        var result = await CancelAsync(handler);

        var cancel = handler.Requests.Should().ContainSingle(r => r.Path == "/v1/subscription_schedules/sub_sched_1/cancel").Subject;
        ParametersOf(cancel)["prorate"].Should().Be("false", "the schedule cancel defaults to crediting unused time");
        ParametersOf(cancel)["invoice_now"].Should().Be("false");
        handler.Requests.Should().NotContain(r => r.Path.EndsWith("/release", StringComparison.Ordinal));
        handler.Requests.Should().NotContain(r => r.Method == HttpMethod.Delete);
        result.Should().Be(CancelRefundResult.None, "no paid invoice means nothing to refund");
    }

    [Fact]
    public async Task CancelNow_WhenTheInvoiceCollectedNothing_CancelsAndMakesNoRefundCall()
    {
        var handler = WithPaidInvoice(CancellableSubscription(), invoiced: 0, paid: 0);

        var result = await CancelAsync(handler);

        handler.Requests.Should().ContainSingle(r => r.Method == HttpMethod.Delete);
        handler.Requests.Should().NotContain(r => r.Path.StartsWith("/v1/refunds", StringComparison.Ordinal));
        result.Should().Be(CancelRefundResult.None);
    }

    [Fact]
    public async Task CancelNow_WhenThePaymentHoldsLessThanTheShare_RefundsNoMoreThanWasPaid()
    {
        var handler = WithPaidInvoice(CancellableSubscription(), invoiced: 30000, paid: 12000);

        var result = await CancelAsync(handler);

        result.RefundedMinorUnits.Should().Be(12000);
        handler.Requests.Single(r => r.Method == HttpMethod.Post && r.Path == "/v1/refunds")
            .Form.Should().Contain(new KeyValuePair<string, string>("amount", "12000"));
    }

    [Fact]
    public async Task CancelNow_PastDue_CancelsButNeverLooksForAPaymentToRefund()
    {
        var handler = CancellableSubscription(status: "past_due");

        var result = await CancelAsync(handler);

        handler.Requests.Should().ContainSingle(r => r.Method == HttpMethod.Delete);
        handler.Requests.Should().NotContain(r => r.Path.StartsWith("/v1/invoices", StringComparison.Ordinal));
        result.Should().Be(CancelRefundResult.None);
    }

    [Fact]
    public async Task CancelNow_WhenTheLatestPaidInvoiceBelongsToAnEarlierPeriod_RefundsNothing()
    {
        var handler = CancellableSubscription()
            .Respond(HttpMethod.Get, "/v1/invoices", InvoicesJson(30000, created: PeriodStart - (30 * 86400)));

        var result = await CancelAsync(handler);

        handler.Requests.Should().NotContain(r => r.Path.StartsWith("/v1/refunds", StringComparison.Ordinal));
        result.Should().Be(CancelRefundResult.None);
    }

    [Fact]
    public async Task CancelNow_WhenAlreadyCanceled_SendsNoCancelRequest_AndRefundsOnlyWhatIsStillDue()
    {
        var handler = new ScriptedStripeHandler(HttpStatusCode.OK, "{\"id\":\"re_new\",\"object\":\"refund\"}")
            .Respond(HttpMethod.Get, "/v1/subscriptions/sub_1", SubscriptionJson("canceled", ScheduleId, TenDaysIn));
        WithPaidInvoice(handler, invoiced: 30000, paid: 30000, alreadyRefunded: 15000);

        var result = await CancelAsync(handler);

        handler.Requests.Should().NotContain(r => r.Method == HttpMethod.Delete || r.Path.Contains("/cancel", StringComparison.Ordinal));
        handler.Requests.Single(r => r.Method == HttpMethod.Post && r.Path == "/v1/refunds")
            .Form.Should().Contain(new KeyValuePair<string, string>("amount", "5000"), "20000 is due and 15000 already went back");
        result.RefundedMinorUnits.Should().Be(5000);
    }

    [Fact]
    public async Task CancelNow_WhenAnEarlierAttemptRefundedEverything_MakesNoSecondRefund()
    {
        var handler = new ScriptedStripeHandler(HttpStatusCode.OK, "{\"id\":\"obj_1\"}")
            .Respond(HttpMethod.Get, "/v1/subscriptions/sub_1", SubscriptionJson("canceled", null, TenDaysIn));
        WithPaidInvoice(handler, invoiced: 30000, paid: 30000, alreadyRefunded: 20000);

        var result = await CancelAsync(handler);

        handler.Requests.Should().NotContain(r => r.Method == HttpMethod.Post);
        result.Should().Be(CancelRefundResult.None);
    }

    [Fact]
    public async Task CancelNow_WhenTheRefundFails_ThrowsBillingProviderException_AfterTheCancel()
    {
        var handler = WithPaidInvoice(CancellableSubscription(), invoiced: 30000, paid: 30000)
            .Fail(HttpMethod.Post, "/v1/refunds", HttpStatusCode.BadRequest, "{\"error\":{\"type\":\"invalid_request_error\"}}");

        var act = () => CancelAsync(handler);

        await act.Should().ThrowAsync<BillingProviderException>();
        handler.Requests.Should().ContainSingle(r => r.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task CancelNow_WhenStripeRefusesTheCancel_ThrowsBillingProviderException_AndNeverRefunds()
    {
        var handler = new ScriptedStripeHandler(HttpStatusCode.OK, "{}")
            .Respond(HttpMethod.Get, "/v1/subscriptions/sub_1", SubscriptionJson("active", null))
            .Fail(HttpMethod.Delete, "/v1/subscriptions/sub_1", HttpStatusCode.BadRequest, "{\"error\":{\"type\":\"invalid_request_error\"}}");

        var act = () => CancelAsync(handler);

        await act.Should().ThrowAsync<BillingProviderException>();
        handler.Requests.Should().NotContain(r => r.Path.StartsWith("/v1/refunds", StringComparison.Ordinal));
    }
}
