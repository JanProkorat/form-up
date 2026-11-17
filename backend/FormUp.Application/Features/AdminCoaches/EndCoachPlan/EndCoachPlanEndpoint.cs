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
                            "reason, ended plan code and whether it was a gift are recorded in the coach's history. A live Stripe " +
                            "subscription is cancelled in Stripe first and at once, with no refund and no credit for unused time; when " +
                            "Stripe cannot do it nothing changes. The paid period ends now, so disabling the account afterwards takes " +
                            "effect right away. An already scheduled account disable is left unchanged.";
            s.Response<EndCoachPlanResponse>(StatusCodes.Status200OK, "The plan that was ended");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest, "Invalid request body");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or invalid credentials");
            s.Response(StatusCodes.Status403Forbidden, "Caller is not an Admin");
            s.Response(StatusCodes.Status404NotFound, "The coach is unknown (PROFESSIONAL_NOT_FOUND)");
            s.Response<ProblemDetails>(StatusCodes.Status409Conflict,
                "No plan to end: no subscription, the Free plan, an ended paid plan, or a gift that has expired (NO_PLAN_TO_END)");
            s.Response(StatusCodes.Status502BadGateway, "Stripe failed; nothing was saved (BILLING_PROVIDER_ERROR)");
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

        if (!endable)
        {
            await this.SendProblemAsync(
                StatusCodes.Status409Conflict, ErrorCodes.NoPlanToEnd, "The coach has no paid or given plan to end.", ct);
            return;
        }

        var subscription = await db.CoachSubscriptions
            .Include(cs => cs.SubscriptionPlan)
            .FirstAsync(cs => cs.ProfessionalProfileId == profileId, ct);
        var planCode = subscription.SubscriptionPlan.Code;
        var wasGivenFree = subscription.GivenFreeAt is not null;
        var cancelsAtProvider = subscription.HasLiveProviderSubscription;

        // Stripe first: a refusal leaves the row untouched, so a later save cannot persist half an ending.
        if (cancelsAtProvider && !await TryCancelAtProviderAsync(subscription.ExternalSubscriptionId!, ct))
        {
            return;
        }

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
            subscription.LastExternalEventAt = new DateTimeOffset(now.Ticks - (now.Ticks % TimeSpan.TicksPerSecond), now.Offset);
        }

        await CoachLapseLinkReset.ResetAsync(db, profileId.Value, ct);
        await db.SaveChangesAsync(ct);

        var fields = new Dictionary<string, object?>
        {
            ["planCode"] = planCode,
            ["wasGivenFree"] = wasGivenFree,
            ["providerSubscriptionCancelled"] = cancelsAtProvider,
        };

        await audit.LogAsync(
            adminId,
            AdminCoachAuditActions.EndPlan,
            nameof(ApplicationUser),
            req.CoachId,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            newValues: AuditPayload.WithReason(fields, req.Reason.Trim()),
            ct: ct);

        await CoachPlanNotifier.TryNotifyAsync(
            db, notifications, notifier, logger, req.CoachId, subscription.SubscriptionPlanId,
            CoachPlanChangeAction.Ended, null, ct);

        await Send.OkAsync(new EndCoachPlanResponse
        {
            CoachId = req.CoachId,
            PlanCode = planCode,
            WasGivenFree = wasGivenFree,
        }, ct);
    }

    /// <summary>
    /// Cancels the subscription at the payment provider now. Answers 503 or 502 and returns false when it could not.
    /// </summary>
    private async Task<bool> TryCancelAtProviderAsync(string externalSubscriptionId, CancellationToken ct)
    {
        if (!subscriptions.IsConfigured)
        {
            await this.SendProblemAsync(
                StatusCodes.Status503ServiceUnavailable, ErrorCodes.BillingNotConfigured, "Billing is not configured.", ct);
            return false;
        }

        try
        {
            await subscriptions.CancelNowAsync(externalSubscriptionId, ct);
        }
        catch (BillingProviderException)
        {
            await this.SendProblemAsync(
                StatusCodes.Status502BadGateway, ErrorCodes.BillingProviderError, "The payment provider failed.", ct);
            return false;
        }

        return true;
    }
}
