using System.Net;
using FluentAssertions;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Services.Billing;
using Microsoft.Extensions.Logging.Abstractions;
using Stripe;

namespace FormUp.Tests.Services.Billing;

/// <summary>
/// Drives the gift and cancelled-renewal paths of <see cref="StripeSubscriptionService"/> through the real Stripe.net client
/// into a scripted handler: which end behaviour and phase dates are sent. No network.
/// </summary>
public class StripeSubscriptionServiceGiftTests
{
    private const string SubscriptionId = "sub_1";
    private const string ScheduleId = "sub_sched_1";

    private static readonly DateTimeOffset PurchasedAt = new(2026, 11, 17, 20, 26, 38, TimeSpan.Zero);
    private static readonly DateTimeOffset FirstRenewal = PurchasedAt.AddMonths(1);
    private static readonly DateTimeOffset Now = PurchasedAt.AddMinutes(30);

    private static long Unix(DateTimeOffset moment) => moment.ToUnixTimeSeconds();

    private static string SubscriptionJson(bool cancelAtPeriodEnd, string? schedule) => """
        {"id":"sub_1","object":"subscription","schedule":SCHEDULE,"discounts":[],"cancel_at_period_end":CANCEL,
         "items":{"object":"list","has_more":false,"data":[
           {"id":"si_plan","object":"subscription_item","current_period_start":START,"current_period_end":END,
            "price":{"id":"price_old","object":"price","product":"prod_1","recurring":{"interval":"month"}}}]}}
        """
        .Replace("SCHEDULE", schedule is null ? "null" : $"\"{schedule}\"")
        .Replace("CANCEL", cancelAtPeriodEnd ? "true" : "false")
        .Replace("START", Unix(PurchasedAt).ToString())
        .Replace("END", Unix(FirstRenewal).ToString());

    private static string ScheduleJson(string endBehavior, DateTimeOffset phaseEnd, string? coupon) => """
        {"id":"sub_sched_1","object":"subscription_schedule","end_behavior":"BEHAVIOR",
         "current_phase":{"start_date":START,"end_date":PHASEEND},
         "phases":[{"start_date":START,"end_date":PHASEEND,"items":[{"price":"price_old","quantity":1}],"discounts":DISCOUNTS}]}
        """
        .Replace("BEHAVIOR", endBehavior)
        .Replace("START", Unix(PurchasedAt).ToString())
        .Replace("PHASEEND", Unix(phaseEnd).ToString())
        .Replace("DISCOUNTS", coupon is null ? "[]" : $"[{{\"coupon\":\"{coupon}\"}}]");

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
            new FixedTimeProvider(Now),
            NullLogger<StripeSubscriptionService>.Instance,
            client);
    }

    private static ScriptedStripeHandler NewHandler(string subscriptionJson) =>
        new ScriptedStripeHandler(HttpStatusCode.OK, "{\"id\":\"obj_1\"}")
            .Respond(HttpMethod.Get, "/v1/subscriptions/sub_1", subscriptionJson)
            .Respond(HttpMethod.Get, $"/v1/coupons/{StripeRequestBuilder.GiftCouponId}", $"{{\"id\":\"{StripeRequestBuilder.GiftCouponId}\",\"object\":\"coupon\"}}")
            .Respond(HttpMethod.Post, "/v1/subscription_schedules", "{\"id\":\"sub_sched_new\",\"object\":\"subscription_schedule\"}");

    private static KeyValuePair<string, string> Pair(string key, string value) => new(key, value);

    [Fact]
    public async Task ApplyGivenFree_CancelledRenewalWithTheRenewalOnTheLastDay_CancelsAtThatRenewalWithNoPaidPhase()
    {
        var handler = NewHandler(SubscriptionJson(cancelAtPeriodEnd: true, schedule: null));
        var endOfDay = new DateTimeOffset(2026, 12, 17, 22, 59, 59, TimeSpan.Zero);

        await NewService(handler).ApplyGivenFreeAsync(SubscriptionId, endOfDay, TestContext.Current.CancellationToken);

        var update = handler.Requests.Last();
        update.Path.Should().Be("/v1/subscription_schedules/sub_sched_new");
        update.Form.Should().Contain(Pair("end_behavior", "cancel"));
        update.Form.Should().Contain(Pair("phases[0][end_date]", Unix(FirstRenewal).ToString()));
        update.Form.Should().Contain(Pair("phases[0][discounts][0][coupon]", StripeRequestBuilder.GiftCouponId));
        update.Form.Should().NotContain(pair => pair.Key.StartsWith("phases[1]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ApplyGivenFree_RenewalStillOn_EndsTheGiftAtTheRenewalOnTheLastDayAndReleasesAfterOnePaidPhase()
    {
        var handler = NewHandler(SubscriptionJson(cancelAtPeriodEnd: false, schedule: null));
        var endOfDay = new DateTimeOffset(2027, 2, 17, 22, 59, 59, TimeSpan.Zero);

        await NewService(handler).ApplyGivenFreeAsync(SubscriptionId, endOfDay, TestContext.Current.CancellationToken);

        var update = handler.Requests.Last();
        update.Form.Should().Contain(Pair("end_behavior", "release"));
        update.Form.Should().Contain(Pair("phases[0][end_date]", Unix(PurchasedAt.AddMonths(3)).ToString()));
        update.Form.Should().Contain(Pair("phases[1][duration][interval]", "month"));
        update.Form.Should().NotContain(pair => pair.Key.StartsWith("phases[1][discounts][0]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ClearScheduledPriceChange_OnACancellingSchedule_KeepsTheCancellation()
    {
        var giftEnd = PurchasedAt.AddMonths(2).AddDays(9);
        var handler = NewHandler(SubscriptionJson(false, ScheduleId))
            .Respond(HttpMethod.Get, "/v1/subscription_schedules/sub_sched_1", ScheduleJson("cancel", giftEnd, StripeRequestBuilder.GiftCouponId));

        await NewService(handler).ClearScheduledPriceChangeAsync(SubscriptionId, TestContext.Current.CancellationToken);

        var update = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        update.Path.Should().Be("/v1/subscription_schedules/sub_sched_1");
        update.Form.Should().Contain(Pair("end_behavior", "cancel"));
        update.Form.Should().Contain(Pair("phases[0][end_date]", Unix(giftEnd).ToString()));
        update.Form.Should().NotContain(pair => pair.Key.StartsWith("phases[1]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ClearScheduledPriceChange_OnACancellingScheduleWithNothingElseToSchedule_ReleasesAndSetsTheCancelFlag()
    {
        var handler = NewHandler(SubscriptionJson(false, ScheduleId))
            .Respond(HttpMethod.Get, "/v1/subscription_schedules/sub_sched_1", ScheduleJson("cancel", FirstRenewal, null));

        await NewService(handler).ClearScheduledPriceChangeAsync(SubscriptionId, TestContext.Current.CancellationToken);

        handler.Requests.Where(r => r.Method == HttpMethod.Post).Select(r => r.Path)
            .Should().Equal("/v1/subscription_schedules/sub_sched_1/release", "/v1/subscriptions/sub_1");
        handler.Requests.Last().Form.Should().Contain(Pair("cancel_at_period_end", "true"));
    }

    [Fact]
    public async Task ResumeRenewal_OnACancellingGiftSchedule_SwitchesTheScheduleToRelease()
    {
        var giftEnd = PurchasedAt.AddMonths(2).AddDays(9);
        var handler = NewHandler(SubscriptionJson(false, ScheduleId))
            .Respond(HttpMethod.Get, "/v1/subscription_schedules/sub_sched_1", ScheduleJson("cancel", giftEnd, StripeRequestBuilder.GiftCouponId));

        await NewService(handler).ResumeRenewalAsync(SubscriptionId, TestContext.Current.CancellationToken);

        var update = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        update.Path.Should().Be("/v1/subscription_schedules/sub_sched_1");
        update.Form.Should().Contain(Pair("end_behavior", "release"));
        update.Form.Should().Contain(Pair("phases[0][end_date]", Unix(giftEnd).ToString()));
        update.Form.Should().Contain(Pair("phases[1][duration][interval]", "month"));
    }

    [Fact]
    public async Task ResumeRenewal_OnACancellingScheduleWithNothingElseToSchedule_ReleasesIt()
    {
        var handler = NewHandler(SubscriptionJson(false, ScheduleId))
            .Respond(HttpMethod.Get, "/v1/subscription_schedules/sub_sched_1", ScheduleJson("cancel", FirstRenewal, null));

        await NewService(handler).ResumeRenewalAsync(SubscriptionId, TestContext.Current.CancellationToken);

        handler.Requests.Where(r => r.Method == HttpMethod.Post).Select(r => r.Path)
            .Should().Equal("/v1/subscription_schedules/sub_sched_1/release");
    }

    [Fact]
    public async Task ResumeRenewal_WhenStripeRejectsTheScheduleUpdate_Throws()
    {
        var handler = NewHandler(SubscriptionJson(false, ScheduleId))
            .Respond(HttpMethod.Get, "/v1/subscription_schedules/sub_sched_1", ScheduleJson("cancel", PurchasedAt.AddMonths(3), StripeRequestBuilder.GiftCouponId))
            .Fail(HttpMethod.Post, "/v1/subscription_schedules/sub_sched_1", HttpStatusCode.InternalServerError, "{\"error\":{\"type\":\"api_error\"}}");

        var act = () => NewService(handler).ResumeRenewalAsync(SubscriptionId, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<BillingProviderException>();
        handler.Requests.Where(r => r.Method == HttpMethod.Post).Should()
            .OnlyContain(r => r.Path == "/v1/subscription_schedules/sub_sched_1", "the schedule is never released or the subscription touched half-way");
    }

    private static string TwoPhaseScheduleJson(DateTimeOffset firstPhaseEnd, string? coupon, string secondPhasePrice) => """
        {"id":"sub_sched_1","object":"subscription_schedule","end_behavior":"release",
         "current_phase":{"start_date":START,"end_date":FIRSTEND},
         "phases":[
           {"start_date":START,"end_date":FIRSTEND,"items":[{"price":"price_old","quantity":1}],"discounts":DISCOUNTS},
           {"start_date":FIRSTEND,"items":[{"price":"SECONDPRICE","quantity":1}],"discounts":[]}]}
        """
        .Replace("START", Unix(PurchasedAt).ToString())
        .Replace("FIRSTEND", Unix(firstPhaseEnd).ToString())
        .Replace("SECONDPRICE", secondPhasePrice)
        .Replace("DISCOUNTS", coupon is null ? "[]" : $"[{{\"coupon\":\"{coupon}\"}}]");

    [Fact]
    public async Task CancelAtPeriodEnd_OnAGiftSchedule_KeepsTheCouponPhaseAndCancelsWithoutReleasing()
    {
        var giftEnd = PurchasedAt.AddMonths(2).AddDays(9);
        var handler = NewHandler(SubscriptionJson(false, ScheduleId))
            .Respond(HttpMethod.Get, "/v1/subscription_schedules/sub_sched_1", TwoPhaseScheduleJson(giftEnd, StripeRequestBuilder.GiftCouponId, "price_old"));

        await NewService(handler).CancelAtPeriodEndAsync(SubscriptionId, TestContext.Current.CancellationToken);

        var update = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        update.Path.Should().Be("/v1/subscription_schedules/sub_sched_1");
        update.Form.Should().Contain(Pair("end_behavior", "cancel"));
        update.Form.Should().Contain(Pair("phases[0][end_date]", Unix(giftEnd).ToString()));
        update.Form.Should().Contain(Pair("phases[0][discounts][0][coupon]", StripeRequestBuilder.GiftCouponId));
        update.Form.Should().NotContain(pair => pair.Key.StartsWith("phases[1]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancelAtPeriodEnd_WithoutASchedule_SetsOnlyTheCancelFlag()
    {
        var handler = NewHandler(SubscriptionJson(cancelAtPeriodEnd: false, schedule: null));

        await NewService(handler).CancelAtPeriodEndAsync(SubscriptionId, TestContext.Current.CancellationToken);

        var update = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        update.Path.Should().Be("/v1/subscriptions/sub_1");
        update.Form.Should().Contain(Pair("cancel_at_period_end", "true"));
    }

    [Fact]
    public async Task CancelAtPeriodEnd_OnAScheduleHoldingOnlyAPriceChange_CancelsInOnePhaseOnTheCurrentPrices()
    {
        var handler = NewHandler(SubscriptionJson(false, ScheduleId))
            .Respond(HttpMethod.Get, "/v1/subscription_schedules/sub_sched_1", TwoPhaseScheduleJson(FirstRenewal, null, "price_new"));

        await NewService(handler).CancelAtPeriodEndAsync(SubscriptionId, TestContext.Current.CancellationToken);

        var update = handler.Requests.Single(r => r.Method == HttpMethod.Post);
        update.Path.Should().Be("/v1/subscription_schedules/sub_sched_1");
        update.Form.Should().Contain(Pair("end_behavior", "cancel"));
        update.Form.Should().Contain(Pair("phases[0][end_date]", Unix(FirstRenewal).ToString()));
        update.Form.Should().Contain(pair => pair.Key.Contains("[price]", StringComparison.Ordinal) && pair.Value == "price_old");
        update.Form.Should().NotContain(pair => pair.Value == "price_new");
        update.Form.Should().NotContain(pair => pair.Key.StartsWith("phases[1]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancelAtPeriodEnd_WhenStripeRejectsTheScheduleUpdate_ThrowsWithoutReleasing()
    {
        var giftEnd = PurchasedAt.AddMonths(2).AddDays(9);
        var handler = NewHandler(SubscriptionJson(false, ScheduleId))
            .Respond(HttpMethod.Get, "/v1/subscription_schedules/sub_sched_1", TwoPhaseScheduleJson(giftEnd, StripeRequestBuilder.GiftCouponId, "price_old"))
            .Fail(HttpMethod.Post, "/v1/subscription_schedules/sub_sched_1", HttpStatusCode.InternalServerError, "{\"error\":{\"type\":\"api_error\"}}");

        var act = () => NewService(handler).CancelAtPeriodEndAsync(SubscriptionId, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<BillingProviderException>();
        handler.Requests.Where(r => r.Method == HttpMethod.Post).Should()
            .OnlyContain(r => r.Path == "/v1/subscription_schedules/sub_sched_1", "the gift's schedule is never released when the cancel fails");
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
