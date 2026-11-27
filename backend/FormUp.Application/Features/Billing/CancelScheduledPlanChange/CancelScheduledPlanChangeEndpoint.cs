using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Features.Billing.Shared;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Services.Billing;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.Billing.CancelScheduledPlanChange;

/// <summary>
/// Cancels the caller's own scheduled plan change: a downgrade waiting for the next renewal, or their own switch to Free.
/// Stripe is called first; our row is written once, after it succeeded. Nothing is charged.
/// </summary>
/// <param name="db">Application database context.</param>
/// <param name="subscriptions">The payment provider seam.</param>
/// <param name="timeProvider">Clock.</param>
/// <param name="coachRoleStatus">Resolves the coach roles that are not removed.</param>
/// <param name="logger">Logger.</param>
[RemovedCoachRole(RemovedCoachRoleMode.Refuse)]
internal sealed class CancelScheduledPlanChangeEndpoint(
    IApplicationDbContext db,
    ISubscriptionService subscriptions,
    TimeProvider timeProvider,
    ICoachRoleStatus coachRoleStatus,
    ILogger<CancelScheduledPlanChangeEndpoint> logger)
    : EndpointWithoutRequest<CancelScheduledPlanChangeResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/billing/scheduled-change/cancel");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Cancel my scheduled plan change";
            s.Description = "Undoes the caller's own scheduled downgrade or switch to Free, so the subscription renews on the current " +
                            "plan. Nothing is charged. A renewal cancelled by Form Up, or a pending coach-account disable, cannot be " +
                            "undone here.";
            s.Response<CancelScheduledPlanChangeResponse>(StatusCodes.Status200OK, "The scheduled change is cancelled");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or unreadable caller claim");
            s.Response(StatusCodes.Status403Forbidden, "The caller is not a trainer or nutritionist, or the coach role was removed");
            s.Response(StatusCodes.Status409Conflict,
                "No live paid subscription (BILLING_NO_SUBSCRIPTION), an admin gift is in force (BILLING_GIFT_IN_FORCE), the coach account is being disabled (BILLING_ACCOUNT_DISABLING), Form Up cancelled the renewal (BILLING_RENEWAL_CANCELLED_BY_ADMIN), or nothing is scheduled (BILLING_NOTHING_SCHEDULED)");
            s.Response(StatusCodes.Status502BadGateway, "Stripe failed; nothing was saved (BILLING_PROVIDER_ERROR)");
            s.Response(StatusCodes.Status503ServiceUnavailable, "Stripe is not configured on this server (BILLING_NOT_CONFIGURED)");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(AppClaims.UserId), out var userId))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        if (await this.RespondIfBillingNotConfiguredAsync(subscriptions, ct))
        {
            return;
        }

        var subscription = await this.LoadLiveSubscriptionOrRespondAsync(db, userId, timeProvider.GetUtcNow(), ct);

        if (subscription is null || await RespondIfNotCancellableAsync(subscription, userId, ct))
        {
            return;
        }

        var kind = subscription.CancelAtPeriodEnd ? ScheduledPlanChangeKind.SwitchToFree : ScheduledPlanChangeKind.Downgrade;
        var cancelled = kind == ScheduledPlanChangeKind.SwitchToFree
            ? await TryCancelSwitchToFreeAsync(subscription, userId, ct)
            : await TryCancelDowngradeAsync(subscription, ct);

        if (!cancelled)
        {
            await this.RespondBillingProviderErrorAsync(ct);
            return;
        }

        await db.SaveChangesAsync(ct);

        await Send.OkAsync(
            new CancelScheduledPlanChangeResponse
            {
                Kind = kind,
                PlanCode = subscription.SubscriptionPlan.Code,
                RenewsAt = subscription.CurrentPeriodEndsAt,
            },
            ct);
    }

    /// <summary>
    /// Answers 409 when the account is being disabled, an admin cancelled the renewal, or nothing is scheduled. Returns true
    /// when it answered, so the caller returns.
    /// </summary>
    private async Task<bool> RespondIfNotCancellableAsync(CoachSubscription subscription, Guid userId, CancellationToken ct)
    {
        var disabling = await db.ProfessionalProfiles
            .AsNoTracking()
            .AnyAsync(p => p.UserId == userId && p.CoachAccountActiveUntil != null, ct);

        if (disabling)
        {
            await this.SendProblemAsync(
                StatusCodes.Status409Conflict,
                ErrorCodes.BillingAccountDisabling,
                "The coach account is being disabled; keep the account instead.",
                ct);
            return true;
        }

        if (subscription.RenewalCancelledByAdminAt is not null)
        {
            await this.SendProblemAsync(
                StatusCodes.Status409Conflict,
                ErrorCodes.BillingRenewalCancelledByAdmin,
                "Form Up cancelled the renewal; it cannot be undone here.",
                ct);
            return true;
        }

        if (!subscription.CancelAtPeriodEnd && subscription.PendingSubscriptionPlanPriceId is null)
        {
            await this.SendProblemAsync(
                StatusCodes.Status409Conflict, ErrorCodes.BillingNothingScheduled, "No plan change is scheduled.", ct);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Drops the scheduled downgrade at Stripe and on the row. When a second-role removal is scheduled too, the schedule is
    /// rewritten on the current plan without the add-on instead, so that removal survives. False on a provider failure.
    /// </summary>
    private async Task<bool> TryCancelDowngradeAsync(CoachSubscription subscription, CancellationToken ct)
    {
        try
        {
            if (subscription.SecondRoleAddOnEndsAt is null)
            {
                await subscriptions.ClearScheduledPriceChangeAsync(subscription.ExternalSubscriptionId!, ct);
            }
            else
            {
                await ScheduleAddOnRemovalOnCurrentPlanAsync(subscription, ct);
            }
        }
        catch (BillingProviderException)
        {
            return false;
        }

        subscription.PendingSubscriptionPlanPriceId = null;

        return true;
    }

    /// <summary>
    /// Resumes the renewal at Stripe. When a role was removed while the switch was pending, nothing scheduled the add-on's
    /// end, so it is scheduled now; if that fails the resume is undone and the row stays as it was. False on a provider failure.
    /// </summary>
    private async Task<bool> TryCancelSwitchToFreeAsync(CoachSubscription subscription, Guid userId, CancellationToken ct)
    {
        try
        {
            await subscriptions.ResumeRenewalAsync(subscription.ExternalSubscriptionId!, ct);
        }
        catch (BillingProviderException)
        {
            return false;
        }

        subscription.CancelAtPeriodEnd = false;

        if (!subscription.PaysSecondRoleAddOn
            || subscription.SubscriptionPlan.BothRolesIncluded
            || subscription.SecondRoleAddOnEndsAt is not null
            || (await coachRoleStatus.GetActiveRolesAsync(userId, ct)).Count >= 2)
        {
            return true;
        }

        try
        {
            await ScheduleAddOnRemovalOnCurrentPlanAsync(subscription, ct);
        }
        catch (BillingProviderException)
        {
            subscription.CancelAtPeriodEnd = true;
            await TryUndoResumeAsync(subscription);

            return false;
        }

        subscription.SecondRoleAddOnEndsAt = subscription.CurrentPeriodEndsAt ?? timeProvider.GetUtcNow();

        return true;
    }

    private async Task ScheduleAddOnRemovalOnCurrentPlanAsync(CoachSubscription subscription, CancellationToken ct)
    {
        var price = subscription.SubscriptionPlanPrice;

        if (price.ExternalPriceId is null)
        {
            throw new BillingProviderException("The plan price is not set up at the provider.");
        }

        await subscriptions.ChangePlanAsync(
            subscription.ExternalSubscriptionId!,
            subscription.SubscriptionPlan.Code,
            price.ExternalPriceId,
            null,
            price.Currency,
            price.BillingInterval,
            atNextRenewal: true,
            ct);
    }

    /// <summary>
    /// Puts the end of the subscription back after the add-on removal failed. Best effort: a failure is logged with ids only
    /// and never replaces the original error.
    /// </summary>
    private async Task TryUndoResumeAsync(CoachSubscription subscription)
    {
        try
        {
            await subscriptions.CancelAtPeriodEndAsync(subscription.ExternalSubscriptionId!, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "Undoing the resumed renewal of subscription {SubscriptionId} failed", subscription.ExternalSubscriptionId);
        }
    }
}
