using System.Net;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using FormUp.Tests.Builders;
using FormUp.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FormUp.Tests.Endpoints.Billing;

/// <summary>
/// Integration tests for <c>POST /billing/stripe/webhook</c> (<c>StripeWebhookEndpoint</c>). Payloads are signed locally with
/// the test signing secret, so nothing here touches the network.
/// </summary>
[Collection(TestCollection.Name)]
public class StripeWebhookEndpointTests(FitnessApiFactory factory)
{
    private const string Route = "/billing/stripe/webhook";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Scenario(Actor Coach, string PlanCode, string PriceId, string CustomerId, string SubscriptionId);

    private static string Sign(string payload, string secret = FitnessApiFactory.TestWebhookSecret)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{payload}"));

        return $"t={timestamp},v1={Convert.ToHexStringLower(hash)}";
    }

    private static string SubscriptionEvent(
        string eventId,
        string type,
        Scenario scenario,
        string status,
        long? created = null,
        IEnumerable<string>? extraPriceIds = null,
        bool cancelAtPeriodEnd = false,
        string? userId = null)
    {
        var periodEnd = DateTimeOffset.UtcNow.AddDays(20).ToUnixTimeSeconds();
        var periodStart = DateTimeOffset.UtcNow.AddDays(-10).ToUnixTimeSeconds();
        var items = new[] { scenario.PriceId }.Concat(extraPriceIds ?? []).Select((priceId, index) => new
        {
            Id = $"si_{index}",
            Object = "subscription_item",
            Quantity = 1,
            CurrentPeriodStart = periodStart,
            CurrentPeriodEnd = periodEnd,
            Price = new { Id = priceId, Object = "price" },
        }).ToList();

        return Envelope(eventId, type, created, new
        {
            Id = scenario.SubscriptionId,
            Object = "subscription",
            Customer = scenario.CustomerId,
            Status = status,
            CancelAtPeriodEnd = cancelAtPeriodEnd,
            Metadata = userId is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["userId"] = userId },
            Items = new { Object = "list", Data = items, HasMore = false, Url = "/v1/subscription_items" },
        });
    }

    private static string InvoiceEvent(string eventId, string type, Scenario scenario, long? created = null) =>
        Envelope(eventId, type, created, new
        {
            Id = $"in_{Guid.NewGuid():N}",
            Object = "invoice",
            Customer = scenario.CustomerId,
            Parent = new
            {
                Type = "subscription_details",
                SubscriptionDetails = new { Subscription = scenario.SubscriptionId },
            },
        });

    private static string Envelope(string eventId, string type, long? created, object payload) =>
        System.Text.Json.JsonSerializer.Serialize(
            new
            {
                Id = eventId,
                Object = "event",
                ApiVersion = "2020-01-01",
                Created = created ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Livemode = false,
                PendingWebhooks = 1,
                Type = type,
                Data = new { Object = payload },
            },
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower });

    private static string NewEventId() => $"evt_{Guid.NewGuid():N}";

    private async Task<HttpResponseMessage> PostAsync(string payload, string? signature = null, HttpClient? client = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Stripe-Signature", signature ?? Sign(payload));

        return await (client ?? factory.CreateClient()).SendAsync(request, Ct);
    }

    /// <summary>A coach on the Free plan who has a Stripe customer (as after checkout) and a paid plan priced in Stripe.</summary>
    private async Task<Scenario> ArrangeOnFreePlanAsync(bool bothRolesIncluded = true)
    {
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(factory, addOnMinorUnits: bothRolesIncluded ? null : 9900, bothRolesIncluded: bothRolesIncluded);
        var priceId = await SetStripePriceAsync(plan);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, "free");

        var scenario = new Scenario(coach, plan, priceId, $"cus_{Guid.NewGuid():N}", $"sub_{Guid.NewGuid():N}");
        await UpdateRowAsync(coach.ProfileId, row => row.ExternalCustomerId = scenario.CustomerId);

        return scenario;
    }

    /// <summary>A coach already paying for a plan priced in Stripe, with the Stripe ids stored.</summary>
    private async Task<Scenario> ArrangeOnPaidPlanAsync(SubscriptionStatus status = SubscriptionStatus.Active)
    {
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(factory);
        var priceId = await SetStripePriceAsync(plan);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, plan, status);

        var scenario = new Scenario(coach, plan, priceId, $"cus_{Guid.NewGuid():N}", $"sub_{Guid.NewGuid():N}");
        await UpdateRowAsync(coach.ProfileId, row =>
        {
            row.ExternalCustomerId = scenario.CustomerId;
            row.ExternalSubscriptionId = scenario.SubscriptionId;
        });

        return scenario;
    }

    private Task<string> SetStripePriceAsync(string planCode) => BillingTestData.SetStripePriceAsync(factory, planCode);

    private async Task UpdateRowAsync(long profileId, Action<CoachSubscription> change)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var row = await db.CoachSubscriptions.SingleAsync(cs => cs.ProfessionalProfileId == profileId, Ct);

        change(row);
        await db.SaveChangesAsync(Ct);
    }

    private async Task<CoachSubscription> ReadRowAsync(long profileId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.CoachSubscriptions.AsNoTracking()
            .Include(cs => cs.SubscriptionPlan)
            .SingleAsync(cs => cs.ProfessionalProfileId == profileId, Ct);
    }

    private async Task<int> ProcessedCountAsync(string eventId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.ProcessedWebhookEvents.CountAsync(e => e.EventId == eventId, Ct);
    }

    private async Task<int?> MaxClientsAsync(long profileId)
    {
        using var scope = factory.Services.CreateScope();

        return (await scope.ServiceProvider.GetRequiredService<EntitlementService>().GetEntitlementsAsync(profileId, Ct)).MaxActiveClients;
    }

    private async Task<int?> FreePlanMaxClientsAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.SubscriptionPlans.AsNoTracking().Where(p => p.Code == "free").Select(p => p.MaxActiveClients).SingleAsync(Ct);
    }

    [Fact]
    public async Task Webhook_WithAWrongSignature_Returns400_AndChangesNothing()
    {
        var scenario = await ArrangeOnFreePlanAsync();
        var eventId = NewEventId();
        var payload = SubscriptionEvent(eventId, "customer.subscription.created", scenario, "active");

        var response = await PostAsync(payload, Sign(payload, secret: "some-other-signing-secret"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadRowAsync(scenario.Coach.ProfileId)).SubscriptionPlan.Code.Should().Be("free");
        (await ProcessedCountAsync(eventId)).Should().Be(0);
    }

    [Fact]
    public async Task Webhook_WithATamperedBody_Returns400()
    {
        var scenario = await ArrangeOnFreePlanAsync();
        var payload = SubscriptionEvent(NewEventId(), "customer.subscription.created", scenario, "active");
        var signature = Sign(payload);

        var response = await PostAsync(payload.Replace("active", "trialing"), signature);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Webhook_WithoutASignatureHeader_Returns400()
    {
        var scenario = await ArrangeOnFreePlanAsync();
        var payload = SubscriptionEvent(NewEventId(), "customer.subscription.created", scenario, "active");
        using var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        var response = await factory.CreateClient().SendAsync(request, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Webhook_SubscriptionCreated_PutsTheCoachOnThePaidPlanAsActive()
    {
        var scenario = await ArrangeOnFreePlanAsync();
        var eventId = NewEventId();

        var response = await PostAsync(SubscriptionEvent(eventId, "customer.subscription.created", scenario, "active"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var row = await ReadRowAsync(scenario.Coach.ProfileId);
        row.SubscriptionPlan.Code.Should().Be(scenario.PlanCode);
        row.Status.Should().Be(SubscriptionStatus.Active);
        row.ExternalSubscriptionId.Should().Be(scenario.SubscriptionId);
        row.CurrentPeriodEndsAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddDays(20), TimeSpan.FromMinutes(1));
        row.GivenFreeAt.Should().BeNull();
        (await ProcessedCountAsync(eventId)).Should().Be(1);
        (await MaxClientsAsync(scenario.Coach.ProfileId)).Should().Be(30, "the paid plan's limit applies");
    }

    [Fact]
    public async Task Webhook_SubscriptionCreatedForAnUnknownCustomer_MatchesTheCoachByTheCheckoutMetadata()
    {
        var scenario = await ArrangeOnFreePlanAsync();
        await UpdateRowAsync(scenario.Coach.ProfileId, row => row.ExternalCustomerId = null);

        var response = await PostAsync(SubscriptionEvent(
            NewEventId(), "customer.subscription.created", scenario, "active", userId: scenario.Coach.UserId.ToString()));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var row = await ReadRowAsync(scenario.Coach.ProfileId);
        row.SubscriptionPlan.Code.Should().Be(scenario.PlanCode);
        row.ExternalCustomerId.Should().Be(scenario.CustomerId);
    }

    [Fact]
    public async Task Webhook_FirstPaymentIncomplete_KeepsThePaidFeaturesOff()
    {
        var scenario = await ArrangeOnFreePlanAsync();

        await PostAsync(SubscriptionEvent(NewEventId(), "customer.subscription.created", scenario, "incomplete"));
        await PostAsync(InvoiceEvent(NewEventId(), "invoice.payment_failed", scenario));

        var row = await ReadRowAsync(scenario.Coach.ProfileId);
        row.Status.Should().Be(SubscriptionStatus.Incomplete, "a failed first payment must not grant the paid plan");
        (await MaxClientsAsync(scenario.Coach.ProfileId)).Should().Be(await FreePlanMaxClientsAsync());
    }

    [Fact]
    public async Task Webhook_SubscriptionWithASecondRoleLine_MarksTheAddOnAsPaid()
    {
        var scenario = await ArrangeOnFreePlanAsync(bothRolesIncluded: false);

        await PostAsync(SubscriptionEvent(
            NewEventId(), "customer.subscription.created", scenario, "active", extraPriceIds: ["price_addon_unknown_to_plans"]));

        var row = await ReadRowAsync(scenario.Coach.ProfileId);
        row.PaysSecondRoleAddOn.Should().BeTrue();
    }

    [Fact]
    public async Task Webhook_SubscriptionUpdatedToThePendingPrice_MirrorsTheSwitchAndClearsThePendingId()
    {
        var scenario = await ArrangeOnPaidPlanAsync();
        string newPriceId;
        long newPriceRowId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var plan = await db.SubscriptionPlans.Include(p => p.Prices).SingleAsync(p => p.Code == scenario.PlanCode, Ct);
            var oldRow = plan.Prices.Single(p => p.ValidTo == null);
            oldRow.ValidTo = DateTime.UtcNow;
            newPriceId = $"price_{Guid.NewGuid():N}";
            var newRow = new SubscriptionPlanPrice
            {
                SubscriptionPlanId = plan.Id,
                PriceMinorUnits = 39900,
                Currency = "CZK",
                ExternalPriceId = newPriceId,
                ValidFrom = DateTime.UtcNow,
            };
            plan.Prices.Add(newRow);
            await db.SaveChangesAsync(Ct);
            newPriceRowId = newRow.Id;

            var row = await db.CoachSubscriptions.SingleAsync(cs => cs.ProfessionalProfileId == scenario.Coach.ProfileId, Ct);
            row.PendingSubscriptionPlanPriceId = newRow.Id;
            await db.SaveChangesAsync(Ct);
        }

        var renewal = scenario with { PriceId = newPriceId };
        var response = await PostAsync(SubscriptionEvent(NewEventId(), "customer.subscription.updated", renewal, "active"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var after = await ReadRowAsync(scenario.Coach.ProfileId);
        after.SubscriptionPlanPriceId.Should().Be(newPriceRowId);
        after.PendingSubscriptionPlanPriceId.Should().BeNull();
    }

    [Fact]
    public async Task Webhook_InvoicePaymentFailed_MakesTheSubscriptionPastDue_AndKeepsThePaidEntitlements()
    {
        var scenario = await ArrangeOnPaidPlanAsync();

        var response = await PostAsync(InvoiceEvent(NewEventId(), "invoice.payment_failed", scenario));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadRowAsync(scenario.Coach.ProfileId)).Status.Should().Be(SubscriptionStatus.PastDue);
        (await MaxClientsAsync(scenario.Coach.ProfileId)).Should().Be(30, "the coach keeps the paid plan while Stripe retries");
    }

    [Fact]
    public async Task Webhook_InvoicePaid_RecoversAPastDueSubscription()
    {
        var scenario = await ArrangeOnPaidPlanAsync(SubscriptionStatus.PastDue);

        await PostAsync(InvoiceEvent(NewEventId(), "invoice.paid", scenario));

        (await ReadRowAsync(scenario.Coach.ProfileId)).Status.Should().Be(SubscriptionStatus.Active);
    }

    [Fact]
    public async Task Webhook_SubscriptionDeleted_FallsBackToFree_WithoutALock()
    {
        var scenario = await ArrangeOnPaidPlanAsync();

        var response = await PostAsync(SubscriptionEvent(NewEventId(), "customer.subscription.deleted", scenario, "canceled"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var row = await ReadRowAsync(scenario.Coach.ProfileId);
        row.Status.Should().Be(SubscriptionStatus.Canceled);
        row.ExternalSubscriptionId.Should().BeNull();
        var maxClients = await MaxClientsAsync(scenario.Coach.ProfileId);
        maxClients.Should().Be(await FreePlanMaxClientsAsync(), "a canceled subscription resolves to the Free plan, not to a lock");
        maxClients.Should().NotBe(0);
    }

    [Fact]
    public async Task Webhook_RepeatedEventId_IsANoOp()
    {
        var scenario = await ArrangeOnPaidPlanAsync();
        var eventId = NewEventId();
        var payload = InvoiceEvent(eventId, "invoice.payment_failed", scenario);

        (await PostAsync(payload)).StatusCode.Should().Be(HttpStatusCode.OK);
        await UpdateRowAsync(scenario.Coach.ProfileId, row => row.Status = SubscriptionStatus.Active);
        var repeat = await PostAsync(payload);

        repeat.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadRowAsync(scenario.Coach.ProfileId)).Status.Should().Be(SubscriptionStatus.Active, "the repeat must not be applied again");
        (await ProcessedCountAsync(eventId)).Should().Be(1);
    }

    [Fact]
    public async Task Webhook_ConcurrentDuplicateDeliveries_AllAnswer200_AndApplyOnce()
    {
        var scenario = await ArrangeOnPaidPlanAsync();
        var eventId = NewEventId();
        var payload = InvoiceEvent(eventId, "invoice.payment_failed", scenario);

        var responses = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => PostAsync(payload)));

        responses.Should().OnlyContain(response => response.StatusCode == HttpStatusCode.OK);
        (await ProcessedCountAsync(eventId)).Should().Be(1);
        (await ReadRowAsync(scenario.Coach.ProfileId)).Status.Should().Be(SubscriptionStatus.PastDue);
    }

    [Fact]
    public async Task Webhook_UnhandledEventType_Returns200_AndChangesNothing()
    {
        var scenario = await ArrangeOnPaidPlanAsync();
        var eventId = NewEventId();
        var payload = InvoiceEvent(eventId, "invoice.finalized", scenario);

        var response = await PostAsync(payload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadRowAsync(scenario.Coach.ProfileId)).Status.Should().Be(SubscriptionStatus.Active);
        (await ProcessedCountAsync(eventId)).Should().Be(0);
    }

    [Fact]
    public async Task Webhook_UnknownCustomer_Returns200_AndChangesNothing()
    {
        var scenario = await ArrangeOnPaidPlanAsync();
        var stranger = scenario with { CustomerId = "cus_unknown", SubscriptionId = "sub_unknown" };
        var eventId = NewEventId();

        var response = await PostAsync(InvoiceEvent(eventId, "invoice.payment_failed", stranger));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadRowAsync(scenario.Coach.ProfileId)).Status.Should().Be(SubscriptionStatus.Active);
        (await ProcessedCountAsync(eventId)).Should().Be(0);
    }

    [Fact]
    public async Task Webhook_UnknownPrice_Returns200_AndChangesNothing()
    {
        var scenario = await ArrangeOnFreePlanAsync();
        var unknownPrice = scenario with { PriceId = "price_nobody_sells" };
        var eventId = NewEventId();

        var response = await PostAsync(SubscriptionEvent(eventId, "customer.subscription.created", unknownPrice, "active"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadRowAsync(scenario.Coach.ProfileId)).SubscriptionPlan.Code.Should().Be("free");
        (await ProcessedCountAsync(eventId)).Should().Be(0);
    }

    [Fact]
    public async Task Webhook_EventOlderThanTheLastAppliedOne_IsSkipped()
    {
        var scenario = await ArrangeOnPaidPlanAsync();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        await PostAsync(SubscriptionEvent(NewEventId(), "customer.subscription.updated", scenario, "active", created: now));
        var staleId = NewEventId();
        var stale = await PostAsync(InvoiceEvent(staleId, "invoice.payment_failed", scenario, created: now - 600));

        stale.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadRowAsync(scenario.Coach.ProfileId)).Status.Should().Be(SubscriptionStatus.Active);
        (await ProcessedCountAsync(staleId)).Should().Be(0);
    }

    [Fact]
    public async Task Webhook_SubscriptionUpdatedForACoachWithAnActiveGift_LeavesPlanPriceAndStatusUnchanged()
    {
        var scenario = await ArrangeOnPaidPlanAsync();
        var other = await PaidPlanBuilder.CreateAsync(factory, priceMinorUnits: 59900);
        var otherPriceId = await SetStripePriceAsync(other);
        await UpdateRowAsync(scenario.Coach.ProfileId, row =>
        {
            row.GivenFreeAt = DateTimeOffset.UtcNow.AddDays(-1);
            row.FreeUntil = DateTimeOffset.UtcNow.AddDays(30);
        });
        var before = await ReadRowAsync(scenario.Coach.ProfileId);

        var response = await PostAsync(SubscriptionEvent(
            NewEventId(), "customer.subscription.updated", scenario with { PriceId = otherPriceId }, "past_due"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var after = await ReadRowAsync(scenario.Coach.ProfileId);
        after.SubscriptionPlanId.Should().Be(before.SubscriptionPlanId);
        after.SubscriptionPlanPriceId.Should().Be(before.SubscriptionPlanPriceId);
        after.Status.Should().Be(SubscriptionStatus.Active);
        after.GivenFreeAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Webhook_WhenProcessingThrows_Returns500_AndStoresNoEventId()
    {
        // An event id longer than the 255-character column makes the single save fail after the change was staged.
        // A second host would replace FastEndpoints' process-wide service provider, so the failure is forced in the data.
        var scenario = await ArrangeOnPaidPlanAsync();
        var eventId = $"evt_{new string('x', 300)}";

        var response = await PostAsync(InvoiceEvent(eventId, "invoice.payment_failed", scenario));

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await ProcessedCountAsync(eventId)).Should().Be(0, "a retry from Stripe must be able to reprocess the event");
        (await ReadRowAsync(scenario.Coach.ProfileId)).Status.Should().Be(SubscriptionStatus.Active, "the staged change rolls back with the event id");
    }
}
