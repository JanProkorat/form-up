using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Services.Billing;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Billing;

/// <summary>
/// The coach events the Stripe webhook writes: one row per real change, none for a repeated delivery, and a plan change
/// recorded once even when the change-plan endpoint already moved the row. Payloads are signed locally.
/// </summary>
[Collection(TestCollection.Name)]
public class StripeWebhookCoachEventsTests(FitnessApiFactory factory)
{
    private const string Route = "/billing/stripe/webhook";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewEventId() => $"evt_{Guid.NewGuid():N}";

    private static string Sign(string payload)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var hash = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(FitnessApiFactory.TestWebhookSecret), Encoding.UTF8.GetBytes($"{timestamp}.{payload}"));

        return $"t={timestamp},v1={Convert.ToHexStringLower(hash)}";
    }

    private static string Envelope(string eventId, string type, object payload) =>
        JsonSerializer.Serialize(
            new
            {
                Id = eventId,
                Object = "event",
                ApiVersion = "2020-01-01",
                Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Livemode = false,
                PendingWebhooks = 1,
                Type = type,
                Data = new { Object = payload },
            },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });

    private static string SubscriptionEvent(
        string eventId, string type, string subscriptionId, string priceId, string status, string customerId = "cus_unused")
    {
        var periodEnd = DateTimeOffset.UtcNow.AddDays(20).ToUnixTimeSeconds();
        var periodStart = DateTimeOffset.UtcNow.AddDays(-10).ToUnixTimeSeconds();

        return Envelope(eventId, type, new
        {
            Id = subscriptionId,
            Object = "subscription",
            Customer = customerId,
            Status = status,
            Created = periodStart,
            CancelAtPeriodEnd = false,
            Metadata = new Dictionary<string, string>(),
            Items = new
            {
                Object = "list",
                Data = new[]
                {
                    new
                    {
                        Id = "si_0",
                        Object = "subscription_item",
                        Quantity = 1,
                        CurrentPeriodStart = periodStart,
                        CurrentPeriodEnd = periodEnd,
                        Price = new { Id = priceId, Object = "price" },
                    },
                },
                HasMore = false,
                Url = "/v1/subscription_items",
            },
            Discounts = Array.Empty<string>(),
        });
    }

    private static string FailedInvoiceEvent(string eventId, string subscriptionId) =>
        Envelope(eventId, "invoice.payment_failed", new
        {
            Id = $"in_{Guid.NewGuid():N}",
            Object = "invoice",
            Customer = "cus_unused",
            Parent = new { Type = "subscription_details", SubscriptionDetails = new { Subscription = subscriptionId } },
        });

    private async Task PostAsync(string payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Stripe-Signature", Sign(payload));

        var response = await factory.CreateClient().SendAsync(request, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task<List<CoachEvent>> EventsOfAsync(Actor coach)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.CoachEvents.AsNoTracking().Where(e => e.CoachUserId == coach.UserId).OrderBy(e => e.Id).ToListAsync(Ct);
    }

    /// <summary>A coach live on a paid plan priced in Stripe; returns the coach, plan code, Stripe price id and subscription id.</summary>
    private async Task<(Actor Coach, string PlanCode, string PriceId, string SubscriptionId)> ArrangeLiveAsync(long price)
    {
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (code, priceId) = await BillingTestData.StripePlanAsync(factory, price);
        var subscriptionId = await BillingTestData.SubscribeLiveAsync(factory, coach, code);

        return (coach, code, priceId, subscriptionId);
    }

    [Fact]
    public async Task Webhook_FirstPaidSubscription_WritesOneStartedPaidPlan_AndARepeatWritesNone()
    {
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (code, priceId) = await BillingTestData.StripePlanAsync(factory, 29900);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, "free");
        var subscriptionId = $"sub_{Guid.NewGuid():N}";
        await BillingTestData.UpdateRowAsync(factory, coach.ProfileId, row => row.ExternalCustomerId = "cus_unused");
        var payload = SubscriptionEvent(NewEventId(), "customer.subscription.created", subscriptionId, priceId, "active");

        await PostAsync(payload);
        await PostAsync(payload);

        var events = await EventsOfAsync(coach);
        var started = events.Should().ContainSingle().Subject;
        started.EventType.Should().Be(CoachEventType.StartedPaidPlan);
        started.PlanCode.Should().Be(code);
        started.FullMonthlyRevenueEffectMinorUnits.Should().Be(29900);
        started.DiscountedMonthlyRevenueEffectMinorUnits.Should().Be(29900);
    }

    [Fact]
    public async Task Webhook_MovingToADearerPlan_WritesOneUpgraded_AndALaterUnchangedEventWritesNone()
    {
        var (coach, oldCode, _, subscriptionId) = await ArrangeLiveAsync(29900);
        var (newCode, newPriceId) = await BillingTestData.StripePlanAsync(factory, 49900);

        await PostAsync(SubscriptionEvent(NewEventId(), "customer.subscription.updated", subscriptionId, newPriceId, "active"));
        await PostAsync(SubscriptionEvent(NewEventId(), "customer.subscription.updated", subscriptionId, newPriceId, "active"));

        var upgraded = (await EventsOfAsync(coach)).Should().ContainSingle().Subject;
        upgraded.EventType.Should().Be(CoachEventType.Upgraded);
        upgraded.PreviousPlanCode.Should().Be(oldCode);
        upgraded.PlanCode.Should().Be(newCode);
        upgraded.FullMonthlyRevenueEffectMinorUnits.Should().Be(20000);
    }

    [Fact]
    public async Task Webhook_AfterChangePlanAlreadyUpgraded_AddsNoSecondUpgradedRow()
    {
        BillingTestData.ResetProvider(factory);
        var (coach, _, _, subscriptionId) = await ArrangeLiveAsync(29900);
        var (newCode, newPriceId) = await BillingTestData.StripePlanAsync(factory, 59900);

        var response = await coach.Http.PostAsJsonAsync(
            "/billing/change-plan", new { PlanCode = newCode, AttemptId = Guid.NewGuid() }, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await PostAsync(SubscriptionEvent(NewEventId(), "customer.subscription.updated", subscriptionId, newPriceId, "active"));

        (await EventsOfAsync(coach)).Should().ContainSingle(e => e.EventType == CoachEventType.Upgraded);
        BillingTestData.ResetProvider(factory);
    }

    [Fact]
    public async Task ChangePlan_WebhookForTheSameUpgradeArrivesDuringTheStripeCall_WritesOneUpgraded()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var (coach, _, _, subscriptionId) = await ArrangeLiveAsync(29900);
        var (newCode, newPriceId) = await BillingTestData.StripePlanAsync(factory, 59900);
        fake.UpgradeNowHook = _ => PostAsync(
            SubscriptionEvent(NewEventId(), "customer.subscription.updated", subscriptionId, newPriceId, "active"));

        try
        {
            var response = await coach.Http.PostAsJsonAsync(
                "/billing/change-plan", new { PlanCode = newCode, AttemptId = Guid.NewGuid() }, Ct);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            BillingTestData.ResetProvider(factory);
        }

        (await BillingTestData.ReadRowAsync(factory, coach.ProfileId)).SubscriptionPlan.Code.Should().Be(newCode);
        (await EventsOfAsync(coach)).Should().ContainSingle(e => e.EventType == CoachEventType.Upgraded);
    }

    [Fact]
    public async Task ChangePlan_SameUpgradeKeyCommittedByAnotherContextBeforeTheSave_StillReturns200_WithOneUpgraded()
    {
        BillingTestData.ResetProvider(factory);
        var (coach, _, oldPriceId, _) = await ArrangeLiveAsync(29900);
        var (newCode, newPriceId) = await BillingTestData.StripePlanAsync(factory, 59900);
        var before = await BillingTestData.ReadRowAsync(factory, coach.ProfileId);
        var hooks = factory.Services.GetRequiredService<CoachEventRecorderHooks>();

        hooks.AfterUpgrade = async (_, ct) =>
        {
            using var racer = factory.Services.CreateScope();
            var racerDb = racer.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var oldRowId = await racerDb.SubscriptionPlanPrices.Where(p => p.ExternalPriceId == oldPriceId).Select(p => p.Id).SingleAsync(ct);
            var newRowId = await racerDb.SubscriptionPlanPrices.Where(p => p.ExternalPriceId == newPriceId).Select(p => p.Id).SingleAsync(ct);
            racerDb.CoachEvents.Add(new CoachEvent
            {
                CoachUserId = coach.UserId,
                EventType = CoachEventType.Upgraded,
                OccurredAt = DateTimeOffset.UtcNow,
                IdempotencyKey = $"upgrade:{coach.ProfileId}:{oldRowId}:{newRowId}:{before.CurrentPeriodEndsAt!.Value.ToUnixTimeSeconds()}",
            });
            await racerDb.SaveChangesAsync(ct);
        };

        try
        {
            var response = await coach.Http.PostAsJsonAsync(
                "/billing/change-plan", new { PlanCode = newCode, AttemptId = Guid.NewGuid() }, Ct);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            hooks.AfterUpgrade = null;
            BillingTestData.ResetProvider(factory);
        }

        (await BillingTestData.ReadRowAsync(factory, coach.ProfileId)).SubscriptionPlan.Code.Should().Be(newCode);
        (await EventsOfAsync(coach)).Should().ContainSingle(e => e.EventType == CoachEventType.Upgraded);
    }

    [Fact]
    public async Task ChangePlan_UpgradeAgainToTheSamePlanAfterARenewalDowngrade_WritesASecondUpgraded()
    {
        BillingTestData.ResetProvider(factory);
        var (coach, oldCode, oldPriceId, subscriptionId) = await ArrangeLiveAsync(29900);
        var (newCode, _) = await BillingTestData.StripePlanAsync(factory, 59900);

        try
        {
            (await coach.Http.PostAsJsonAsync("/billing/change-plan", new { PlanCode = newCode, AttemptId = Guid.NewGuid() }, Ct))
                .StatusCode.Should().Be(HttpStatusCode.OK);
            await BillingTestData.SetPendingPlanAsync(factory, coach.ProfileId, oldCode);
            await PostAsync(SubscriptionEvent(NewEventId(), "customer.subscription.updated", subscriptionId, oldPriceId, "active"));
            (await coach.Http.PostAsJsonAsync("/billing/change-plan", new { PlanCode = newCode, AttemptId = Guid.NewGuid() }, Ct))
                .StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            BillingTestData.ResetProvider(factory);
        }

        (await EventsOfAsync(coach)).Select(e => e.EventType).Should().Equal(
            CoachEventType.Upgraded, CoachEventType.Downgraded, CoachEventType.Upgraded);
    }

    [Fact]
    public async Task ChangePlan_WhenTheCardNeedsAuthentication_WritesNoEvent_AndTheLaterWebhookWritesOneUpgraded()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var (coach, _, _, subscriptionId) = await ArrangeLiveAsync(29900);
        var (newCode, newPriceId) = await BillingTestData.StripePlanAsync(factory, 59900);
        fake.UpgradeOutcome = PlanUpgradeOutcome.AwaitingPayment("https://invoice.stripe.test/i/in_1");

        try
        {
            (await coach.Http.PostAsJsonAsync("/billing/change-plan", new { PlanCode = newCode, AttemptId = Guid.NewGuid() }, Ct))
                .StatusCode.Should().Be(HttpStatusCode.OK);
            (await EventsOfAsync(coach)).Should().BeEmpty();

            await PostAsync(SubscriptionEvent(NewEventId(), "customer.subscription.updated", subscriptionId, newPriceId, "active"));
        }
        finally
        {
            BillingTestData.ResetProvider(factory);
        }

        (await EventsOfAsync(coach)).Should().ContainSingle(e => e.EventType == CoachEventType.Upgraded);
    }

    [Fact]
    public async Task Webhook_ScheduledSwitchToACheaperPlan_AtRenewal_WritesOneDowngraded()
    {
        var (coach, expensiveCode, _, subscriptionId) = await ArrangeLiveAsync(49900);
        var (cheapCode, cheapPriceId) = await BillingTestData.StripePlanAsync(factory, 29900);
        await BillingTestData.SetPendingPlanAsync(factory, coach.ProfileId, cheapCode);

        await PostAsync(SubscriptionEvent(NewEventId(), "customer.subscription.updated", subscriptionId, cheapPriceId, "active"));

        var downgraded = (await EventsOfAsync(coach)).Should().ContainSingle().Subject;
        downgraded.EventType.Should().Be(CoachEventType.Downgraded);
        downgraded.PreviousPlanCode.Should().Be(expensiveCode);
        downgraded.PlanCode.Should().Be(cheapCode);
        downgraded.FullMonthlyRevenueEffectMinorUnits.Should().Be(-20000);
    }

    [Fact]
    public async Task Webhook_SubscriptionDeleted_WritesOneCancelled_WithTheLostRevenueAsNegative()
    {
        var (coach, code, priceId, subscriptionId) = await ArrangeLiveAsync(29900);

        await PostAsync(SubscriptionEvent(NewEventId(), "customer.subscription.deleted", subscriptionId, priceId, "canceled"));

        var cancelled = (await EventsOfAsync(coach)).Should().ContainSingle().Subject;
        cancelled.EventType.Should().Be(CoachEventType.Cancelled);
        cancelled.PreviousPlanCode.Should().Be(code);
        cancelled.PlanCode.Should().BeNull();
        cancelled.FullMonthlyRevenueEffectMinorUnits.Should().Be(-29900);
        cancelled.DiscountedMonthlyRevenueEffectMinorUnits.Should().Be(-29900);
    }

    [Fact]
    public async Task Webhook_CoachEventCollidingWithAConcurrentlyStoredKey_StillCommitsTheSubscriptionChange_AndTheProcessedEvent()
    {
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var (_, priceId) = await BillingTestData.StripePlanAsync(factory, 29900);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, "free");
        // The webhook finds the coach's row by customer id, so it must be unique to this test.
        var customerId = $"cus_{Guid.NewGuid():N}";
        await BillingTestData.UpdateRowAsync(factory, coach.ProfileId, row => row.ExternalCustomerId = customerId);
        var subscriptionId = $"sub_{Guid.NewGuid():N}";
        var eventId = NewEventId();
        var payload = SubscriptionEvent(eventId, "customer.subscription.created", subscriptionId, priceId, "active", customerId);
        var hooks = factory.Services.GetRequiredService<CoachEventRecorderHooks>();

        // A concurrent request stores the same key right after this webhook added its event and passed the key check.
        hooks.AfterSubscriptionChange = async (stripeEventId, _, ct) =>
        {
            using var racer = factory.Services.CreateScope();
            var racerDb = racer.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            racerDb.CoachEvents.Add(new CoachEvent
            {
                CoachUserId = coach.UserId,
                EventType = CoachEventType.StartedPaidPlan,
                OccurredAt = DateTimeOffset.UtcNow,
                IdempotencyKey = $"{stripeEventId}:{CoachEventType.StartedPaidPlan}",
            });
            await racerDb.SaveChangesAsync(ct);
        };

        try
        {
            await PostAsync(payload);
        }
        finally
        {
            hooks.AfterSubscriptionChange = null;
        }

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = await db.CoachSubscriptions.AsNoTracking().FirstAsync(cs => cs.ProfessionalProfileId == coach.ProfileId, Ct);
        row.ExternalSubscriptionId.Should().Be(subscriptionId, "the duplicate coach event must not cost the webhook its subscription change");
        (await db.ProcessedWebhookEvents.CountAsync(e => e.EventId == eventId, Ct)).Should().Be(1);
        (await EventsOfAsync(coach)).Should().ContainSingle(e => e.EventType == CoachEventType.StartedPaidPlan);
    }

    [Fact]
    public async Task Webhook_PaymentFailed_WritesOnePaymentFailed()
    {
        var (coach, code, _, subscriptionId) = await ArrangeLiveAsync(29900);

        await PostAsync(FailedInvoiceEvent(NewEventId(), subscriptionId));

        var failed = (await EventsOfAsync(coach)).Should().ContainSingle().Subject;
        failed.EventType.Should().Be(CoachEventType.PaymentFailed);
        failed.PlanCode.Should().Be(code);
    }
}
