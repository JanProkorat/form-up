using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
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
/// <param name="subscriptions">The payment provider seam, read to see which discount a subscription carries.</param>
/// <param name="timeProvider">Clock.</param>
/// <param name="notifier">Realtime notifier; tells the coach's open app that the subscription changed.</param>
/// <param name="logger">Logger; receives event ids and types only, never the payload.</param>
internal sealed class StripeWebhookEndpoint(
    IApplicationDbContext db,
    IOptions<BillingOptions> options,
    ISubscriptionService subscriptions,
    TimeProvider timeProvider,
    IRealtimeNotifier notifier,
    ILogger<StripeWebhookEndpoint> logger) : EndpointWithoutRequest
{
    private const string SignatureHeader = "Stripe-Signature";

    /// <summary>The coach's row the applied event changed; null when it changed nothing. One instance serves one request.</summary>
    private CoachSubscription? _changedRow;

    /// <summary>The subscription whose remembered second role the applied event consumed; null when none.</summary>
    private string? _rememberedRoleSubscriptionId;

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

                await TryClearRememberedRoleAsync(ct);

                if (_changedRow is not null)
                {
                    await TryNotifyCoachAsync(stripeEvent.Id, _changedRow.ProfessionalProfileId, ct);
                }
            }
        }
        catch (DbUpdateException ex) when (IsDuplicateEvent(ex))
        {
            logger.LogInformation("Stripe event {EventId} was processed concurrently", stripeEvent.Id);
        }

        await AcknowledgeAsync(ct);
    }

    /// <summary>
    /// Tells the coach's open app to refresh the subscription after a saved change. A failure is logged and swallowed so
    /// Stripe still gets its 200.
    /// </summary>
    private async Task TryNotifyCoachAsync(string eventId, long profileId, CancellationToken ct)
    {
        Guid? coachUserId = null;

        try
        {
            coachUserId = await db.ProfessionalProfiles
                .AsNoTracking()
                .Where(profile => profile.Id == profileId)
                .Select(profile => (Guid?)profile.UserId)
                .FirstOrDefaultAsync(ct);

            if (coachUserId is { } userId)
            {
                await notifier.NotifyAsync(userId, "subscriptionchanged", new { action = "synced" }, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Subscription change notification failed for Stripe event {EventId} and coach {CoachId}", eventId, coachUserId);
        }
    }

    private Task AcknowledgeAsync(CancellationToken ct) => Send.OkAsync(new { Received = true }, ct);

    private static bool IsDuplicateEvent(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: "23505", TableName: "processed_webhook_events" };

    private async Task<bool> ApplyAsync(Event stripeEvent, CancellationToken ct)
    {
        var applied = (stripeEvent.Type, stripeEvent.Data?.Object) switch
        {
            ("customer.subscription.created" or "customer.subscription.updated", Subscription subscription)
                => await ApplySubscriptionAsync(stripeEvent, subscription, pendingUpdateApplied: false, ct),
            // UNVERIFIED: pending_update_applied and pending_update_expired carry the Subscription as data.object — https://docs.stripe.com/billing/subscriptions/pending-updates
            ("customer.subscription.pending_update_applied", Subscription subscription)
                => await ApplySubscriptionAsync(stripeEvent, subscription, pendingUpdateApplied: true, ct),
            ("customer.subscription.pending_update_expired", Subscription subscription)
                => await ApplyExpiredUpdateAsync(stripeEvent, subscription, ct),
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

    private async Task<bool> ApplySubscriptionAsync(
        Event stripeEvent, Subscription subscription, bool pendingUpdateApplied, CancellationToken ct)
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
            // The updated event that carried the new items may have been applied first; the scheduled-change marker still goes.
            if (pendingUpdateApplied && row.SubscriptionPlanPriceId == planPrice.Id)
            {
                row.PendingSubscriptionPlanPriceId = null;
                _changedRow = row;
                await GrantPaidRoleAsync(subscription, planPrice, row, ct);
                return true;
            }

            return false;
        }

        row.ExternalCustomerId ??= subscription.CustomerId;
        row.ExternalSubscriptionId ??= subscription.Id;
        row.LastExternalEventAt = StripeWebhookMapper.ToUtc(stripeEvent.Created);
        _changedRow = row;

        if (IsGiftInForce(row) && !IsBoughtAfterGift(row, subscription))
        {
            // A scheduled add-on removal still completes under a gift, which otherwise freezes the plan fields.
            if (row.SecondRoleAddOnEndsAt is not null && !HasAddOnItem(subscription, planPrice))
            {
                row.PaysSecondRoleAddOn = false;
                row.SecondRoleAddOnEndsAt = null;
            }

            logger.LogInformation("Stripe event {EventId} left the plan of a coach with a given plan unchanged", stripeEvent.Id);
            return true;
        }

        row.ExternalSubscriptionId = subscription.Id;
        row.SubscriptionPlanId = planPrice.SubscriptionPlanId;
        row.SubscriptionPlanPriceId = planPrice.Id;
        row.Status = StripeWebhookMapper.ToStatus(subscription.Status) ?? row.Status;
        row.PaysSecondRoleAddOn = !planPrice.SubscriptionPlan.BothRolesIncluded && HasAddOnItem(subscription, planPrice);

        // UNVERIFIED: the schedule's phase change at renewal emits customer.subscription.updated carrying the new items — https://docs.stripe.com/billing/subscriptions/subscription-schedules
        // The event right after scheduling still carries the add-on item, so the marker survives until the renewal drops it.
        if (!row.PaysSecondRoleAddOn)
        {
            row.SecondRoleAddOnEndsAt = null;
        }

        row.CancelAtPeriodEnd = subscription.CancelAtPeriodEnd || subscription.CancelAt is not null;

        if (!row.CancelAtPeriodEnd)
        {
            row.RenewalCancelledByAdminAt = null;
        }

        row.CurrentPeriodEndsAt = StripeWebhookMapper.LatestPeriodEnd(subscription);
        row.TrialEndsAt = subscription.TrialEnd is { } trialEnd ? StripeWebhookMapper.ToUtc(trialEnd) : null;
        row.GivenFreeAt = null;
        row.FreeUntil = null;

        if (pendingUpdateApplied || row.PendingSubscriptionPlanPriceId == planPrice.Id)
        {
            row.PendingSubscriptionPlanPriceId = null;
        }

        if (row.Status is SubscriptionStatus.Active or SubscriptionStatus.Trialing or SubscriptionStatus.PastDue)
        {
            row.FirstPaidAt ??= StripeWebhookMapper.ToUtc(stripeEvent.Created);
        }

        await ApplyDiscountAsync(stripeEvent, row, subscription, ct);

        if (pendingUpdateApplied)
        {
            await GrantPaidRoleAsync(subscription, planPrice, row, ct);
        }

        return true;
    }

    /// <summary>
    /// A pending update was applied: adds the role the coach bought when it was a second-role add-on, now paid. The role
    /// is remembered in the subscription metadata; a leftover one on any other applied update (a plan upgrade) adds nothing.
    /// The remembered role is cleared at the provider after the save.
    /// </summary>
    private async Task GrantPaidRoleAsync(Subscription subscription, SubscriptionPlanPrice planPrice, CoachSubscription row, CancellationToken ct)
    {
        if (subscription.Metadata is null
            || !subscription.Metadata.TryGetValue(StripeRequestBuilder.PendingSecondRoleKey, out var role)
            || string.IsNullOrEmpty(role))
        {
            return;
        }

        _rememberedRoleSubscriptionId = subscription.Id;

        if (planPrice.SubscriptionPlan.BothRolesIncluded || !HasAddOnItem(subscription, planPrice) || !CoachRoleGranter.IsCoachRole(role))
        {
            return;
        }

        var profile = await db.ProfessionalProfiles.FirstOrDefaultAsync(p => p.Id == row.ProfessionalProfileId, ct);

        if (profile is not null && await CoachRoleGranter.GrantAsync(db, profile, role, ct))
        {
            logger.LogInformation("Added the paid second role to coach {ProfileId}", profile.Id);
        }
    }

    /// <summary>
    /// Removes the remembered role from the subscription after the event is saved. A failure is logged and swallowed: the
    /// role is already added, and a leftover key adds nothing on a later event without a paid add-on item.
    /// </summary>
    private async Task TryClearRememberedRoleAsync(CancellationToken ct)
    {
        if (_rememberedRoleSubscriptionId is not { } subscriptionId || !subscriptions.IsConfigured)
        {
            return;
        }

        try
        {
            await subscriptions.ClearPendingSecondRoleAsync(subscriptionId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Removing the remembered second role of subscription {SubscriptionId} failed", subscriptionId);
        }
    }

    /// <summary>
    /// Mirrors the subscription's discount onto the coach: the current promotion (cleared when the subscription has no
    /// discount or one of ours does not match) and, once per coach and promotion, a redemption row. Covers codes typed on
    /// Stripe Checkout and automatic coupons. A provider failure propagates so Stripe retries the event.
    /// </summary>
    private async Task ApplyDiscountAsync(Event stripeEvent, CoachSubscription row, Subscription subscription, CancellationToken ct)
    {
        var hasDiscount = (subscription.DiscountIds?.Count ?? 0) > 0 || (subscription.Discounts?.Count ?? 0) > 0;

        if (!hasDiscount)
        {
            ClearPromotion(row);
            return;
        }

        if (!subscriptions.IsConfigured)
        {
            return;
        }

        var discount = await subscriptions.GetSubscriptionDiscountAsync(subscription.Id, ct);
        var promotion = discount is null ? null : await FindPromotionAsync(discount, ct);

        if (discount is null || promotion is null)
        {
            ClearPromotion(row);
            return;
        }

        row.CurrentPromotionId = promotion.Id;
        row.CurrentPromotionEndsAt = discount.EndsAt;

        var alreadyRecorded = await db.PromotionRedemptions
            .AnyAsync(r => r.PromotionId == promotion.Id && r.ProfessionalProfileId == row.ProfessionalProfileId, ct);

        if (!alreadyRecorded)
        {
            db.PromotionRedemptions.Add(new PromotionRedemption
            {
                PromotionId = promotion.Id,
                ProfessionalProfileId = row.ProfessionalProfileId,
                RedeemedAt = StripeWebhookMapper.ToUtc(stripeEvent.Created),
            });
        }
    }

    private static void ClearPromotion(CoachSubscription row)
    {
        row.CurrentPromotionId = null;
        row.CurrentPromotionEndsAt = null;
    }

    private async Task<Promotion?> FindPromotionAsync(SubscriptionDiscount discount, CancellationToken ct)
    {
        var promotionCodeId = discount.PromotionCodeId;
        var couponId = discount.CouponId;

        return await db.Promotions
            .AsNoTracking()
            .Where(p => (promotionCodeId != null && p.StripePromotionCodeId == promotionCodeId)
                        || (couponId != null && p.StripeCouponId == couponId))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// A pending upgrade expired unpaid: the row keeps its plan. What the release of a scheduled downgrade dropped is put back
    /// at Stripe. A provider failure propagates so Stripe retries the event.
    /// </summary>
    private async Task<bool> ApplyExpiredUpdateAsync(Event stripeEvent, Subscription subscription, CancellationToken ct)
    {
        var row = await FindRowAsync(subscription.Id, subscription.CustomerId, ct);

        if (row is null)
        {
            logger.LogWarning("Stripe event {EventId} ({EventType}) matches no coach; ignored", stripeEvent.Id, stripeEvent.Type);
            return false;
        }

        if (!subscriptions.IsConfigured)
        {
            return true;
        }

        await subscriptions.RestoreAfterExpiredUpgradeAsync(subscription.Id, row.PendingSubscriptionPlanPriceId is not null, ct);

        // The add-on was never paid, so the role it would have bought is forgotten.
        if (subscription.Metadata?.ContainsKey(StripeRequestBuilder.PendingSecondRoleKey) == true)
        {
            await subscriptions.ClearPendingSecondRoleAsync(subscription.Id, ct);
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
        _changedRow = row;
        row.ExternalSubscriptionId = null;
        row.NextPaymentAttemptAt = null;

        if (IsGiftInForce(row))
        {
            // The gift plan keeps running, so Status and the gift fields stay; only the paid leftovers go,
            // including the paid period end (nothing renews, and a never-paid gift row has none either).
            row.CurrentPeriodEndsAt = null;
            row.CancelAtPeriodEnd = false;
            row.RenewalCancelledByAdminAt = null;
            row.PendingSubscriptionPlanPriceId = null;
            row.SecondRoleAddOnEndsAt = null;
            row.PaysSecondRoleAddOn = false;

            return true;
        }

        // A canceled status already resolves to the Free plan's entitlements and the keep-clients choice, not a lock.
        row.Status = SubscriptionStatus.Canceled;
        row.CancelAtPeriodEnd = false;
        row.RenewalCancelledByAdminAt = null;
        row.PendingSubscriptionPlanPriceId = null;
        row.SecondRoleAddOnEndsAt = null;

        return true;
    }

    private static bool HasAddOnItem(Subscription subscription, SubscriptionPlanPrice planPrice) =>
        subscription.Items.Data.Any(item => item.Price.Id != planPrice.ExternalPriceId);

    private async Task<bool> ApplyInvoiceAsync(Event stripeEvent, Invoice invoice, bool paymentFailed, CancellationToken ct)
    {
        var row = await FindRowAsync(StripeWebhookMapper.SubscriptionIdOf(invoice), invoice.CustomerId, ct);

        if (row is null)
        {
            logger.LogInformation("Stripe event {EventId} ({EventType}) matches no coach; ignored", stripeEvent.Id, stripeEvent.Type);
            return false;
        }

        var invoiceSubscriptionId = StripeWebhookMapper.SubscriptionIdOf(invoice);

        if (!string.IsNullOrEmpty(invoiceSubscriptionId) && invoiceSubscriptionId != row.ExternalSubscriptionId)
        {
            logger.LogInformation(
                "Stripe event {EventId} ({EventType}) belongs to a subscription not yet recorded on the coach; ignored",
                stripeEvent.Id, stripeEvent.Type);
            return false;
        }

        if (IsStale(stripeEvent, row))
        {
            return false;
        }

        row.LastExternalEventAt = StripeWebhookMapper.ToUtc(stripeEvent.Created);
        _changedRow = row;

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

    /// <summary>
    /// True when the Stripe subscription was created after the plan was given, so the coach bought it and it ends the gift.
    /// </summary>
    private static bool IsBoughtAfterGift(CoachSubscription row, Subscription subscription) =>
        row.GivenFreeAt is { } givenFreeAt && StripeWebhookMapper.ToUtc(subscription.Created) > givenFreeAt;
}
