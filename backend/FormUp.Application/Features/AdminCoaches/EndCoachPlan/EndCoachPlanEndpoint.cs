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

namespace FormUp.Application.Features.AdminCoaches.EndCoachPlan;

/// <summary>
/// Ends a coach's paid or given plan now, so the coach falls back to the Free plan exactly like a lapsed coach.
/// </summary>
/// <param name="db">Application database context.</param>
/// <param name="audit">Audit logging service.</param>
/// <param name="timeProvider">Clock.</param>
/// <param name="notifications">Notification service.</param>
/// <param name="notifier">Realtime notifier.</param>
/// <param name="logger">Logger.</param>
/// <param name="subscriptions">The payment provider seam.</param>
internal sealed class EndCoachPlanEndpoint(
    IApplicationDbContext db,
    IAuditService audit,
    TimeProvider timeProvider,
    INotificationService notifications,
    IRealtimeNotifier notifier,
    ILogger<EndCoachPlanEndpoint> logger,
    ISubscriptionService subscriptions)
    : Endpoint<EndCoachPlanRequest, EndCoachPlanResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/admin/coaches/{CoachId}/end-plan");
        Roles(AppRoles.Admin);
        Summary(s =>
        {
            s.Summary = "End a coach's plan now";
            s.Description = "Ends a paying coach's plan (including payment failed and cancelling) or a running gift at once. The coach " +
                            "becomes Free like a lapsed coach: the plan code stays, the status is Canceled, the pending price change, " +
                            "gift dates and cancel-at-period-end flag are cleared, and the kept/released client choice is reset. The " +
                            "reason, ended plan code, whether it was a gift and the refunded amount are recorded in the coach's " +
                            "history. A live Stripe subscription is cancelled in Stripe first and at once, and the unused days of the paid " +
                            "period are refunded to the card (nothing for a payment that never went through or a 100 % discount); when " +
                            "Stripe cannot do it the plan is unchanged, the refund stays owed and repeating the call pays it once, even " +
                            "after Stripe's webhook already ended the plan (answered 200 with the refund). The paid period ends now, so disabling " +
                            "the account afterwards takes effect right away. An already scheduled account disable is left unchanged.";
            s.Response<EndCoachPlanResponse>(StatusCodes.Status200OK, "The plan that was ended");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest, "Invalid request body");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or invalid credentials");
            s.Response(StatusCodes.Status403Forbidden, "Caller is not an Admin");
            s.Response(StatusCodes.Status404NotFound, "The coach is unknown (PROFESSIONAL_NOT_FOUND)");
            s.Response<ProblemDetails>(StatusCodes.Status409Conflict,
                "No plan to end: no subscription, the Free plan, an ended paid plan, or a gift that has expired, and no refund left owed by an earlier attempt (NO_PLAN_TO_END)");
            s.Response(StatusCodes.Status502BadGateway, "Stripe failed; the plan is unchanged and the refund stays owed for a retry (BILLING_PROVIDER_ERROR)");
            s.Response(StatusCodes.Status503ServiceUnavailable, "The coach has a live Stripe subscription and Stripe is not configured on this server (BILLING_NOT_CONFIGURED)");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(EndCoachPlanRequest req, CancellationToken ct)
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

        var now = timeProvider.GetUtcNow();
        var endable = await db.ProfessionalProfiles
            .AsNoTracking()
            .Where(p => p.Id == profileId)
            .AnyAsync(CoachSubscriptionStatusClassifier.Matches(AdminCoachStatusFilter.Paying, now), ct)
            || await db.ProfessionalProfiles
                .AsNoTracking()
                .Where(p => p.Id == profileId)
                .AnyAsync(CoachSubscriptionStatusClassifier.Matches(AdminCoachStatusFilter.GivenFree, now), ct);

        var before = await db.CoachSubscriptions
            .AsNoTracking()
            .FirstOrDefaultAsync(cs => cs.ProfessionalProfileId == profileId, ct);

        // A refund an earlier attempt left owed (marker) still gets through once the webhook ended the plan.
        if (!endable && before?.PendingRefundSubscriptionId is null)
        {
            await this.SendProblemAsync(
                StatusCodes.Status409Conflict, ErrorCodes.NoPlanToEnd, "The coach has no paid or given plan to end.", ct);
            return;
        }

        var markerOnly = !endable;
        var cancelsAtProvider = !markerOnly && before!.HasLiveProviderSubscription;
        var pendingId = before!.PendingRefundSubscriptionId;
        var cancellableId = cancelsAtProvider ? before.ExternalSubscriptionId : null;
        var settlesAtProvider = pendingId is not null || cancellableId is not null;

        // Stripe first: the owed-refund marker is written before the cancel, so a failed refund stays retryable. The row is
        // tracked only afterwards, so the marker cleared below is compared with what is stored now.
        var refund = CancelRefundResult.None;

        if (settlesAtProvider)
        {
            var settled = await TrySettleAtProviderAsync(before.Id, pendingId, cancellableId, ct);

            if (settled is null)
            {
                return;
            }

            refund = settled;
        }

        var subscription = await db.CoachSubscriptions
            .Include(cs => cs.SubscriptionPlan)
            .FirstAsync(cs => cs.ProfessionalProfileId == profileId, ct);
        var planCode = subscription.SubscriptionPlan.Code;
        var wasGivenFree = subscription.GivenFreeAt is not null;
        subscription.PendingRefundSubscriptionId = null;

        // A marker-only retry only pays the refund: the end state, the kept/released choice and the notification stay as
        // the first attempt and the webhook left them.
        if (!markerOnly)
        {
            // Keeps the paid plan code so the coach reads as lapsed, with the Free cap applied to their clients.
            subscription.Status = SubscriptionStatus.Canceled;
            subscription.PendingSubscriptionPlanPriceId = null;
            subscription.GivenFreeAt = null;
            subscription.FreeUntil = null;
            subscription.CancelAtPeriodEnd = false;
            subscription.RenewalCancelledByAdminAt = null;

            // The paid period ends now: the active-until date and the lapse "ended on" date read it.
            subscription.CurrentPeriodEndsAt = now;
            subscription.PaysSecondRoleAddOn = false;
            subscription.SecondRoleAddOnEndsAt = null;

            if (cancelsAtProvider)
            {
                // Same end state as the deleted webhook; the event time lets a late older update be skipped as stale.
                subscription.ExternalSubscriptionId = null;
                subscription.NextPaymentAttemptAt = null;

                // Read after the cancel and never behind the event time the deleted webhook may have stored meanwhile.
                var storedEventAt = await db.CoachSubscriptions
                    .AsNoTracking()
                    .Where(cs => cs.Id == subscription.Id)
                    .Select(cs => cs.LastExternalEventAt)
                    .FirstAsync(ct);
                var afterCancel = timeProvider.GetUtcNow();
                var eventAt = new DateTimeOffset(afterCancel.Ticks - (afterCancel.Ticks % TimeSpan.TicksPerSecond), afterCancel.Offset);
                subscription.LastExternalEventAt = storedEventAt is { } stored && stored > eventAt ? stored : eventAt;
            }

            await CoachLapseLinkReset.ResetAsync(db, profileId.Value, ct);
        }

        await db.SaveChangesAsync(ct);

        var fields = new Dictionary<string, object?>
        {
            ["planCode"] = planCode,
            ["wasGivenFree"] = wasGivenFree,
            ["providerSubscriptionCancelled"] = settlesAtProvider,
            ["refundedMinorUnits"] = refund.RefundedMinorUnits,
            ["refundCurrency"] = refund.Currency,
        };

        await audit.LogAsync(
            adminId,
            AdminCoachAuditActions.EndPlan,
            nameof(ApplicationUser),
            req.CoachId,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            newValues: AuditPayload.WithReason(fields, req.Reason.Trim()),
            ct: ct);

        if (!markerOnly)
        {
            await CoachPlanNotifier.TryNotifyAsync(
                db, notifications, notifier, logger, req.CoachId, subscription.SubscriptionPlanId,
                CoachPlanChangeAction.Ended, null, ct);
        }

        await Send.OkAsync(new EndCoachPlanResponse
        {
            CoachId = req.CoachId,
            PlanCode = planCode,
            WasGivenFree = wasGivenFree,
            RefundedMinorUnits = refund.RefundedMinorUnits > 0 && refund.Currency is not null
                ? refund.RefundedMinorUnits
                : null,
            RefundCurrency = refund.RefundedMinorUnits > 0 ? refund.Currency?.ToUpperInvariant() : null,
        }, ct);
    }

    /// <summary>
    /// Settles the owed-refund marker id and the live subscription id at the payment provider, each at most once: cancels now and
    /// refunds unused days (an already-cancelled subscription is only refunded). The live id is written to the marker
    /// first, so a retry can still refund after the webhook cleared it. Answers 503 or 502 and returns null when it could
    /// not.
    /// </summary>
    private async Task<CancelRefundResult?> TrySettleAtProviderAsync(
        long subscriptionRowId, string? pendingId, string? cancellableId, CancellationToken ct)
    {
        if (!subscriptions.IsConfigured)
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
                var earlier = await subscriptions.CancelNowAsync(pendingId, ct);
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

                var current = await subscriptions.CancelNowAsync(cancellableId, ct);
                refundedMinorUnits += current.RefundedMinorUnits;
                currency ??= current.Currency;
            }
        }
        catch (BillingProviderException)
        {
            await this.SendProblemAsync(
                StatusCodes.Status502BadGateway, ErrorCodes.BillingProviderError,
                "The payment provider failed; repeating the call retries the refund.", ct);
            return null;
        }

        return new CancelRefundResult(refundedMinorUnits, currency);
    }
}
