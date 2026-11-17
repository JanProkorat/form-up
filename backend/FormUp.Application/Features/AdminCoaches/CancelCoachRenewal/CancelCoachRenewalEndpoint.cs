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

namespace FormUp.Application.Features.AdminCoaches.CancelCoachRenewal;

/// <summary>
/// Cancels a paying coach's renewal: the plan runs to the end of the paid period and then ends.
/// </summary>
/// <param name="db">Application database context.</param>
/// <param name="audit">Audit logging service.</param>
/// <param name="timeProvider">Clock.</param>
/// <param name="notifications">Notification service.</param>
/// <param name="notifier">Realtime notifier.</param>
/// <param name="logger">Logger.</param>
/// <param name="subscriptions">The payment provider seam.</param>
internal sealed class CancelCoachRenewalEndpoint(
    IApplicationDbContext db,
    IAuditService audit,
    TimeProvider timeProvider,
    INotificationService notifications,
    IRealtimeNotifier notifier,
    ILogger<CancelCoachRenewalEndpoint> logger,
    ISubscriptionService subscriptions)
    : Endpoint<CancelCoachRenewalRequest, CancelCoachRenewalResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/admin/coaches/{CoachId}/cancel-renewal");
        Roles(AppRoles.Admin);
        Summary(s =>
        {
            s.Summary = "Cancel a coach's renewal";
            s.Description = "Marks a paying coach's subscription as not renewing, so it stops at the end of the paid period. The reason is " +
                            "recorded in the coach's history. Repeating the call returns 200 without a second history entry.";
            s.Response<CancelCoachRenewalResponse>(StatusCodes.Status200OK, "The renewal is cancelled");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest, "Invalid request body");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or invalid credentials");
            s.Response(StatusCodes.Status403Forbidden, "Caller is not an Admin");
            s.Response(StatusCodes.Status404NotFound, "The coach is unknown (PROFESSIONAL_NOT_FOUND)");
            s.Response<ProblemDetails>(StatusCodes.Status409Conflict,
                "Nothing renews: no subscription, the Free plan, a given-free plan, or a plan that no longer applies (NO_RENEWING_SUBSCRIPTION)");
            s.Response(StatusCodes.Status502BadGateway, "Stripe failed; nothing was saved (BILLING_PROVIDER_ERROR)");
            s.Response(StatusCodes.Status503ServiceUnavailable, "The coach has a live Stripe subscription and Stripe is not configured on this server (BILLING_NOT_CONFIGURED)");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(CancelCoachRenewalRequest req, CancellationToken ct)
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

        var renews = await db.ProfessionalProfiles
            .AsNoTracking()
            .Where(p => p.Id == profileId)
            .AnyAsync(CoachSubscriptionStatusClassifier.Matches(AdminCoachStatusFilter.Paying, timeProvider.GetUtcNow()), ct);

        if (!renews)
        {
            await this.SendProblemAsync(
                StatusCodes.Status409Conflict, ErrorCodes.NoRenewingSubscription, "The coach has no subscription that renews.", ct);
            return;
        }

        var subscription = await db.CoachSubscriptions
            .FirstAsync(cs => cs.ProfessionalProfileId == profileId, ct);

        var wasCancelling = subscription.CancelAtPeriodEnd;
        var cancelsAtProvider = !wasCancelling && subscription.HasLiveProviderSubscription;

        // Stripe first: a refusal leaves the row untouched, so a later save cannot persist half a cancellation.
        if (cancelsAtProvider && !await TryCancelAtProviderAsync(subscription.ExternalSubscriptionId!, ct))
        {
            return;
        }

        // An admin call also locks a cancellation the coach scheduled themselves.
        subscription.RenewalCancelledByAdminAt ??= timeProvider.GetUtcNow();

        if (!wasCancelling)
        {
            subscription.CancelAtPeriodEnd = true;

            if (cancelsAtProvider)
            {
                // The provider drops a scheduled plan change when it cancels the subscription.
                subscription.PendingSubscriptionPlanPriceId = null;
                subscription.SecondRoleAddOnEndsAt = null;
            }

            await db.SaveChangesAsync(ct);

            var fields = new Dictionary<string, object?>
            {
                ["periodEndsAt"] = subscription.CurrentPeriodEndsAt?.ToString("O"),
            };

            await audit.LogAsync(
                adminId,
                AdminCoachAuditActions.CancelRenewal,
                nameof(ApplicationUser),
                req.CoachId,
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                newValues: AuditPayload.WithReason(fields, req.Reason.Trim()),
                ct: ct);

            await CoachPlanNotifier.TryNotifyAsync(
                db, notifications, notifier, logger, req.CoachId, subscription.SubscriptionPlanId,
                CoachPlanChangeAction.RenewalCancelled, subscription.CurrentPeriodEndsAt, ct);

            await Send.OkAsync(new CancelCoachRenewalResponse
            {
                CoachId = req.CoachId,
                CurrentPeriodEndsAt = subscription.CurrentPeriodEndsAt,
            }, ct);
            return;
        }

        await db.SaveChangesAsync(ct);

        await Send.OkAsync(new CancelCoachRenewalResponse
        {
            CoachId = req.CoachId,
            CurrentPeriodEndsAt = subscription.CurrentPeriodEndsAt,
            AlreadyCancelled = true,
        }, ct);
    }

    /// <summary>
    /// Asks the payment provider to end the subscription at the period end. Answers 503 or 502 and returns false when it could not.
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
            await subscriptions.CancelAtPeriodEndAsync(externalSubscriptionId, ct);
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
