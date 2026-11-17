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

    private static StripeSubscriptionService NewService(ScriptedStripeHandler handler)
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
        var handler = new ScriptedStripeHandler(HttpStatusCode.OK, SubscriptionJson);

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
        var handler = new ScriptedStripeHandler(HttpStatusCode.OK, SubscriptionJson)
            .Fail(HttpMethod.Post, "/v1/subscriptions/sub_1", HttpStatusCode.PaymentRequired, CardDeclinedJson);

        var act = () => UpgradeAsync(NewService(handler));

        await act.Should().ThrowAsync<BillingProviderException>();
        handler.Requests.Should().NotContain(r => r.Path.StartsWith("/v1/subscription_schedules", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChangePlan_UpgradeWithADeclinedCardAndAScheduledDowngrade_RestoresTheSchedule_ThenThrows()
    {
        var handler = new ScriptedStripeHandler(HttpStatusCode.OK, "{\"id\":\"obj_1\"}")
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
        var handler = new ScriptedStripeHandler(HttpStatusCode.OK, "{\"id\":\"obj_1\"}")
            .Respond(HttpMethod.Get, "/v1/subscriptions/sub_1", SubscriptionWithScheduleJson)
            .Respond(HttpMethod.Get, "/v1/subscription_schedules/sub_sched_1", ScheduleJson)
            .Fail(HttpMethod.Post, "/v1/subscription_schedules", HttpStatusCode.InternalServerError, "{\"error\":{\"type\":\"api_error\"}}")
            .Fail(HttpMethod.Post, "/v1/subscriptions/sub_1", HttpStatusCode.PaymentRequired, CardDeclinedJson);

        var act = () => UpgradeAsync(NewService(handler));

        var thrown = await act.Should().ThrowAsync<BillingProviderException>();
        thrown.Which.InnerException.Should().BeOfType<StripeException>()
            .Which.HttpStatusCode.Should().Be(HttpStatusCode.PaymentRequired, "the original charge failure is what surfaces");
    }

    private const string GiftOnEveryPhaseScheduleJson = """
        {"id":"sub_sched_1","object":"subscription_schedule",
         "current_phase":{"start_date":1700000000,"end_date":1702592000},
         "phases":[
           {"start_date":1700000000,"end_date":1702592000,"items":[{"price":"price_old","quantity":1}],"discounts":[{"coupon":"coupon_gift"}]},
           {"start_date":1702592000,"end_date":1705270400,"items":[{"price":"price_cheaper","quantity":1}],"discounts":[{"coupon":"coupon_gift"}]}]}
        """;

    private const string GiftEndingWithThePeriodScheduleJson = """
        {"id":"sub_sched_1","object":"subscription_schedule",
         "current_phase":{"start_date":1700000000,"end_date":1702592000},
         "phases":[
           {"start_date":1700000000,"end_date":1702592000,"items":[{"price":"price_old","quantity":1}],"discounts":[{"coupon":"coupon_gift"}]},
           {"start_date":1702592000,"end_date":1705270400,"items":[{"price":"price_cheaper","quantity":1}]}]}
        """;

    private const string SubscriptionWithAddOnJson = """
        {"id":"sub_1","object":"subscription","schedule":null,"discounts":[],
         "items":{"object":"list","has_more":false,"data":[
           {"id":"si_plan","object":"subscription_item","current_period_start":1700000000,"current_period_end":1702592000,
            "price":{"id":"price_old","object":"price","product":"prod_1","recurring":{"interval":"month"}}},
           {"id":"si_addon","object":"subscription_item","current_period_start":1700000000,"current_period_end":1702592000,
            "price":{"id":"price_addon","object":"price","product":"prod_1","lookup_key":"formup-addon-pro-czk-9900-month-prod_1","recurring":{"interval":"month"}}}]}}
        """;

    [Fact]
    public async Task ChangePlan_ScheduledWithoutTheAddOn_KeepsTheAddOnThisPeriod_AndDropsItFromTheNextPhase_WithoutProration()
    {
        var handler = new ScriptedStripeHandler(HttpStatusCode.OK, "{\"id\":\"obj_1\"}")
            .Respond(HttpMethod.Get, "/v1/subscriptions/sub_1", SubscriptionWithAddOnJson)
            .Respond(HttpMethod.Post, "/v1/subscription_schedules", "{\"id\":\"sub_sched_new\",\"object\":\"subscription_schedule\"}");

        await NewService(handler).ChangePlanAsync(
            SubscriptionId, "pro", "price_old", null, "CZK", BillingInterval.Monthly, atNextRenewal: true, TestContext.Current.CancellationToken);

        var update = handler.Requests.Last();
        update.Path.Should().Be("/v1/subscription_schedules/sub_sched_new");
        update.Form.Should().Contain(new KeyValuePair<string, string>("phases[0][items][0][price]", "price_old"));
        update.Form.Should().Contain(new KeyValuePair<string, string>("phases[0][items][1][price]", "price_addon"));
        update.Form.Should().Contain(new KeyValuePair<string, string>("phases[1][items][0][price]", "price_old"));
        update.Form.Should().NotContain(pair => pair.Key.StartsWith("phases[1][items][1]", StringComparison.Ordinal));
        update.Form.Should().Contain(new KeyValuePair<string, string>("proration_behavior", "none"));
    }

    private static Task ClearAsync(StripeSubscriptionService service) =>
        service.ClearScheduledPriceChangeAsync(SubscriptionId, TestContext.Current.CancellationToken);

    [Fact]
    public async Task ClearScheduledPriceChange_WithoutASchedule_CallsNothingToChange()
    {
        var handler = new ScriptedStripeHandler(HttpStatusCode.OK, SubscriptionJson);

        await ClearAsync(NewService(handler));

        handler.Requests.Should().NotContain(r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task ClearScheduledPriceChange_WithAScheduledDowngradeAndNoGift_ReleasesTheSchedule()
    {
        var handler = new ScriptedStripeHandler(HttpStatusCode.OK, "{\"id\":\"obj_1\"}")
            .Respond(HttpMethod.Get, "/v1/subscriptions/sub_1", SubscriptionWithScheduleJson)
            .Respond(HttpMethod.Get, "/v1/subscription_schedules/sub_sched_1", ScheduleJson);

        await ClearAsync(NewService(handler));

        handler.Requests.Where(r => r.Method == HttpMethod.Post).Select(r => r.Path)
            .Should().Equal("/v1/subscription_schedules/sub_sched_1/release");
    }

    [Fact]
    public async Task ClearScheduledPriceChange_WithAnEndlessGift_ReleasesTheScheduleAndPutsTheGiftBackAsADiscount()
    {
        var handler = new ScriptedStripeHandler(HttpStatusCode.OK, "{\"id\":\"obj_1\"}")
            .Respond(HttpMethod.Get, "/v1/subscriptions/sub_1", SubscriptionWithScheduleJson)
            .Respond(HttpMethod.Get, "/v1/subscription_schedules/sub_sched_1", GiftOnEveryPhaseScheduleJson);

        await ClearAsync(NewService(handler));

        handler.Requests.Where(r => r.Method == HttpMethod.Post).Select(r => r.Path)
            .Should().Equal("/v1/subscription_schedules/sub_sched_1/release", "/v1/subscriptions/sub_1");
        handler.Requests.Last().Form.Should().Contain(new KeyValuePair<string, string>("discounts[0][coupon]", "coupon_gift"));
    }

    [Fact]
    public async Task ClearScheduledPriceChange_WithAGiftThatEnds_RewritesTheSchedulePhasesOnTheCurrentPrices()
    {
        var handler = new ScriptedStripeHandler(HttpStatusCode.OK, "{\"id\":\"obj_1\"}")
            .Respond(HttpMethod.Get, "/v1/subscriptions/sub_1", SubscriptionWithScheduleJson)
            .Respond(HttpMethod.Get, "/v1/subscription_schedules/sub_sched_1", GiftEndingWithThePeriodScheduleJson);

        await ClearAsync(NewService(handler));

        var update = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        update.Path.Should().Be("/v1/subscription_schedules/sub_sched_1");
        update.Form.Should().Contain(new KeyValuePair<string, string>("phases[0][items][0][price]", "price_old"));
        update.Form.Should().Contain(new KeyValuePair<string, string>("phases[1][items][0][price]", "price_old"));
        update.Form.Should().NotContain(pair => pair.Value == "price_cheaper");
        update.Form.Should().Contain(new KeyValuePair<string, string>("proration_behavior", "none"));
    }

    [Fact]
    public async Task ResumeRenewal_ClearsTheCancelFlagAndTheCancelAtDate_InOneUpdate()
    {
        var handler = new ScriptedStripeHandler(HttpStatusCode.OK, SubscriptionJson);

        await NewService(handler).ResumeRenewalAsync(SubscriptionId, TestContext.Current.CancellationToken);

        var update = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        update.Path.Should().Be("/v1/subscriptions/sub_1");
        update.Form.Should().Contain(new KeyValuePair<string, string>("cancel_at_period_end", "false"));
        update.Form.Should().Contain(new KeyValuePair<string, string>("cancel_at", string.Empty));
        update.Form.Should().NotContain(pair => pair.Key.StartsWith("items", StringComparison.Ordinal));
    }

    private const string AuthenticationRequiredJson = """
        {"error":{"type":"card_error","code":"subscription_payment_intent_requires_action","message":"Authentication required."}}
        """;

    private const string PendingInvoiceUrl = "https://invoice.stripe.test/i/in_pending";

    private const string PendingSubscriptionJson = """
        {"id":"sub_1","object":"subscription","schedule":null,"discounts":[],
         "latest_invoice":{"id":"in_pending","object":"invoice","status":"open","hosted_invoice_url":"https://invoice.stripe.test/i/in_pending"},
         "items":{"object":"list","has_more":false,"data":[
           {"id":"si_plan","object":"subscription_item","current_period_start":1700000000,"current_period_end":1702592000,
            "price":{"id":"price_old","object":"price","product":"prod_1","recurring":{"interval":"month"}}}]}}
        """;

    private const string PendingWithoutUrlJson = """
        {"id":"sub_1","object":"subscription","schedule":null,"discounts":[],
         "latest_invoice":{"id":"in_pending","object":"invoice","status":"open"},
         "items":{"object":"list","has_more":false,"data":[
           {"id":"si_plan","object":"subscription_item","current_period_start":1700000000,"current_period_end":1702592000,
            "price":{"id":"price_old","object":"price","product":"prod_1","recurring":{"interval":"month"}}}]}}
        """;

    private const string SubscriptionWithPendingUpdateTemplate = """
        {"id":"sub_1","object":"subscription","schedule":null,"discounts":[],
         "pending_update":{"expires_at":1700050000,"subscription_items":[{"id":"si_plan","object":"subscription_item","price":{"id":"TARGET","object":"price"}}]},
         "latest_invoice":{"id":"in_old","object":"invoice","status":"open","hosted_invoice_url":"https://invoice.stripe.test/i/in_old"},
         "items":{"object":"list","has_more":false,"data":[
           {"id":"si_plan","object":"subscription_item","current_period_start":1700000000,"current_period_end":1702592000,
            "price":{"id":"price_old","object":"price","product":"prod_1","recurring":{"interval":"month"}}}]}}
        """;

    private static string SubscriptionWithPendingUpdateJson(string targetPriceId) =>
        SubscriptionWithPendingUpdateTemplate.Replace("TARGET", targetPriceId, StringComparison.Ordinal);

    private const string SubscriptionWithScheduleAndInvoiceJson = """
        {"id":"sub_1","object":"subscription","schedule":"sub_sched_1","discounts":[],
         "items":{"object":"list","has_more":false,"data":[
           {"id":"si_plan","object":"subscription_item","current_period_start":1700000000,"current_period_end":1702592000,
            "price":{"id":"price_old","object":"price","product":"prod_1","recurring":{"interval":"month"}}}]}}
        """;

    private static Task<PlanUpgradeOutcome> UpgradeNowAsync(StripeSubscriptionService service) =>
        service.UpgradePlanNowAsync(
            SubscriptionId, "pro", "price_new", null, "CZK", BillingInterval.Monthly, "formup-key", TestContext.Current.CancellationToken);

    private static ScriptedStripeHandler AuthenticationRequiredThenPending(ScriptedStripeHandler handler, string pendingJson, bool snapshotCall = false) =>
        handler.Sequence(
            HttpMethod.Post,
            "/v1/subscriptions/sub_1",
            snapshotCall
                ? [(HttpStatusCode.PaymentRequired, AuthenticationRequiredJson), (HttpStatusCode.OK, SubscriptionJson), (HttpStatusCode.OK, pendingJson)]
                : [(HttpStatusCode.PaymentRequired, AuthenticationRequiredJson), (HttpStatusCode.OK, pendingJson)]);

    [Fact]
    public async Task UpgradePlanNow_WhenTheCardNeedsNoAuthentication_AppliesAtOnce_WithoutAPendingUpdate()
    {
        var handler = new ScriptedStripeHandler(HttpStatusCode.OK, SubscriptionJson);

        var outcome = await UpgradeNowAsync(NewService(handler));

        outcome.IsAwaitingPayment.Should().BeFalse();
        outcome.PaymentUrl.Should().BeNull();
        var update = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        update.Form.Should().Contain(new KeyValuePair<string, string>("payment_behavior", "error_if_incomplete"));
        update.IdempotencyKey.Should().Be("formup-key");
    }

    [Fact]
    public async Task UpgradePlanNow_WhenTheCardNeedsAuthentication_RepeatsAsAPendingUpdate_AndReturnsTheHostedInvoiceUrl()
    {
        var handler = AuthenticationRequiredThenPending(new ScriptedStripeHandler(HttpStatusCode.OK, SubscriptionJson), PendingSubscriptionJson);

        var outcome = await UpgradeNowAsync(NewService(handler));

        outcome.IsAwaitingPayment.Should().BeTrue();
        outcome.PaymentUrl.Should().Be(PendingInvoiceUrl);
        var updates = handler.Requests.Where(r => r.Method == HttpMethod.Post && r.Path == "/v1/subscriptions/sub_1").ToList();
        updates.Should().HaveCount(2, "no schedule and no earlier snapshot, so no metadata call in between");
        var pending = updates[1];
        pending.Form.Should().Contain(new KeyValuePair<string, string>("payment_behavior", "pending_if_incomplete"));
        pending.Form.Should().Contain(new KeyValuePair<string, string>("proration_behavior", "always_invoice"));
        pending.Form.Should().Contain(new KeyValuePair<string, string>("items[0][price]", "price_new"));
        pending.Form.Should().Contain(new KeyValuePair<string, string>("cancel_at_period_end", "false"));
        pending.Form.Should().Contain(new KeyValuePair<string, string>("expand[0]", "latest_invoice"));
        pending.IdempotencyKey.Should().Be("formup-key-pending");
    }

    [Fact]
    public async Task UpgradePlanNow_WhenAuthenticationIsNeededAndADowngradeWasScheduled_SnapshotsItBeforeThePendingUpdate()
    {
        var handler = AuthenticationRequiredThenPending(
            new ScriptedStripeHandler(HttpStatusCode.OK, "{\"id\":\"obj_1\"}")
                .Respond(HttpMethod.Get, "/v1/subscriptions/sub_1", SubscriptionWithScheduleAndInvoiceJson)
                .Respond(HttpMethod.Get, "/v1/subscription_schedules/sub_sched_1", ScheduleJson),
            PendingSubscriptionJson,
            snapshotCall: true);

        var outcome = await UpgradeNowAsync(NewService(handler));

        outcome.PaymentUrl.Should().Be(PendingInvoiceUrl);
        var posts = handler.Requests.Where(r => r.Method == HttpMethod.Post).ToList();
        posts.Select(r => r.Path).Should().Equal(
            "/v1/subscription_schedules/sub_sched_1/release",
            "/v1/subscriptions/sub_1",
            "/v1/subscriptions/sub_1",
            "/v1/subscriptions/sub_1");
        posts[2].Form.Should().Contain(new KeyValuePair<string, string>("metadata[formup_restore_prices]", "price_cheaper"));
        posts[3].Form.Should().Contain(new KeyValuePair<string, string>("payment_behavior", "pending_if_incomplete"));
    }

    [Fact]
    public async Task UpgradePlanNow_WithADeclinedCard_RestoresTheSchedule_ThrowsAndNeverGoesPending()
    {
        var handler = new ScriptedStripeHandler(HttpStatusCode.OK, "{\"id\":\"obj_1\"}")
            .Respond(HttpMethod.Get, "/v1/subscriptions/sub_1", SubscriptionWithScheduleAndInvoiceJson)
            .Respond(HttpMethod.Get, "/v1/subscription_schedules/sub_sched_1", ScheduleJson)
            .Respond(HttpMethod.Post, "/v1/subscription_schedules", "{\"id\":\"sub_sched_new\",\"object\":\"subscription_schedule\"}")
            .Fail(HttpMethod.Post, "/v1/subscriptions/sub_1", HttpStatusCode.PaymentRequired, CardDeclinedJson);

        var act = () => UpgradeNowAsync(NewService(handler));

        await act.Should().ThrowAsync<BillingProviderException>();
        handler.Requests.Should().NotContain(r => r.Form.Contains(new KeyValuePair<string, string>("payment_behavior", "pending_if_incomplete")));
        handler.Requests.Last().Path.Should().Be("/v1/subscription_schedules/sub_sched_new");
    }

    [Fact]
    public async Task UpgradePlanNow_WhenThePendingUpdateHasNoPaymentPage_RestoresTheScheduleAndTheSnapshot_ThenThrows()
    {
        var handler = AuthenticationRequiredThenPending(
            new ScriptedStripeHandler(HttpStatusCode.OK, "{\"id\":\"obj_1\"}")
                .Respond(HttpMethod.Get, "/v1/subscriptions/sub_1", SubscriptionWithScheduleAndInvoiceJson)
                .Respond(HttpMethod.Get, "/v1/subscription_schedules/sub_sched_1", ScheduleJson)
                .Respond(HttpMethod.Post, "/v1/subscription_schedules", "{\"id\":\"sub_sched_new\",\"object\":\"subscription_schedule\"}"),
            PendingWithoutUrlJson,
            snapshotCall: true);

        var act = () => UpgradeNowAsync(NewService(handler));

        await act.Should().ThrowAsync<BillingProviderException>();
        var posts = handler.Requests.Where(r => r.Method == HttpMethod.Post).ToList();
        posts.Should().Contain(r => r.Path == "/v1/subscription_schedules/sub_sched_new");
        posts.Last().Path.Should().Be("/v1/subscriptions/sub_1");
        posts.Last().Form.Should().Contain(new KeyValuePair<string, string>("metadata[formup_restore_prices]", string.Empty));
    }

    [Fact]
    public async Task UpgradePlanNow_WhenAnUpdateForTheSameTargetIsPending_ReturnsItsInvoiceUrl_WithoutAnyStripeWrite()
    {
        var handler = new ScriptedStripeHandler(HttpStatusCode.OK, SubscriptionWithPendingUpdateJson("price_new"));

        var outcome = await UpgradeNowAsync(NewService(handler));

        outcome.PaymentUrl.Should().Be("https://invoice.stripe.test/i/in_old");
        handler.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get, "a second update or charge must not happen");
    }

    [Fact]
    public async Task UpgradePlanNow_WhenAnUpdateForAnotherTargetIsPending_VoidsItsOpenInvoiceBeforeReplacingIt()
    {
        var handler = new ScriptedStripeHandler(HttpStatusCode.OK, SubscriptionWithPendingUpdateJson("price_other"))
            .Respond(HttpMethod.Post, "/v1/invoices/in_old/void", "{\"id\":\"in_old\",\"object\":\"invoice\",\"status\":\"void\"}");

        var outcome = await UpgradeNowAsync(NewService(handler));

        outcome.IsAwaitingPayment.Should().BeFalse("the card needed no authentication this time");
        handler.Requests.Where(r => r.Method == HttpMethod.Post).Select(r => r.Path)
            .Should().Equal("/v1/invoices/in_old/void", "/v1/subscriptions/sub_1");
    }

    private const string PricesListJson = """
        {"object":"list","has_more":false,"data":[{"id":"price_addon","object":"price","lookup_key":"formup-addon-pro"}]}
        """;

    private const string SubscriptionWithPendingAddOnJson = """
        {"id":"sub_1","object":"subscription","schedule":null,"discounts":[],
         "pending_update":{"expires_at":1700050000,"subscription_items":[
           {"id":"si_plan","object":"subscription_item","price":{"id":"price_old","object":"price"}},
           {"id":"si_addon","object":"subscription_item","price":{"id":"price_addon","object":"price","lookup_key":"formup-addon-pro-czk-9900-month-prod_1"}}]},
         "latest_invoice":{"id":"in_addon","object":"invoice","status":"open","hosted_invoice_url":"https://invoice.stripe.test/i/in_addon"},
         "items":{"object":"list","has_more":false,"data":[
           {"id":"si_plan","object":"subscription_item","current_period_start":1700000000,"current_period_end":1702592000,
            "price":{"id":"price_old","object":"price","product":"prod_1","recurring":{"interval":"month"}}}]}}
        """;

    private const string AddOnPendingSubscriptionJson = """
        {"id":"sub_1","object":"subscription","schedule":null,"discounts":[],
         "latest_invoice":{"id":"in_addon","object":"invoice","status":"open","hosted_invoice_url":"https://invoice.stripe.test/i/in_addon"},
         "items":{"object":"list","has_more":false,"data":[
           {"id":"si_plan","object":"subscription_item","current_period_start":1700000000,"current_period_end":1702592000,
            "price":{"id":"price_old","object":"price","product":"prod_1","recurring":{"interval":"month"}}}]}}
        """;

    private static Task<AddOnChargeResult> ChargeAddOnAsync(StripeSubscriptionService service) =>
        service.ChargeSecondRoleAddOnAsync(
            SubscriptionId, "pro", 9900, "CZK", BillingInterval.Monthly, "formup-second-role-sub_1-attempt", TestContext.Current.CancellationToken);

    private static ScriptedStripeHandler AddOnScript(ScriptedStripeHandler handler) =>
        handler.Respond(HttpMethod.Get, "/v1/prices", PricesListJson);

    [Fact]
    public async Task ChargeSecondRoleAddOn_WhenTheCardNeedsNoAuthentication_ChargesAtOnce_WithoutAPendingUpdate()
    {
        var handler = AddOnScript(new ScriptedStripeHandler(HttpStatusCode.OK, SubscriptionJson));

        var result = await ChargeAddOnAsync(NewService(handler));

        result.Should().Be(AddOnChargeResult.Charged);
        var update = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        update.Form.Should().Contain(new KeyValuePair<string, string>("payment_behavior", "error_if_incomplete"));
        update.IdempotencyKey.Should().Be("formup-second-role-sub_1-attempt");
    }

    [Fact]
    public async Task ChargeSecondRoleAddOn_WhenTheCardNeedsAuthentication_RepeatsAsAPendingUpdate_AndReturnsTheHostedInvoiceUrl()
    {
        var handler = AddOnScript(new ScriptedStripeHandler(HttpStatusCode.OK, SubscriptionJson))
            .Sequence(
                HttpMethod.Post,
                "/v1/subscriptions/sub_1",
                (HttpStatusCode.PaymentRequired, AuthenticationRequiredJson),
                (HttpStatusCode.OK, AddOnPendingSubscriptionJson));

        var result = await ChargeAddOnAsync(NewService(handler));

        result.Outcome.Should().Be(AddOnChargeOutcome.AwaitingPayment);
        result.PaymentUrl.Should().Be("https://invoice.stripe.test/i/in_addon");
        var updates = handler.Requests.Where(r => r.Method == HttpMethod.Post).ToList();
        updates.Should().HaveCount(2, "no schedule is released and no snapshot is written");
        updates.Should().OnlyContain(r => r.Path == "/v1/subscriptions/sub_1");
        var pending = updates[1];
        pending.Form.Should().Contain(new KeyValuePair<string, string>("payment_behavior", "pending_if_incomplete"));
        pending.Form.Should().Contain(new KeyValuePair<string, string>("proration_behavior", "always_invoice"));
        pending.Form.Should().Contain(new KeyValuePair<string, string>("items[0][price]", "price_addon"));
        pending.Form.Should().Contain(new KeyValuePair<string, string>("expand[0]", "latest_invoice"));
        pending.IdempotencyKey.Should().Be("formup-second-role-sub_1-attempt-pending");
    }

    [Fact]
    public async Task ChargeSecondRoleAddOn_WithADeclinedCard_ReturnsDeclined_AndNeverGoesPending()
    {
        var handler = AddOnScript(new ScriptedStripeHandler(HttpStatusCode.OK, SubscriptionJson))
            .Fail(HttpMethod.Post, "/v1/subscriptions/sub_1", HttpStatusCode.PaymentRequired, CardDeclinedJson);

        var result = await ChargeAddOnAsync(NewService(handler));

        result.Should().Be(AddOnChargeResult.Declined);
        handler.Requests.Should().NotContain(r => r.Form.Contains(new KeyValuePair<string, string>("payment_behavior", "pending_if_incomplete")));
    }

    [Fact]
    public async Task ChargeSecondRoleAddOn_WhenThePendingUpdateHasNoPaymentPage_Throws()
    {
        var handler = AddOnScript(new ScriptedStripeHandler(HttpStatusCode.OK, SubscriptionJson))
            .Sequence(
                HttpMethod.Post,
                "/v1/subscriptions/sub_1",
                (HttpStatusCode.PaymentRequired, AuthenticationRequiredJson),
                (HttpStatusCode.OK, PendingWithoutUrlJson));

        var act = () => ChargeAddOnAsync(NewService(handler));

        await act.Should().ThrowAsync<BillingProviderException>();
    }

    [Fact]
    public async Task ChargeSecondRoleAddOn_WhenAnAddOnUpdateIsAlreadyPending_ReturnsItsInvoiceUrl_WithoutAnyStripeWrite()
    {
        var handler = new ScriptedStripeHandler(HttpStatusCode.OK, SubscriptionWithPendingAddOnJson);

        var result = await ChargeAddOnAsync(NewService(handler));

        result.Outcome.Should().Be(AddOnChargeOutcome.AwaitingPayment);
        result.PaymentUrl.Should().Be("https://invoice.stripe.test/i/in_addon");
        handler.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get, "a second update or invoice must not be created");
    }

    [Fact]
    public async Task ChargeSecondRoleAddOn_WhenAnUpgradeIsPending_ReturnsPlanChangePending_AndNeverVoidsItsInvoice()
    {
        var handler = new ScriptedStripeHandler(HttpStatusCode.OK, SubscriptionWithPendingUpdateJson("price_new"));

        var result = await ChargeAddOnAsync(NewService(handler));

        result.Should().Be(AddOnChargeResult.PlanChangePending);
        handler.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get, "the upgrade and its invoice stay as they are");
    }

    private const string SnapshotSubscriptionTemplate = """
        {"id":"sub_1","object":"subscription","schedule":SCHEDULE,"discounts":[],
         "metadata":{"formup_restore_prices":"price_cheaper"},
         PENDING
         "items":{"object":"list","has_more":false,"data":[
           {"id":"si_plan","object":"subscription_item","current_period_start":1700000000,"current_period_end":1702592000,
            "price":{"id":"price_old","object":"price","product":"prod_1","recurring":{"interval":"month"}}}]}}
        """;

    private static string SnapshotSubscriptionJson(bool pendingUpdate, bool schedule) =>
        SnapshotSubscriptionTemplate
            .Replace("SCHEDULE", schedule ? "\"sub_sched_1\"" : "null", StringComparison.Ordinal)
            .Replace("PENDING", pendingUpdate ? "\"pending_update\":{\"expires_at\":1700050000}," : string.Empty, StringComparison.Ordinal);

    [Fact]
    public async Task RestoreAfterExpiredUpgrade_WithAScheduledChangeKept_RebuildsTheScheduleFromTheSnapshot_ThenClearsIt()
    {
        var handler = new ScriptedStripeHandler(HttpStatusCode.OK, "{\"id\":\"obj_1\"}")
            .Respond(HttpMethod.Get, "/v1/subscriptions/sub_1", SnapshotSubscriptionJson(false, false))
            .Respond(HttpMethod.Post, "/v1/subscription_schedules", "{\"id\":\"sub_sched_new\",\"object\":\"subscription_schedule\"}");

        await NewService(handler).RestoreAfterExpiredUpgradeAsync(SubscriptionId, true, TestContext.Current.CancellationToken);

        var posts = handler.Requests.Where(r => r.Method == HttpMethod.Post).ToList();
        posts.Select(r => r.Path).Should().Equal("/v1/subscription_schedules", "/v1/subscription_schedules/sub_sched_new", "/v1/subscriptions/sub_1");
        posts[1].Form.Should().Contain(new KeyValuePair<string, string>("phases[0][items][0][price]", "price_old"));
        posts[1].Form.Should().Contain(new KeyValuePair<string, string>("phases[1][items][0][price]", "price_cheaper"));
        posts[2].Form.Should().Contain(new KeyValuePair<string, string>("metadata[formup_restore_prices]", string.Empty));
    }

    [Fact]
    public async Task RestoreAfterExpiredUpgrade_WhenTheRowNoLongerHasAScheduledChange_OnlyClearsTheSnapshot()
    {
        var handler = new ScriptedStripeHandler(HttpStatusCode.OK, "{\"id\":\"obj_1\"}")
            .Respond(HttpMethod.Get, "/v1/subscriptions/sub_1", SnapshotSubscriptionJson(false, false));

        await NewService(handler).RestoreAfterExpiredUpgradeAsync(SubscriptionId, false, TestContext.Current.CancellationToken);

        handler.Requests.Where(r => r.Method == HttpMethod.Post).Select(r => r.Path).Should().Equal("/v1/subscriptions/sub_1");
    }

    [Fact]
    public async Task RestoreAfterExpiredUpgrade_WhileANewerPendingUpdateExists_ChangesNothing()
    {
        var handler = new ScriptedStripeHandler(HttpStatusCode.OK, "{\"id\":\"obj_1\"}")
            .Respond(HttpMethod.Get, "/v1/subscriptions/sub_1", SnapshotSubscriptionJson(true, false));

        await NewService(handler).RestoreAfterExpiredUpgradeAsync(SubscriptionId, true, TestContext.Current.CancellationToken);

        handler.Requests.Should().OnlyContain(r => r.Method == HttpMethod.Get);
    }

    [Fact]
    public async Task RestoreAfterExpiredUpgrade_WhenStripeFails_ThrowsSoTheWebhookIsRetried()
    {
        var handler = new ScriptedStripeHandler(HttpStatusCode.OK, "{\"id\":\"obj_1\"}")
            .Respond(HttpMethod.Get, "/v1/subscriptions/sub_1", SnapshotSubscriptionJson(false, false))
            .Fail(HttpMethod.Post, "/v1/subscription_schedules", HttpStatusCode.InternalServerError, "{\"error\":{\"type\":\"api_error\"}}");

        var act = () => NewService(handler).RestoreAfterExpiredUpgradeAsync(SubscriptionId, true, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<BillingProviderException>();
        handler.Requests.Should().NotContain(r => r.Method == HttpMethod.Post && r.Path == "/v1/subscriptions/sub_1", "the snapshot stays for the retry");
    }
}
