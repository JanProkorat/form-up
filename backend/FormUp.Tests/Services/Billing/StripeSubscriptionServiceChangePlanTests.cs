using System.Net;
using System.Text;
using FluentAssertions;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Services.Billing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Stripe;

namespace FormUp.Tests.Services.Billing;

/// <summary>
/// Drives <see cref="StripeSubscriptionService.ChangePlanAsync"/> through the real Stripe.net client into a scripted
/// handler, so the requests of an immediate upgrade (the charge, the restore of a released schedule) are asserted. No network.
/// </summary>
public class StripeSubscriptionServiceChangePlanTests
{
    private const string SubscriptionId = "sub_1";
    private const string ScheduleId = "sub_sched_1";

    private const string SubscriptionJson = """
        {"id":"sub_1","object":"subscription","schedule":null,"discounts":[],
         "items":{"object":"list","has_more":false,"data":[
           {"id":"si_plan","object":"subscription_item","current_period_start":1700000000,"current_period_end":1702592000,
            "price":{"id":"price_old","object":"price","product":"prod_1","recurring":{"interval":"month"}}}]}}
        """;

    private const string SubscriptionWithScheduleJson = """
        {"id":"sub_1","object":"subscription","schedule":"sub_sched_1","discounts":[],
         "items":{"object":"list","has_more":false,"data":[
           {"id":"si_plan","object":"subscription_item","current_period_start":1700000000,"current_period_end":1702592000,
            "price":{"id":"price_old","object":"price","product":"prod_1","recurring":{"interval":"month"}}}]}}
        """;

    private const string ScheduleJson = """
        {"id":"sub_sched_1","object":"subscription_schedule",
         "current_phase":{"start_date":1700000000,"end_date":1702592000},
         "phases":[
           {"start_date":1700000000,"end_date":1702592000,"items":[{"price":"price_old","quantity":1}]},
           {"start_date":1702592000,"end_date":1705270400,"items":[{"price":"price_cheaper","quantity":1}]}]}
        """;

    private const string CardDeclinedJson = """
        {"error":{"type":"card_error","code":"card_declined","message":"Your card was declined."}}
        """;

    private static BillingOptions Options() => new()
    {
        SecretKey = "configured",
        TaxMode = BillingTaxMode.None,
        CheckoutSuccessUrl = "https://app.example/billing/success",
        CheckoutCancelUrl = "https://app.example/billing/cancel",
        PortalReturnUrl = "https://app.example/billing",
    };

    private static StripeSubscriptionService NewService(ScriptedHandler handler)
    {
        var client = new StripeClient("sk_test_placeholder", httpClient: new SystemNetHttpClient(new HttpClient(handler)));

        return new StripeSubscriptionService(
            Microsoft.Extensions.Options.Options.Create(Options()),
            TimeProvider.System,
            NullLogger<StripeSubscriptionService>.Instance,
            client);
    }

    private static Task UpgradeAsync(StripeSubscriptionService service, string? key = "formup-plan-upgrade-key") =>
        service.ChangePlanAsync(
            SubscriptionId,
            "pro",
            "price_new",
            null,
            "CZK",
            BillingInterval.Monthly,
            atNextRenewal: false,
            TestContext.Current.CancellationToken,
            key);

    [Fact]
    public async Task ChangePlan_Upgrade_ChargesTheProrationNow_ClearsTheCancellation_AndSendsTheIdempotencyKey()
    {
        var handler = new ScriptedHandler(HttpStatusCode.OK, SubscriptionJson);

        await UpgradeAsync(NewService(handler));

        var update = handler.Requests.Single(r => r.Method == HttpMethod.Post && r.Path == "/v1/subscriptions/sub_1");
        update.Form.Should().Contain(new KeyValuePair<string, string>("proration_behavior", "always_invoice"));
        update.Form.Should().Contain(new KeyValuePair<string, string>("payment_behavior", "error_if_incomplete"));
        update.Form.Should().Contain(new KeyValuePair<string, string>("cancel_at_period_end", "false"));
        update.Form.Should().Contain(new KeyValuePair<string, string>("items[0][id]", "si_plan"));
        update.Form.Should().Contain(new KeyValuePair<string, string>("items[0][price]", "price_new"));
        update.IdempotencyKey.Should().Be("formup-plan-upgrade-key");
        handler.Requests.Should().NotContain(r => r.Path.StartsWith("/v1/subscription_schedules", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChangePlan_UpgradeWithADeclinedCardAndNoSchedule_Throws_AndTouchesNoSchedule()
    {
        var handler = new ScriptedHandler(HttpStatusCode.OK, SubscriptionJson)
            .Fail(HttpMethod.Post, "/v1/subscriptions/sub_1", HttpStatusCode.PaymentRequired, CardDeclinedJson);

        var act = () => UpgradeAsync(NewService(handler));

        await act.Should().ThrowAsync<BillingProviderException>();
        handler.Requests.Should().NotContain(r => r.Path.StartsWith("/v1/subscription_schedules", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChangePlan_UpgradeWithADeclinedCardAndAScheduledDowngrade_RestoresTheSchedule_ThenThrows()
    {
        var handler = new ScriptedHandler(HttpStatusCode.OK, "{\"id\":\"obj_1\"}")
            .Respond(HttpMethod.Get, "/v1/subscriptions/sub_1", SubscriptionWithScheduleJson)
            .Respond(HttpMethod.Get, "/v1/subscription_schedules/sub_sched_1", ScheduleJson)
            .Respond(HttpMethod.Post, "/v1/subscription_schedules", "{\"id\":\"sub_sched_new\",\"object\":\"subscription_schedule\"}")
            .Fail(HttpMethod.Post, "/v1/subscriptions/sub_1", HttpStatusCode.PaymentRequired, CardDeclinedJson);

        var act = () => UpgradeAsync(NewService(handler));

        await act.Should().ThrowAsync<BillingProviderException>();
        var paths = handler.Requests.Where(r => r.Method == HttpMethod.Post).Select(r => r.Path).ToList();
        paths.Should().Equal(
            "/v1/subscription_schedules/sub_sched_1/release",
            "/v1/subscriptions/sub_1",
            "/v1/subscription_schedules",
            "/v1/subscription_schedules/sub_sched_new");
        var restore = handler.Requests.Last();
        restore.Form.Should().Contain(new KeyValuePair<string, string>("phases[0][items][0][price]", "price_old"));
        restore.Form.Should().Contain(new KeyValuePair<string, string>("phases[1][items][0][price]", "price_cheaper"));
    }

    [Fact]
    public async Task ChangePlan_UpgradeWithADeclinedCardWhenTheRestoreAlsoFails_StillThrowsTheProviderError()
    {
        var handler = new ScriptedHandler(HttpStatusCode.OK, "{\"id\":\"obj_1\"}")
            .Respond(HttpMethod.Get, "/v1/subscriptions/sub_1", SubscriptionWithScheduleJson)
            .Respond(HttpMethod.Get, "/v1/subscription_schedules/sub_sched_1", ScheduleJson)
            .Fail(HttpMethod.Post, "/v1/subscription_schedules", HttpStatusCode.InternalServerError, "{\"error\":{\"type\":\"api_error\"}}")
            .Fail(HttpMethod.Post, "/v1/subscriptions/sub_1", HttpStatusCode.PaymentRequired, CardDeclinedJson);

        var act = () => UpgradeAsync(NewService(handler));

        var thrown = await act.Should().ThrowAsync<BillingProviderException>();
        thrown.Which.InnerException.Should().BeOfType<StripeException>()
            .Which.HttpStatusCode.Should().Be(HttpStatusCode.PaymentRequired, "the original charge failure is what surfaces");
    }

    private sealed record CapturedRequest(HttpMethod Method, string Path, List<KeyValuePair<string, string>> Form, string? IdempotencyKey);

    private sealed class ScriptedHandler(HttpStatusCode defaultStatus, string defaultJson) : HttpMessageHandler
    {
        private readonly List<(HttpMethod Method, string Path, HttpStatusCode Status, string Json)> rules = [];

        public List<CapturedRequest> Requests { get; } = [];

        public ScriptedHandler Respond(HttpMethod method, string path, string json) => Fail(method, path, HttpStatusCode.OK, json);

        public ScriptedHandler Fail(HttpMethod method, string path, HttpStatusCode status, string json)
        {
            rules.Add((method, path, status, json));

            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var path = request.RequestUri!.AbsolutePath;
            var form = body.Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2))
                .Select(parts => new KeyValuePair<string, string>(Decode(parts[0]), parts.Length > 1 ? Decode(parts[1]) : string.Empty))
                .ToList();
            var key = request.Headers.TryGetValues("Idempotency-Key", out var values) ? values.Single() : null;

            Requests.Add(new CapturedRequest(request.Method, path, form, key));

            var rule = rules.FirstOrDefault(r => r.Method == request.Method && r.Path == path);
            var (status, json) = rule.Path is null ? (defaultStatus, defaultJson) : (rule.Status, rule.Json);

            return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }

        private static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));
    }
}
