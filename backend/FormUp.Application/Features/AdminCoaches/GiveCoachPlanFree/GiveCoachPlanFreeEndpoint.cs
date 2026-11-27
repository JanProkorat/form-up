using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.AdminCoaches.Shared;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Services.Billing;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.AdminCoaches.GiveCoachPlanFree;

/// <summary>
/// Changes a coach's plan to a paid plan set by Form Up, optionally until a date. Shows as set by Form Up and, once it
/// ends, the coach falls back to the Free plan like a lapsed coach.
/// </summary>
/// <param name="db">Application database context.</param>
/// <param name="audit">Audit logging service.</param>
/// <param name="timeProvider">Clock.</param>
/// <param name="notifications">Notification service.</param>
/// <param name="notifier">Realtime notifier.</param>
/// <param name="logger">Logger.</param>
/// <param name="subscriptionService">The payment provider seam.</param>
internal sealed class GiveCoachPlanFreeEndpoint(
    IApplicationDbContext db,
    IAuditService audit,
    TimeProvider timeProvider,
    INotificationService notifications,
    IRealtimeNotifier notifier,
    ILogger<GiveCoachPlanFreeEndpoint> logger,
    ISubscriptionService subscriptionService)
    : Endpoint<GiveCoachPlanFreeRequest, GiveCoachPlanFreeResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/admin/coaches/{CoachId}/give-free");
        Roles(AppRoles.Admin);
        Summary(s =>
        {
            s.Summary = "Change a coach's plan";
            s.Description = "Puts the coach on the plan's open price row as a plan set by Form Up: Active, no pending price change, and the " +
                            "kept/released client choice made while lapsed is reset. The plan ends at FreeUntil, or never when it is null. The " +
                            "reason is recorded in the coach's history. Replaces any earlier subscription assignment. The coach gets an in-app " +
                            "notification and a subscriptionchanged event; a failure to notify does not fail the call. A given plan and a " +
                            "paid Stripe subscription never run together: a coach with an Active, Trialing, PastDue or Incomplete Stripe " +
                            "subscription has it cancelled at once and the unused days of the paid period are refunded to the card (nothing " +
                            "for a payment that never went through or a 100 % discount). The second-role add-on ends with the subscription, " +
                            "and the given plan includes both professional roles while it is in force (no role is removed when it ends). If Stripe fails the plan is unchanged, the refund stays owed and repeating the call pays it once, even after Stripe's webhook already cancelled the subscription.";
            s.Response<GiveCoachPlanFreeResponse>(StatusCodes.Status200OK, "The plan as it now stands");
            s.Response<ProblemDetails>(StatusCodes.Status502BadGateway,
                "Stripe failed; the plan is unchanged and the refund stays owed for a retry (BILLING_PROVIDER_ERROR)");
            s.Response<ProblemDetails>(StatusCodes.Status503ServiceUnavailable,
                "The coach has a cancellable Stripe subscription and Stripe is not configured on this server (BILLING_NOT_CONFIGURED)");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest, "Invalid request body");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or invalid credentials");
            s.Response(StatusCodes.Status403Forbidden, "Caller is not an Admin");
            s.Response(StatusCodes.Status404NotFound,
                "The coach or the plan code is unknown (PROFESSIONAL_NOT_FOUND, SUBSCRIPTION_PLAN_NOT_FOUND)");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(GiveCoachPlanFreeRequest req, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(AppClaims.UserId), out var adminId))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var profileId = await db.ProfessionalProfiles
            .AsNoTracking()
            .Where(p => p.UserId == req.CoachId)
            .Select(p => (long?)p.Id)
            .FirstOrDefaultAsync(ct);

        if (profileId is null)
        {
            await this.SendProblemAsync(
                StatusCodes.Status404NotFound, ErrorCodes.ProfessionalNotFound, "Coach not found.", ct);
            return;
        }

        var openPrice = await db.SubscriptionPlanPrices
            .AsNoTracking()
            .Where(p => p.SubscriptionPlan.Code == req.PlanCode && p.ValidTo == null
                        && p.BillingInterval == BillingInterval.Monthly)
            .Select(p => new { p.Id, p.SubscriptionPlanId })
            .FirstOrDefaultAsync(ct);
        if (openPrice is null)
        {
            await this.SendProblemAsync(
                StatusCodes.Status404NotFound, ErrorCodes.SubscriptionPlanNotFound, "Subscription plan not found.", ct);
            return;
        }

        var now = timeProvider.GetUtcNow();

        // Stripe first, read untracked: the cancel takes seconds and Stripe's deleted webhook can rewrite the row meanwhile.
        // The row is tracked only afterwards, so every assignment below is compared with what is stored now, not with a
        // value loaded before the cancel. A refusal leaves the plan untouched and the refund owed (marker).
        var beforeCancel = await db.CoachSubscriptions
            .AsNoTracking()
            .FirstOrDefaultAsync(cs => cs.ProfessionalProfileId == profileId, ct);
        var cancelsAtProvider = beforeCancel?.HasCancellableProviderSubscription ?? false;
        var endedAddOn = beforeCancel?.PaysSecondRoleAddOn ?? false;
        var pendingId = beforeCancel?.PendingRefundSubscriptionId;
        var cancellableId = cancelsAtProvider ? beforeCancel!.ExternalSubscriptionId : null;
        var settlesAtProvider = pendingId is not null || cancellableId is not null;
        var refund = CancelRefundResult.None;

        // A refund an earlier attempt left owed (marker) is paid now even after the webhook cleared the live id.
        if (settlesAtProvider)
        {
            var settled = await TrySettleAtProviderAsync(beforeCancel!.Id, pendingId, cancellableId, ct);

            if (settled is null)
            {
                return;
            }

            refund = settled;
        }

        var subscription = await db.CoachSubscriptions
            .FirstOrDefaultAsync(cs => cs.ProfessionalProfileId == profileId, ct);

        if (subscription is null)
        {
            subscription = new CoachSubscription
            {
                ProfessionalProfileId = profileId.Value,
                SubscriptionPlanId = openPrice.SubscriptionPlanId,
                SubscriptionPlanPriceId = openPrice.Id,
            };
            db.CoachSubscriptions.Add(subscription);
        }

        subscription.SubscriptionPlanId = openPrice.SubscriptionPlanId;
        subscription.SubscriptionPlanPriceId = openPrice.Id;
        subscription.PendingSubscriptionPlanPriceId = null;
        subscription.GivenFreeAt = now;
        subscription.FreeUntil = req.FreeUntil;
        subscription.Status = SubscriptionStatus.Active;
        subscription.PendingRefundSubscriptionId = null;

        // The add-on is billed on the subscription that was just cancelled. Only the billing flags go; no role is removed, so
        // the coach keeps what they hold. A given plan includes both roles while it is in force (EntitlementService).
        subscription.PaysSecondRoleAddOn = false;
        subscription.SecondRoleAddOnEndsAt = null;

        if (cancelsAtProvider)
        {
            // Same end state as the deleted webhook; the event time lets a late older update be skipped as stale.
            subscription.ExternalSubscriptionId = null;
            subscription.NextPaymentAttemptAt = null;
            subscription.CurrentPeriodEndsAt = null;
            subscription.CancelAtPeriodEnd = false;
            subscription.RenewalCancelledByAdminAt = null;
            subscription.CurrentPromotionId = null;
            subscription.CurrentPromotionEndsAt = null;

            // Read after the cancel and never behind the webhook's stored event time, so the stored time only moves forward.
            var afterCancel = timeProvider.GetUtcNow();
            var eventAt = new DateTimeOffset(afterCancel.Ticks - (afterCancel.Ticks % TimeSpan.TicksPerSecond), afterCancel.Offset);
            subscription.LastExternalEventAt = subscription.LastExternalEventAt is { } stored && stored > eventAt ? stored : eventAt;
        }

        await CoachLapseLinkReset.ResetAsync(db, profileId.Value, ct);
        await db.SaveChangesAsync(ct);

        var fields = new Dictionary<string, object?>
        {
            ["planCode"] = req.PlanCode,
            ["freeUntil"] = req.FreeUntil?.ToString("O"),
            ["providerSubscriptionCancelled"] = settlesAtProvider,
            ["refundedMinorUnits"] = refund.RefundedMinorUnits,
            ["refundCurrency"] = refund.Currency,
            ["secondRoleAddOnEnded"] = endedAddOn,
        };

        await audit.LogAsync(
            adminId,
            AdminCoachAuditActions.GivePlanFree,
            nameof(ApplicationUser),
            req.CoachId,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            newValues: AuditPayload.WithReason(fields, req.Reason.Trim()),
            ct: ct);

        await CoachPlanNotifier.TryNotifyAsync(
            db, notifications, notifier, logger, req.CoachId, openPrice.SubscriptionPlanId,
            CoachPlanChangeAction.Changed, req.FreeUntil, ct);

        await Send.OkAsync(new GiveCoachPlanFreeResponse
        {
            CoachId = req.CoachId,
            PlanCode = req.PlanCode,
            GivenFreeAt = now,
            FreeUntil = req.FreeUntil,
            RefundedMinorUnits = refund.RefundedMinorUnits > 0 && refund.Currency is not null
                ? refund.RefundedMinorUnits
                : null,
            RefundCurrency = refund.RefundedMinorUnits > 0 ? refund.Currency?.ToUpperInvariant() : null,
        }, ct);
    }

    /// <summary>
    /// Settles the owed-refund marker id and the live subscription id at Stripe, each at most once: cancels now and
    /// refunds unused days (an already-cancelled subscription is only refunded). The live id is written to the marker
    /// first, so a retry can still refund after the webhook cleared it. Answers 503 or 502 and returns null when it could
    /// not.
    /// </summary>
    private async Task<CancelRefundResult?> TrySettleAtProviderAsync(
        long subscriptionRowId, string? pendingId, string? cancellableId, CancellationToken ct)
    {
        if (!subscriptionService.IsConfigured)
        {
            await this.SendProblemAsync(
                StatusCodes.Status503ServiceUnavailable, ErrorCodes.BillingNotConfigured, "Billing is not configured.", ct);
            return null;
        }

        var refundedMinorUnits = 0L;
        string? currency = null;

        try
        {
            if (pendingId is not null && pendingId != cancellableId)
            {
                var earlier = await subscriptionService.CancelNowAsync(pendingId, ct);
                refundedMinorUnits += earlier.RefundedMinorUnits;
                currency ??= earlier.Currency;
            }

            if (cancellableId is not null)
            {
                if (pendingId != cancellableId)
                {
                    await db.CoachSubscriptions
                        .Where(cs => cs.Id == subscriptionRowId)
                        .ExecuteUpdateAsync(setters => setters.SetProperty(cs => cs.PendingRefundSubscriptionId, cancellableId), ct);
                }

                var current = await subscriptionService.CancelNowAsync(cancellableId, ct);
                refundedMinorUnits += current.RefundedMinorUnits;
                currency ??= current.Currency;
            }
        }
        catch (BillingProviderException)
        {
            await this.SendProblemAsync(
                StatusCodes.Status502BadGateway, ErrorCodes.BillingProviderError,
                "The payment provider failed; the plan is unchanged and repeating the call retries the refund.", ct);
            return null;
        }

        return new CancelRefundResult(refundedMinorUnits, currency);
    }
}
