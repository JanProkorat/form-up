using System.Net;
using FluentAssertions;
using FormUp.Application.Infrastructure.Services.Billing;
using Microsoft.Extensions.Logging.Abstractions;
using Stripe;

namespace FormUp.Tests.Services.Billing;

/// <summary>
/// Drives <see cref="StripeSubscriptionService.CancelNowAsync"/> through the real Stripe.net client into a scripted
/// handler, so the requests (and that no refund or proration is asked for) are asserted. No network.
/// </summary>
public class StripeSubscriptionServiceCancelNowTests
{
    private const string SubscriptionId = "sub_1";
    private const string ScheduleId = "sub_sched_1";

    private static string SubscriptionJson(string status, string? scheduleId) =>
        "{\"id\":\"sub_1\",\"object\":\"subscription\",\"status\":\"" + status + "\",\"schedule\":"
        + (scheduleId is null ? "null" : "\"" + scheduleId + "\"")
        + ",\"discounts\":[],\"items\":{\"object\":\"list\",\"has_more\":false,\"data\":[]}}";

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
            TimeProvider.System,
            NullLogger<StripeSubscriptionService>.Instance,
            client);
    }

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

    private static Task CancelAsync(ScriptedStripeHandler handler) =>
        NewService(handler).CancelNowAsync(SubscriptionId, TestContext.Current.CancellationToken);

    [Fact]
    public async Task CancelNow_WithoutASchedule_CancelsTheSubscription_WithoutRefundOrFinalInvoice()
    {
        var handler = new ScriptedStripeHandler(HttpStatusCode.OK, SubscriptionJson("canceled", null))
            .Respond(HttpMethod.Get, "/v1/subscriptions/sub_1", SubscriptionJson("active", null));

        await CancelAsync(handler);

        var cancel = handler.Requests.Should().ContainSingle(r => r.Method == HttpMethod.Delete).Subject;
        cancel.Path.Should().Be("/v1/subscriptions/sub_1");
        var parameters = ParametersOf(cancel);
        parameters["prorate"].Should().Be("false");
        parameters["invoice_now"].Should().Be("false");
        handler.Requests.Should().NotContain(r => r.Path.StartsWith("/v1/subscription_schedules", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancelNow_WithASchedule_CancelsTheSchedule_WithoutReleasingItOrCancelingTheSubscriptionItself()
    {
        var handler = new ScriptedStripeHandler(HttpStatusCode.OK, "{\"id\":\"sub_sched_1\",\"object\":\"subscription_schedule\"}")
            .Respond(HttpMethod.Get, "/v1/subscriptions/sub_1", SubscriptionJson("active", ScheduleId));

        await CancelAsync(handler);

        var cancel = handler.Requests.Should().ContainSingle(r => r.Method == HttpMethod.Post).Subject;
        cancel.Path.Should().Be("/v1/subscription_schedules/sub_sched_1/cancel");
        var parameters = ParametersOf(cancel);
        parameters["prorate"].Should().Be("false", "the schedule cancel defaults to crediting unused time");
        parameters["invoice_now"].Should().Be("false");
        handler.Requests.Should().NotContain(r => r.Path.EndsWith("/release", StringComparison.Ordinal));
        handler.Requests.Should().NotContain(r => r.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task CancelNow_WhenAlreadyCanceled_SendsNoCancelRequest()
    {
        var handler = new ScriptedStripeHandler(HttpStatusCode.OK, "{}")
            .Respond(HttpMethod.Get, "/v1/subscriptions/sub_1", SubscriptionJson("canceled", ScheduleId));

        await CancelAsync(handler);

        handler.Requests.Should().ContainSingle().Which.Method.Should().Be(HttpMethod.Get);
    }

    [Fact]
    public async Task CancelNow_WhenStripeRefuses_ThrowsBillingProviderException()
    {
        var handler = new ScriptedStripeHandler(HttpStatusCode.OK, "{}")
            .Respond(HttpMethod.Get, "/v1/subscriptions/sub_1", SubscriptionJson("active", null))
            .Fail(HttpMethod.Delete, "/v1/subscriptions/sub_1", HttpStatusCode.BadRequest, "{\"error\":{\"type\":\"invalid_request_error\"}}");

        var act = () => CancelAsync(handler);

        await act.Should().ThrowAsync<BillingProviderException>();
    }
}
