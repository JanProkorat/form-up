using System.Net;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
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
        string? userId = null,
        string[]? discountIds = null,
        long? subscriptionCreated = null)
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
            Created = subscriptionCreated ?? periodStart,
            CancelAtPeriodEnd = cancelAtPeriodEnd,
            Metadata = userId is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["userId"] = userId },
            Items = new { Object = "list", Data = items, HasMore = false, Url = "/v1/subscription_items" },
            Discounts = discountIds ?? [],
        });
    }

    private static string InvoiceEvent(
        string eventId, string type, Scenario scenario, long? created = null, long? nextPaymentAttempt = null) =>
        Envelope(eventId, type, created, new
        {
            Id = $"in_{Guid.NewGuid():N}",
            Object = "invoice",
            Customer = scenario.CustomerId,
            NextPaymentAttempt = nextPaymentAttempt,
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
    public async Task Webhook_RenewalToAOneRolePlanWithAnAddOnLine_SetsTheAddOnFlag_AndClearsThePendingId()
    {
        var scenario = await ArrangeOnPaidPlanAsync();
        var (oneRolePlan, oneRolePriceId) = await BillingTestData.StripePlanAsync(
            factory, price: 29900, addOn: 9900, bothRolesIncluded: false);
        await BillingTestData.SetPendingPlanAsync(factory, scenario.Coach.ProfileId, oneRolePlan);
        (await ReadRowAsync(scenario.Coach.ProfileId)).PaysSecondRoleAddOn.Should().BeFalse("the both-roles plan has no add-on line");

        var renewal = scenario with { PriceId = oneRolePriceId };
        var response = await PostAsync(SubscriptionEvent(
            NewEventId(), "customer.subscription.updated", renewal, "active", extraPriceIds: ["price_addon_unknown_to_plans"]));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var after = await BillingTestData.ReadRowAsync(factory, scenario.Coach.ProfileId);
        after.SubscriptionPlan.Code.Should().Be(oneRolePlan);
        after.PaysSecondRoleAddOn.Should().BeTrue();
        after.PendingSubscriptionPlanPriceId.Should().BeNull();
    }

    /// <summary>A coach on a one-role plan who pays the add-on and has already removed the second role (its end is scheduled).</summary>
    private async Task<Scenario> ArrangeScheduledAddOnEndAsync()
    {
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(factory, addOnMinorUnits: 9900, bothRolesIncluded: false);
        var priceId = await SetStripePriceAsync(plan);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, plan, paysSecondRoleAddOn: true);

        var scenario = new Scenario(coach, plan, priceId, $"cus_{Guid.NewGuid():N}", $"sub_{Guid.NewGuid():N}");
        await UpdateRowAsync(coach.ProfileId, row =>
        {
            row.ExternalCustomerId = scenario.CustomerId;
            row.ExternalSubscriptionId = scenario.SubscriptionId;
            row.SecondRoleAddOnEndsAt = DateTimeOffset.UtcNow.AddDays(10);
        });

        return scenario;
    }

    [Fact]
    public async Task Webhook_RenewalWithoutTheAddOnLine_StopsBillingTheAddOn_AndClearsTheScheduledEnd()
    {
        var scenario = await ArrangeScheduledAddOnEndAsync();

        var response = await PostAsync(SubscriptionEvent(NewEventId(), "customer.subscription.updated", scenario, "active"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var after = await ReadRowAsync(scenario.Coach.ProfileId);
        after.PaysSecondRoleAddOn.Should().BeFalse();
        after.SecondRoleAddOnEndsAt.Should().BeNull();
    }

    [Fact]
    public async Task Webhook_UpdateRightAfterSchedulingTheEnd_KeepsTheAddOnAndTheScheduledEnd()
    {
        var scenario = await ArrangeScheduledAddOnEndAsync();

        var response = await PostAsync(SubscriptionEvent(
            NewEventId(), "customer.subscription.updated", scenario, "active", extraPriceIds: ["price_addon_unknown_to_plans"]));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var after = await ReadRowAsync(scenario.Coach.ProfileId);
        after.PaysSecondRoleAddOn.Should().BeTrue("the add-on item is still on the subscription until the renewal");
        after.SecondRoleAddOnEndsAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Webhook_RenewalWithoutTheAddOnLineUnderAGift_StillClearsTheScheduledEnd()
    {
        var scenario = await ArrangeScheduledAddOnEndAsync();
        await UpdateRowAsync(scenario.Coach.ProfileId, row =>
        {
            row.GivenFreeAt = DateTimeOffset.UtcNow.AddDays(-1);
            row.FreeUntil = DateTimeOffset.UtcNow.AddDays(30);
        });

        var response = await PostAsync(SubscriptionEvent(NewEventId(), "customer.subscription.updated", scenario, "active"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var after = await ReadRowAsync(scenario.Coach.ProfileId);
        after.PaysSecondRoleAddOn.Should().BeFalse();
        after.SecondRoleAddOnEndsAt.Should().BeNull();
        after.GivenFreeAt.Should().NotBeNull("the gift itself is untouched");
    }

    [Fact]
    public async Task Webhook_SubscriptionDeletedWithAScheduledEnd_ClearsIt()
    {
        var scenario = await ArrangeScheduledAddOnEndAsync();

        var response = await PostAsync(SubscriptionEvent(NewEventId(), "customer.subscription.deleted", scenario, "canceled"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadRowAsync(scenario.Coach.ProfileId)).SecondRoleAddOnEndsAt.Should().BeNull();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Webhook_SubscriptionUpdated_KeepsTheAdminMarkerOnlyWhileTheProviderStillCancels(bool providerCancels, bool markerKept)
    {
        var scenario = await ArrangeOnPaidPlanAsync();
        await UpdateRowAsync(scenario.Coach.ProfileId, row =>
        {
            row.CancelAtPeriodEnd = true;
            row.RenewalCancelledByAdminAt = DateTimeOffset.UtcNow;
        });

        var response = await PostAsync(SubscriptionEvent(
            NewEventId(), "customer.subscription.updated", scenario, "active", cancelAtPeriodEnd: providerCancels));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var row = await ReadRowAsync(scenario.Coach.ProfileId);
        row.CancelAtPeriodEnd.Should().Be(providerCancels);
        (row.RenewalCancelledByAdminAt is not null).Should().Be(markerKept);
    }

    [Fact]
    public async Task Webhook_SubscriptionDeleted_ClearsTheAdminMarker()
    {
        var scenario = await ArrangeOnPaidPlanAsync();
        await UpdateRowAsync(scenario.Coach.ProfileId, row =>
        {
            row.CancelAtPeriodEnd = true;
            row.RenewalCancelledByAdminAt = DateTimeOffset.UtcNow;
        });

        var response = await PostAsync(SubscriptionEvent(NewEventId(), "customer.subscription.deleted", scenario, "canceled"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadRowAsync(scenario.Coach.ProfileId)).RenewalCancelledByAdminAt.Should().BeNull();
    }

    [Fact]
    public async Task Webhook_SubscriptionDeletedDuringAGift_KeepsTheGiftAndClearsThePaidFields()
    {
        var scenario = await ArrangeOnPaidPlanAsync();
        var giftedAt = DateTimeOffset.UtcNow.AddDays(-1);
        var freeUntil = DateTimeOffset.UtcNow.AddDays(30);
        await UpdateRowAsync(scenario.Coach.ProfileId, row =>
        {
            row.GivenFreeAt = giftedAt;
            row.FreeUntil = freeUntil;
            row.CurrentPeriodEndsAt = DateTimeOffset.UtcNow.AddDays(15);
            row.CancelAtPeriodEnd = true;
            row.RenewalCancelledByAdminAt = DateTimeOffset.UtcNow;
            row.SecondRoleAddOnEndsAt = DateTimeOffset.UtcNow.AddDays(10);
            row.PaysSecondRoleAddOn = true;
        });
        var before = await ReadRowAsync(scenario.Coach.ProfileId);

        var response = await PostAsync(SubscriptionEvent(NewEventId(), "customer.subscription.deleted", scenario, "canceled"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var after = await ReadRowAsync(scenario.Coach.ProfileId);
        after.ExternalSubscriptionId.Should().BeNull();
        after.Status.Should().Be(before.Status, "the gift's own status stays");
        after.SubscriptionPlanId.Should().Be(before.SubscriptionPlanId);
        after.SubscriptionPlanPriceId.Should().Be(before.SubscriptionPlanPriceId);
        after.GivenFreeAt.Should().Be(before.GivenFreeAt);
        after.FreeUntil.Should().Be(before.FreeUntil);
        after.CurrentPeriodEndsAt.Should().BeNull("nothing renews once the subscription is gone");
        after.CancelAtPeriodEnd.Should().BeFalse();
        after.RenewalCancelledByAdminAt.Should().BeNull();
        after.PendingSubscriptionPlanPriceId.Should().BeNull();
        after.SecondRoleAddOnEndsAt.Should().BeNull();
        after.PaysSecondRoleAddOn.Should().BeFalse();
        (await MaxClientsAsync(scenario.Coach.ProfileId)).Should().Be(30, "the gifted plan keeps running");
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
    public async Task Webhook_InvoicePaymentFailed_StoresTheNextRetryDate()
    {
        var scenario = await ArrangeOnPaidPlanAsync();
        var retryAt = DateTimeOffset.UtcNow.AddDays(3).ToUnixTimeSeconds();

        await PostAsync(InvoiceEvent(NewEventId(), "invoice.payment_failed", scenario, nextPaymentAttempt: retryAt));

        (await ReadRowAsync(scenario.Coach.ProfileId)).NextPaymentAttemptAt
            .Should().Be(DateTimeOffset.FromUnixTimeSeconds(retryAt));
    }

    [Fact]
    public async Task Webhook_InvoicePaid_ClearsTheNextRetryDate()
    {
        var scenario = await ArrangeOnPaidPlanAsync(SubscriptionStatus.PastDue);
        await UpdateRowAsync(scenario.Coach.ProfileId, row => row.NextPaymentAttemptAt = DateTimeOffset.UtcNow.AddDays(2));

        await PostAsync(InvoiceEvent(NewEventId(), "invoice.paid", scenario));

        (await ReadRowAsync(scenario.Coach.ProfileId)).NextPaymentAttemptAt.Should().BeNull();
    }

    [Fact]
    public async Task Webhook_SubscriptionDeleted_ClearsTheNextRetryDate()
    {
        var scenario = await ArrangeOnPaidPlanAsync(SubscriptionStatus.PastDue);
        await UpdateRowAsync(scenario.Coach.ProfileId, row => row.NextPaymentAttemptAt = DateTimeOffset.UtcNow.AddDays(2));

        await PostAsync(SubscriptionEvent(NewEventId(), "customer.subscription.deleted", scenario, "canceled"));

        (await ReadRowAsync(scenario.Coach.ProfileId)).NextPaymentAttemptAt.Should().BeNull();
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
        var periodEnd = DateTimeOffset.UtcNow.AddDays(12);
        await UpdateRowAsync(scenario.Coach.ProfileId, row => row.CurrentPeriodEndsAt = periodEnd);

        var response = await PostAsync(SubscriptionEvent(NewEventId(), "customer.subscription.deleted", scenario, "canceled"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var row = await ReadRowAsync(scenario.Coach.ProfileId);
        row.Status.Should().Be(SubscriptionStatus.Canceled);
        row.ExternalSubscriptionId.Should().BeNull();
        row.CurrentPeriodEndsAt.Should().BeCloseTo(periodEnd, TimeSpan.FromSeconds(1), "the lapse ended-on date keeps the paid period end");
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

    /// <summary>A coach on a plan set by Form Up (free plan, gift given a day ago, no Stripe subscription yet).</summary>
    private async Task<Scenario> ArrangeOnGiftWithoutSubscriptionAsync()
    {
        var scenario = await ArrangeOnFreePlanAsync();
        await UpdateRowAsync(scenario.Coach.ProfileId, row => row.GivenFreeAt = DateTimeOffset.UtcNow.AddDays(-1));

        return scenario;
    }

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private async Task AssertBoughtPlanReplacedGiftAsync(Scenario scenario)
    {
        var row = await ReadRowAsync(scenario.Coach.ProfileId);

        row.SubscriptionPlan.Code.Should().Be(scenario.PlanCode);
        row.Status.Should().Be(SubscriptionStatus.Active);
        row.ExternalSubscriptionId.Should().Be(scenario.SubscriptionId);
        row.GivenFreeAt.Should().BeNull();
        row.FreeUntil.Should().BeNull();
    }

    [Fact]
    public async Task Webhook_NewSubscriptionForACoachWithAGivenPlan_EndsTheGiftAndAppliesThePurchase()
    {
        var scenario = await ArrangeOnGiftWithoutSubscriptionAsync();

        var response = await PostAsync(SubscriptionEvent(
            NewEventId(), "customer.subscription.created", scenario, "active", created: Now, subscriptionCreated: Now));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertBoughtPlanReplacedGiftAsync(scenario);
    }

    [Fact]
    public async Task Webhook_SubscriptionOlderThanTheGift_KeepsTheGiftedPlan()
    {
        var scenario = await ArrangeOnGiftWithoutSubscriptionAsync();

        var response = await PostAsync(SubscriptionEvent(
            NewEventId(), "customer.subscription.created", scenario, "active", subscriptionCreated: Now - 5 * 86400));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var row = await ReadRowAsync(scenario.Coach.ProfileId);
        row.SubscriptionPlan.Code.Should().Be("free");
        row.GivenFreeAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Webhook_InvoicePaidBeforeSubscriptionCreated_DoesNotMakeTheSubscriptionEventStale()
    {
        var scenario = await ArrangeOnGiftWithoutSubscriptionAsync();
        var invoiceEventId = NewEventId();

        await PostAsync(InvoiceEvent(invoiceEventId, "invoice.paid", scenario, created: Now + 5));
        var response = await PostAsync(SubscriptionEvent(
            NewEventId(), "customer.subscription.created", scenario, "active", created: Now, subscriptionCreated: Now));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ProcessedCountAsync(invoiceEventId)).Should().Be(0, "the invoice names a subscription the coach does not have yet");
        await AssertBoughtPlanReplacedGiftAsync(scenario);
    }

    [Fact]
    public async Task Webhook_InvoicePaidAfterSubscriptionCreated_KeepsThePurchase()
    {
        var scenario = await ArrangeOnGiftWithoutSubscriptionAsync();
        var invoiceEventId = NewEventId();

        await PostAsync(SubscriptionEvent(
            NewEventId(), "customer.subscription.created", scenario, "active", created: Now, subscriptionCreated: Now));
        await PostAsync(InvoiceEvent(invoiceEventId, "invoice.paid", scenario, created: Now + 5));

        (await ProcessedCountAsync(invoiceEventId)).Should().Be(1);
        await AssertBoughtPlanReplacedGiftAsync(scenario);
    }

    [Fact]
    public async Task Webhook_SameSubscriptionCreatedEventTwice_IsAppliedOnce()
    {
        var scenario = await ArrangeOnGiftWithoutSubscriptionAsync();
        var eventId = NewEventId();
        var payload = SubscriptionEvent(eventId, "customer.subscription.created", scenario, "active", created: Now, subscriptionCreated: Now);

        await PostAsync(payload);
        var second = await PostAsync(payload);

        second.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ProcessedCountAsync(eventId)).Should().Be(1);
        await AssertBoughtPlanReplacedGiftAsync(scenario);
    }

    [Fact]
    public async Task Webhook_NewEventForARowBrokenByThePurchase_RepairsIt()
    {
        var scenario = await ArrangeOnGiftWithoutSubscriptionAsync();
        await UpdateRowAsync(scenario.Coach.ProfileId, row => row.ExternalSubscriptionId = scenario.SubscriptionId);

        var response = await PostAsync(SubscriptionEvent(
            NewEventId(), "customer.subscription.updated", scenario, "active", created: Now, subscriptionCreated: Now));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertBoughtPlanReplacedGiftAsync(scenario);
    }

    /// <summary>A paid coach with a scheduled downgrade pending, and a dearer plan priced in Stripe to upgrade to.</summary>
    private async Task<(Scenario Current, Scenario Upgraded, long PendingPriceRowId)> ArrangeUpgradeWithScheduledDowngradeAsync()
    {
        var current = await ArrangeOnPaidPlanAsync();
        var target = await PaidPlanBuilder.CreateAsync(factory);
        var targetPriceId = await SetStripePriceAsync(target);
        var downgrade = await PaidPlanBuilder.CreateAsync(factory);
        await SetStripePriceAsync(downgrade);
        await BillingTestData.SetPendingPlanAsync(factory, current.Coach.ProfileId, downgrade);
        var pending = (await ReadRowAsync(current.Coach.ProfileId)).PendingSubscriptionPlanPriceId!.Value;

        return (current, current with { PlanCode = target, PriceId = targetPriceId }, pending);
    }

    [Fact]
    public async Task Webhook_PendingUpdateApplied_SwitchesThePlan_AndClearsTheScheduledChangeMarker()
    {
        var (current, upgraded, _) = await ArrangeUpgradeWithScheduledDowngradeAsync();

        var response = await PostAsync(SubscriptionEvent(NewEventId(), "customer.subscription.pending_update_applied", upgraded, "active"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var after = await ReadRowAsync(current.Coach.ProfileId);
        after.SubscriptionPlan.Code.Should().Be(upgraded.PlanCode);
        after.PendingSubscriptionPlanPriceId.Should().BeNull("the upgrade replaced the scheduled downgrade");
    }

    [Fact]
    public async Task Webhook_PendingUpdateAppliedAfterTheUpdatedEventOfTheSameChange_StillClearsTheMarker()
    {
        var (current, upgraded, pendingRowId) = await ArrangeUpgradeWithScheduledDowngradeAsync();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await PostAsync(SubscriptionEvent(NewEventId(), "customer.subscription.updated", upgraded, "active", created: now));
        (await ReadRowAsync(current.Coach.ProfileId)).PendingSubscriptionPlanPriceId.Should().Be(pendingRowId, "the updated event does not know the marker");
        var appliedId = NewEventId();

        var response = await PostAsync(SubscriptionEvent(appliedId, "customer.subscription.pending_update_applied", upgraded, "active", created: now - 5));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadRowAsync(current.Coach.ProfileId)).PendingSubscriptionPlanPriceId.Should().BeNull("an older event order must not lose the clear");
        (await ProcessedCountAsync(appliedId)).Should().Be(1);
    }

    [Fact]
    public async Task Webhook_PendingUpdateApplied_ForAnOlderEventWhoseChangeWasNotApplied_ChangesNothing()
    {
        var (current, upgraded, pendingRowId) = await ArrangeUpgradeWithScheduledDowngradeAsync();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await PostAsync(SubscriptionEvent(NewEventId(), "customer.subscription.updated", current, "active", created: now));

        await PostAsync(SubscriptionEvent(NewEventId(), "customer.subscription.pending_update_applied", upgraded, "active", created: now - 5));

        var after = await ReadRowAsync(current.Coach.ProfileId);
        after.SubscriptionPlan.Code.Should().Be(current.PlanCode);
        after.PendingSubscriptionPlanPriceId.Should().Be(pendingRowId);
    }

    [Fact]
    public async Task Webhook_PendingUpdateApplied_RepeatedEventId_IsANoOp()
    {
        var (current, upgraded, _) = await ArrangeUpgradeWithScheduledDowngradeAsync();
        var eventId = NewEventId();

        await PostAsync(SubscriptionEvent(eventId, "customer.subscription.pending_update_applied", upgraded, "active"));
        await PostAsync(SubscriptionEvent(eventId, "customer.subscription.pending_update_applied", upgraded, "active"));

        (await ProcessedCountAsync(eventId)).Should().Be(1);
        (await ReadRowAsync(current.Coach.ProfileId)).SubscriptionPlan.Code.Should().Be(upgraded.PlanCode);
    }

    /// <summary>A coach on a one-role plan who does not pay the add-on yet, so a pending add-on update can be applied or expire.</summary>
    private async Task<Scenario> ArrangeOneRolePlanWithoutAddOnAsync()
    {
        await SubscriptionPlanPriceBuilder.EnsureFreePlanAsync(factory);
        var coach = await TestActors.Trainer(factory).CreateAsync(Ct);
        var plan = await PaidPlanBuilder.CreateAsync(factory, addOnMinorUnits: 9900, bothRolesIncluded: false);
        var priceId = await SetStripePriceAsync(plan);
        await CoachSubscriptionBuilder.SubscribeToPlanAsync(factory, coach, plan);

        var scenario = new Scenario(coach, plan, priceId, $"cus_{Guid.NewGuid():N}", $"sub_{Guid.NewGuid():N}");
        await UpdateRowAsync(coach.ProfileId, row =>
        {
            row.ExternalCustomerId = scenario.CustomerId;
            row.ExternalSubscriptionId = scenario.SubscriptionId;
        });

        return scenario;
    }

    [Fact]
    public async Task Webhook_PendingUpdateAppliedWithTheAddOnLine_MarksTheAddOnPaid_AndKeepsThePlan()
    {
        var scenario = await ArrangeOneRolePlanWithoutAddOnAsync();
        (await ReadRowAsync(scenario.Coach.ProfileId)).PaysSecondRoleAddOn.Should().BeFalse();

        var response = await PostAsync(SubscriptionEvent(
            NewEventId(), "customer.subscription.pending_update_applied", scenario, "active", extraPriceIds: ["price_addon_unknown_to_plans"]));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var after = await ReadRowAsync(scenario.Coach.ProfileId);
        after.PaysSecondRoleAddOn.Should().BeTrue();
        after.SubscriptionPlan.Code.Should().Be(scenario.PlanCode);
    }

    [Fact]
    public async Task Webhook_PendingUpdateAppliedWithTheAddOnLine_RepeatedEventId_IsANoOp()
    {
        var scenario = await ArrangeOneRolePlanWithoutAddOnAsync();
        var eventId = NewEventId();

        await PostAsync(SubscriptionEvent(eventId, "customer.subscription.pending_update_applied", scenario, "active", extraPriceIds: ["price_addon_unknown_to_plans"]));
        await PostAsync(SubscriptionEvent(eventId, "customer.subscription.pending_update_applied", scenario, "active", extraPriceIds: ["price_addon_unknown_to_plans"]));

        (await ProcessedCountAsync(eventId)).Should().Be(1);
        (await ReadRowAsync(scenario.Coach.ProfileId)).PaysSecondRoleAddOn.Should().BeTrue();
    }

    [Fact]
    public async Task Webhook_PendingUpdateExpiredForAnAddOn_LeavesTheAddOnUnpaid_AndRestoresNothing()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var scenario = await ArrangeOneRolePlanWithoutAddOnAsync();
        var eventId = NewEventId();

        var response = await PostAsync(SubscriptionEvent(eventId, "customer.subscription.pending_update_expired", scenario, "active"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.ExpiredUpgradeRestores.Should().ContainSingle().Which.Should().Be(
            new FakeSubscriptionService.RestoreCall(scenario.SubscriptionId, false), "no scheduled change is kept, so nothing is rebuilt");
        var after = await ReadRowAsync(scenario.Coach.ProfileId);
        after.PaysSecondRoleAddOn.Should().BeFalse();
        after.SubscriptionPlan.Code.Should().Be(scenario.PlanCode);
        after.PendingSubscriptionPlanPriceId.Should().BeNull();
        (await ProcessedCountAsync(eventId)).Should().Be(1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Webhook_PendingUpdateExpired_KeepsThePlan_AndAsksTheProviderToRestoreTheScheduledChange(bool rowHasScheduledChange)
    {
        var fake = BillingTestData.ResetProvider(factory);
        var (current, _, pendingRowId) = await ArrangeUpgradeWithScheduledDowngradeAsync();

        if (!rowHasScheduledChange)
        {
            await UpdateRowAsync(current.Coach.ProfileId, row => row.PendingSubscriptionPlanPriceId = null);
        }

        var eventId = NewEventId();
        var response = await PostAsync(SubscriptionEvent(eventId, "customer.subscription.pending_update_expired", current, "active"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.ExpiredUpgradeRestores.Should().ContainSingle().Which.Should().Be(
            new FakeSubscriptionService.RestoreCall(current.SubscriptionId, rowHasScheduledChange));
        var after = await ReadRowAsync(current.Coach.ProfileId);
        after.SubscriptionPlan.Code.Should().Be(current.PlanCode);
        after.PendingSubscriptionPlanPriceId.Should().Be(rowHasScheduledChange ? pendingRowId : null);
        (await ProcessedCountAsync(eventId)).Should().Be(1);
    }

    [Fact]
    public async Task Webhook_PendingUpdateExpired_WhenTheProviderFails_Returns500_AndStoresNoEventId_SoStripeRetries()
    {
        var fake = BillingTestData.ResetProvider(factory);
        fake.FailExpiredUpgradeRestore = true;
        var (current, _, _) = await ArrangeUpgradeWithScheduledDowngradeAsync();
        var eventId = NewEventId();

        var response = await PostAsync(SubscriptionEvent(eventId, "customer.subscription.pending_update_expired", current, "active"));

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await ProcessedCountAsync(eventId)).Should().Be(0);
    }

    [Fact]
    public async Task Webhook_PendingUpdateExpired_ForAnUnknownSubscription_Returns200_AndCallsNothing()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var unknown = new Scenario(await TestActors.Trainer(factory).CreateAsync(Ct), "none", "price_unknown", "cus_unknown", "sub_unknown");

        var response = await PostAsync(SubscriptionEvent(NewEventId(), "customer.subscription.pending_update_expired", unknown, "active"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.ExpiredUpgradeRestores.Should().BeEmpty();
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

    private async Task<int> RedemptionCountAsync(long promotionId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return await db.PromotionRedemptions.CountAsync(r => r.PromotionId == promotionId, Ct);
    }

    /// <summary>A coach about to subscribe, a code promotion for their plan, and the discount the fake provider reports.</summary>
    private async Task<(Scenario Scenario, Promotion Promotion, DateTimeOffset EndsAt)> ArrangeDiscountAsync(bool reportedAsCode = true)
    {
        var fake = BillingTestData.ResetProvider(factory);
        var scenario = await ArrangeOnFreePlanAsync();
        var promotion = await PromotionBuilder.CreateAsync(factory, [scenario.PlanCode]);
        var endsAt = DateTimeOffset.UtcNow.AddMonths(3);
        fake.SubscriptionDiscounts[scenario.SubscriptionId] = new SubscriptionDiscount(
            promotion.StripeCouponId, reportedAsCode ? promotion.StripePromotionCodeId : null, endsAt);

        return (scenario, promotion, endsAt);
    }

    [Fact]
    public async Task Webhook_SubscriptionWithOurPromotionCode_RecordsTheRedemption_AndSetsTheCurrentPromotion()
    {
        var (scenario, promotion, endsAt) = await ArrangeDiscountAsync();

        var response = await PostAsync(SubscriptionEvent(
            NewEventId(), "customer.subscription.created", scenario, "active", discountIds: ["di_1"]));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var row = await ReadRowAsync(scenario.Coach.ProfileId);
        row.CurrentPromotionId.Should().Be(promotion.Id);
        row.CurrentPromotionEndsAt.Should().BeCloseTo(endsAt, TimeSpan.FromSeconds(1));
        row.FirstPaidAt.Should().NotBeNull("a live provider subscription means the coach has now paid");
        (await RedemptionCountAsync(promotion.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Webhook_DiscountOfAPromotionAnAdminEnded_IsStillLinked_SoCoachesKeepIt()
    {
        var (scenario, promotion, _) = await ArrangeDiscountAsync();
        await EndPromotionAsync(promotion.Id);

        await PostAsync(SubscriptionEvent(NewEventId(), "customer.subscription.created", scenario, "active", discountIds: ["di_1"]));

        (await ReadRowAsync(scenario.Coach.ProfileId)).CurrentPromotionId.Should().Be(promotion.Id);
        (await RedemptionCountAsync(promotion.Id)).Should().Be(1);
    }

    private async Task EndPromotionAsync(long promotionId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        await db.Promotions
            .Where(p => p.Id == promotionId)
            .ExecuteUpdateAsync(set => set.SetProperty(p => p.EndedAt, DateTimeOffset.UtcNow.AddMinutes(-5)), Ct);
    }

    [Fact]
    public async Task Webhook_AnAutomaticCouponWithoutAPromotionCode_IsMappedByItsCouponId()
    {
        var (scenario, promotion, _) = await ArrangeDiscountAsync(reportedAsCode: false);

        await PostAsync(SubscriptionEvent(NewEventId(), "customer.subscription.created", scenario, "active", discountIds: ["di_1"]));

        (await ReadRowAsync(scenario.Coach.ProfileId)).CurrentPromotionId.Should().Be(promotion.Id);
        (await RedemptionCountAsync(promotion.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Webhook_TheSamePromotionOnALaterEvent_StaysOneRedemptionPerCoach()
    {
        var (scenario, promotion, _) = await ArrangeDiscountAsync();

        await PostAsync(SubscriptionEvent(NewEventId(), "customer.subscription.created", scenario, "active", discountIds: ["di_1"]));
        var second = await PostAsync(SubscriptionEvent(
            NewEventId(), "customer.subscription.updated", scenario, "active", discountIds: ["di_1"], created: DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds()));

        second.StatusCode.Should().Be(HttpStatusCode.OK);
        (await RedemptionCountAsync(promotion.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Webhook_ASubscriptionWithoutADiscount_ClearsTheCurrentPromotion()
    {
        var (scenario, promotion, _) = await ArrangeDiscountAsync();
        await UpdateRowAsync(scenario.Coach.ProfileId, row =>
        {
            row.CurrentPromotionId = promotion.Id;
            row.CurrentPromotionEndsAt = DateTimeOffset.UtcNow.AddDays(5);
        });

        await PostAsync(SubscriptionEvent(NewEventId(), "customer.subscription.updated", scenario, "active"));

        var row = await ReadRowAsync(scenario.Coach.ProfileId);
        row.CurrentPromotionId.Should().BeNull();
        row.CurrentPromotionEndsAt.Should().BeNull();
    }

    [Fact]
    public async Task Webhook_ADiscountThatIsNotOurPromotion_SetsNoPromotionAndRecordsNothing()
    {
        var fake = BillingTestData.ResetProvider(factory);
        var scenario = await ArrangeOnFreePlanAsync();
        var promotion = await PromotionBuilder.CreateAsync(factory, [scenario.PlanCode]);
        fake.SubscriptionDiscounts[scenario.SubscriptionId] = new SubscriptionDiscount("formup-gift-100", null, null);

        var response = await PostAsync(SubscriptionEvent(
            NewEventId(), "customer.subscription.created", scenario, "active", discountIds: ["di_1"]));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadRowAsync(scenario.Coach.ProfileId)).CurrentPromotionId.Should().BeNull();
        (await RedemptionCountAsync(promotion.Id)).Should().Be(0);
    }
}
