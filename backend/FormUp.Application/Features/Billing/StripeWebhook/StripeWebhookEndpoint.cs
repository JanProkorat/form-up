using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Services.Billing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Stripe;

namespace FormUp.Application.Features.Billing.StripeWebhook;

/// <summary>
/// Receives Stripe webhook events and mirrors them onto the coach's <see cref="CoachSubscription"/>. Anonymous by design:
/// the signature over the raw body is the credential.
/// </summary>
/// <param name="db">Application database context.</param>
/// <param name="options">Billing settings holding the webhook signing secret.</param>
/// <param name="timeProvider">Clock.</param>
/// <param name="logger">Logger; receives event ids and types only, never the payload.</param>
internal sealed class StripeWebhookEndpoint(
    IApplicationDbContext db,
    IOptions<BillingOptions> options,
    TimeProvider timeProvider,
    ILogger<StripeWebhookEndpoint> logger) : EndpointWithoutRequest
{
    private const string SignatureHeader = "Stripe-Signature";

    /// <inheritdoc />
    public override void Configure()
    {
        Post("/billing/stripe/webhook");
        AllowAnonymous();
        Description(b => b.ExcludeFromDescription());
        Summary(s =>
        {
            s.Summary = "Stripe webhook";
            s.Description = "Anonymous: the Stripe-Signature header over the raw body is the credential. Applies subscription and " +
                            "invoice events to the coach's subscription; a repeated event id is a no-op.";
            s.Response(StatusCodes.Status200OK, "The event was applied, ignored or already processed");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest, "The signature is missing or does not match the body");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(CancellationToken ct)
    {
        var secret = options.Value.WebhookSecret;
        var signature = HttpContext.Request.Headers[SignatureHeader].ToString();

        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(signature))
        {
            await this.SendProblemAsync(
                StatusCodes.Status400BadRequest, ErrorCodes.WebhookSignatureInvalid, "Missing webhook signature.", ct);
            return;
        }

        using var reader = new StreamReader(HttpContext.Request.Body);
        var body = await reader.ReadToEndAsync(ct);
        Event stripeEvent;

        try
        {
            stripeEvent = EventUtility.ConstructEvent(body, signature, secret, throwOnApiVersionMismatch: false);
        }
        catch (Exception ex) when (ex is StripeException or System.Text.Json.JsonException)
        {
            logger.LogWarning("Stripe webhook rejected: signature or body could not be verified");
            await this.SendProblemAsync(
                StatusCodes.Status400BadRequest, ErrorCodes.WebhookSignatureInvalid, "Invalid webhook signature.", ct);
            return;
        }

        if (await db.ProcessedWebhookEvents.AnyAsync(e => e.EventId == stripeEvent.Id, ct))
        {
            logger.LogInformation("Stripe event {EventId} ({EventType}) already processed", stripeEvent.Id, stripeEvent.Type);
            await AcknowledgeAsync(ct);
            return;
        }

        try
        {
            if (await ApplyAsync(stripeEvent, ct))
            {
                db.ProcessedWebhookEvents.Add(new ProcessedWebhookEvent
                {
                    EventId = stripeEvent.Id,
                    EventType = stripeEvent.Type,
                    EventCreatedAt = StripeWebhookMapper.ToUtc(stripeEvent.Created),
                });

                // One save: the event id and the subscription change commit together or not at all.
                await db.SaveChangesAsync(ct);
            }
        }
        catch (DbUpdateException ex) when (IsDuplicateEvent(ex))
        {
            logger.LogInformation("Stripe event {EventId} was processed concurrently", stripeEvent.Id);
        }

        await AcknowledgeAsync(ct);
    }

    private Task AcknowledgeAsync(CancellationToken ct) => Send.OkAsync(new { Received = true }, ct);

    private static bool IsDuplicateEvent(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: "23505", TableName: "processed_webhook_events" };

    private async Task<bool> ApplyAsync(Event stripeEvent, CancellationToken ct)
    {
        var applied = (stripeEvent.Type, stripeEvent.Data?.Object) switch
        {
            ("customer.subscription.created" or "customer.subscription.updated", Subscription subscription)
                => await ApplySubscriptionAsync(stripeEvent, subscription, ct),
            ("customer.subscription.deleted", Subscription subscription)
                => await ApplyDeletedAsync(stripeEvent, subscription, ct),
            ("invoice.paid", Invoice invoice)
                => await ApplyInvoiceAsync(stripeEvent, invoice, paymentFailed: false, ct),
            ("invoice.payment_failed", Invoice invoice)
                => await ApplyInvoiceAsync(stripeEvent, invoice, paymentFailed: true, ct),
            _ => (bool?)null,
        };

        if (applied is null)
        {
            logger.LogInformation("Stripe event {EventId} of type {EventType} ignored", stripeEvent.Id, stripeEvent.Type);
        }

        return applied == true;
    }

    private async Task<bool> ApplySubscriptionAsync(Event stripeEvent, Subscription subscription, CancellationToken ct)
    {
        var priceIds = subscription.Items.Data.Select(item => item.Price.Id).ToList();
        var planPrice = await db.SubscriptionPlanPrices
            .Include(price => price.SubscriptionPlan)
            .Where(price => price.ExternalPriceId != null && priceIds.Contains(price.ExternalPriceId))
            .FirstOrDefaultAsync(ct);

        if (planPrice is null)
        {
            logger.LogWarning("Stripe event {EventId} ({EventType}) names no known price; ignored", stripeEvent.Id, stripeEvent.Type);
            return false;
        }

        var row = await FindRowAsync(subscription.Id, subscription.CustomerId, ct)
                  ?? await FindOrCreateByMetadataAsync(subscription, planPrice, ct);

        if (row is null)
        {
            logger.LogWarning("Stripe event {EventId} ({EventType}) matches no coach; ignored", stripeEvent.Id, stripeEvent.Type);
            return false;
        }

        if (IsStale(stripeEvent, row))
        {
            return false;
        }

        row.ExternalCustomerId ??= subscription.CustomerId;
        row.ExternalSubscriptionId ??= subscription.Id;
        row.LastExternalEventAt = StripeWebhookMapper.ToUtc(stripeEvent.Created);

        if (IsGiftInForce(row))
        {
            logger.LogInformation("Stripe event {EventId} left the plan of a coach with a given plan unchanged", stripeEvent.Id);
            return true;
        }

        row.ExternalSubscriptionId = subscription.Id;
        row.SubscriptionPlanId = planPrice.SubscriptionPlanId;
        row.SubscriptionPlanPriceId = planPrice.Id;
        row.Status = StripeWebhookMapper.ToStatus(subscription.Status) ?? row.Status;
        row.PaysSecondRoleAddOn = !planPrice.SubscriptionPlan.BothRolesIncluded
                                  && subscription.Items.Data.Any(item => item.Price.Id != planPrice.ExternalPriceId);
        row.CancelAtPeriodEnd = subscription.CancelAtPeriodEnd || subscription.CancelAt is not null;
        row.CurrentPeriodEndsAt = StripeWebhookMapper.LatestPeriodEnd(subscription);
        row.TrialEndsAt = subscription.TrialEnd is { } trialEnd ? StripeWebhookMapper.ToUtc(trialEnd) : null;
        row.GivenFreeAt = null;
        row.FreeUntil = null;

        if (row.PendingSubscriptionPlanPriceId == planPrice.Id)
        {
            row.PendingSubscriptionPlanPriceId = null;
        }

        return true;
    }

    private async Task<bool> ApplyDeletedAsync(Event stripeEvent, Subscription subscription, CancellationToken ct)
    {
        var row = await FindRowAsync(subscription.Id, subscription.CustomerId, ct);

        if (row is null)
        {
            logger.LogWarning("Stripe event {EventId} ({EventType}) matches no coach; ignored", stripeEvent.Id, stripeEvent.Type);
            return false;
        }

        if (IsStale(stripeEvent, row))
        {
            return false;
        }

        row.LastExternalEventAt = StripeWebhookMapper.ToUtc(stripeEvent.Created);
        row.ExternalSubscriptionId = null;
        row.NextPaymentAttemptAt = null;

        if (IsGiftInForce(row))
        {
            return true;
        }

        // A canceled status already resolves to the Free plan's entitlements and the keep-clients choice, not a lock.
        row.Status = SubscriptionStatus.Canceled;
        row.CancelAtPeriodEnd = false;
        row.PendingSubscriptionPlanPriceId = null;

        return true;
    }

    private async Task<bool> ApplyInvoiceAsync(Event stripeEvent, Invoice invoice, bool paymentFailed, CancellationToken ct)
    {
        var row = await FindRowAsync(StripeWebhookMapper.SubscriptionIdOf(invoice), invoice.CustomerId, ct);

        if (row is null)
        {
            logger.LogInformation("Stripe event {EventId} ({EventType}) matches no coach; ignored", stripeEvent.Id, stripeEvent.Type);
            return false;
        }

        if (IsStale(stripeEvent, row))
        {
            return false;
        }

        row.LastExternalEventAt = StripeWebhookMapper.ToUtc(stripeEvent.Created);

        if (IsGiftInForce(row))
        {
            return true;
        }

        if (paymentFailed && row.Status is SubscriptionStatus.Active or SubscriptionStatus.Trialing or SubscriptionStatus.PastDue)
        {
            // Past due keeps the paid plan's features while Stripe retries; an Incomplete first payment stays Incomplete.
            row.Status = SubscriptionStatus.PastDue;
        }
        else if (!paymentFailed && row.Status is SubscriptionStatus.PastDue or SubscriptionStatus.Incomplete)
        {
            row.Status = SubscriptionStatus.Active;
        }

        row.NextPaymentAttemptAt = paymentFailed && invoice.NextPaymentAttempt is { } nextAttempt
            ? StripeWebhookMapper.ToUtc(nextAttempt)
            : null;

        return true;
    }

    private bool IsGiftInForce(CoachSubscription row) => row.IsGiftInForce(timeProvider.GetUtcNow());

    private bool IsStale(Event stripeEvent, CoachSubscription row)
    {
        if (row.LastExternalEventAt is { } last && StripeWebhookMapper.ToUtc(stripeEvent.Created) < last)
        {
            logger.LogInformation(
                "Stripe event {EventId} ({EventType}) is older than the last applied event; skipped", stripeEvent.Id, stripeEvent.Type);
            return true;
        }

        return false;
    }

    private async Task<CoachSubscription?> FindRowAsync(string? subscriptionId, string? customerId, CancellationToken ct)
    {
        CoachSubscription? row = null;

        if (!string.IsNullOrEmpty(subscriptionId))
        {
            row = await db.CoachSubscriptions.FirstOrDefaultAsync(cs => cs.ExternalSubscriptionId == subscriptionId, ct);
        }

        if (row is null && !string.IsNullOrEmpty(customerId))
        {
            row = await db.CoachSubscriptions.FirstOrDefaultAsync(cs => cs.ExternalCustomerId == customerId, ct);
        }

        return row;
    }

    private async Task<CoachSubscription?> FindOrCreateByMetadataAsync(
        Subscription subscription, SubscriptionPlanPrice planPrice, CancellationToken ct)
    {
        if (!subscription.Metadata.TryGetValue("userId", out var userIdText) || !Guid.TryParse(userIdText, out var userId))
        {
            return null;
        }

        var profileId = await db.ProfessionalProfiles
            .AsNoTracking()
            .Where(profile => profile.UserId == userId)
            .Select(profile => (long?)profile.Id)
            .FirstOrDefaultAsync(ct);

        if (profileId is null)
        {
            return null;
        }

        var row = await db.CoachSubscriptions.FirstOrDefaultAsync(cs => cs.ProfessionalProfileId == profileId, ct);

        if (row is not null)
        {
            return row;
        }

        row = new CoachSubscription
        {
            ProfessionalProfileId = profileId.Value,
            SubscriptionPlanId = planPrice.SubscriptionPlanId,
            SubscriptionPlanPriceId = planPrice.Id,
            Status = SubscriptionStatus.Incomplete,
        };
        db.CoachSubscriptions.Add(row);

        return row;
    }
}
