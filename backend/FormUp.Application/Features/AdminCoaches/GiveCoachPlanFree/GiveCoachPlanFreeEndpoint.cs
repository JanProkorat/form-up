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
                            "and the given plan includes both professional roles while it is in force (no role is removed when it ends). If Stripe fails nothing is saved and repeating the call is safe.";
            s.Response<GiveCoachPlanFreeResponse>(StatusCodes.Status200OK, "The plan as it now stands");
            s.Response<ProblemDetails>(StatusCodes.Status502BadGateway,
                "Stripe failed; nothing was saved (BILLING_PROVIDER_ERROR)");
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
            .Where(p => p.SubscriptionPlan.Code == req.PlanCode && p.ValidTo == null)
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
        // value loaded before the cancel. A refusal leaves the row untouched.
        var beforeCancel = await db.CoachSubscriptions
            .AsNoTracking()
            .FirstOrDefaultAsync(cs => cs.ProfessionalProfileId == profileId, ct);
        var cancelsAtProvider = beforeCancel?.HasCancellableProviderSubscription ?? false;
        var endedAddOn = beforeCancel?.PaysSecondRoleAddOn ?? false;
        var refund = CancelRefundResult.None;

        if (cancelsAtProvider)
        {
            var cancelled = await TryCancelAtProviderAsync(beforeCancel!.ExternalSubscriptionId!, ct);

            if (cancelled is null)
            {
                return;
            }

            refund = cancelled;
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
            ["providerSubscriptionCancelled"] = cancelsAtProvider,
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
    /// Cancels the subscription at Stripe now and refunds its unused days. Answers 503 or 502 and returns null when it
    /// could not, so the caller saves nothing.
    /// </summary>
    private async Task<CancelRefundResult?> TryCancelAtProviderAsync(string externalSubscriptionId, CancellationToken ct)
    {
        if (!subscriptionService.IsConfigured)
        {
            await this.SendProblemAsync(
                StatusCodes.Status503ServiceUnavailable, ErrorCodes.BillingNotConfigured, "Billing is not configured.", ct);
            return null;
        }

        try
        {
            return await subscriptionService.CancelNowAsync(externalSubscriptionId, ct);
        }
        catch (BillingProviderException)
        {
            await this.SendProblemAsync(
                StatusCodes.Status502BadGateway, ErrorCodes.BillingProviderError, "The payment provider failed; nothing was saved.", ct);
            return null;
        }
    }
}
