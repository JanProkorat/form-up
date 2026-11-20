using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.Users.Shared;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Services.Billing;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.Users.RemoveCoachRole;

/// <summary>
/// Marks one of the caller's coach roles as removed. The Identity role and link flags are untouched. When the coach pays
/// the second-role add-on, its removal is scheduled for the next renewal first (no refund; the paid period runs out).
/// </summary>
/// <param name="userManager">ASP.NET Identity user manager.</param>
/// <param name="db">Database context.</param>
/// <param name="coachRoleStatus">Resolves which coach roles are active.</param>
/// <param name="audit">Audit logging service.</param>
/// <param name="timeProvider">Clock.</param>
/// <param name="coachRoleRemover">Notifies the clients affected by the removal.</param>
/// <param name="subscriptions">The payment provider seam.</param>
/// <param name="logger">Logger.</param>
[RemovedCoachRole(RemovedCoachRoleMode.Exempt, Reason = "Account lifecycle action that must stay reachable after every role is removed.")]
internal sealed class RemoveCoachRoleEndpoint(
    UserManager<ApplicationUser> userManager,
    IApplicationDbContext db,
    ICoachRoleStatus coachRoleStatus,
    IAuditService audit,
    TimeProvider timeProvider,
    ICoachRoleRemover coachRoleRemover,
    ISubscriptionService subscriptions,
    ILogger<RemoveCoachRoleEndpoint> logger) : Endpoint<RemoveCoachRoleRequest, RemoveCoachRoleResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Delete("/users/me/roles/{Role}");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Remove a coach role";
            s.Description = "Marks the Trainer or Nutritionist role as removed. Rejected when it is the caller's only active coach role. " +
                            "A coach who pays the second-role add-on keeps paying it until the end of the paid period; its removal " +
                            "is scheduled at the payment provider first.";
            s.Response<RemoveCoachRoleResponse>(StatusCodes.Status200OK, "Role removed; counts as they stood before the removal");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest, "Invalid role, ROLE_NOT_ASSIGNED, or ONLY_COACH_ROLE");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or unreadable caller claim");
            s.Response(StatusCodes.Status502BadGateway, "Stripe failed; the role was not removed (BILLING_PROVIDER_ERROR)");
            s.Response(StatusCodes.Status503ServiceUnavailable, "The add-on is billed and Stripe is not configured on this server (BILLING_NOT_CONFIGURED)");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(RemoveCoachRoleRequest req, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(AppClaims.UserId), out var userId))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        // Handler-level mirror of the validator allow-list (see SelfAssignableRoles).
        if (!SelfAssignableRoles.Contains(req.Role))
        {
            ThrowError(r => r.Role, "Role is not self-managed.", 400);
            return;
        }

        var role = req.Role.Equals(AppRoles.Trainer, StringComparison.OrdinalIgnoreCase)
            ? AppRoles.Trainer
            : AppRoles.Nutritionist;

        var user = await userManager.FindByIdAsync(userId.ToString());
        var profile = await db.ProfessionalProfiles.FirstOrDefaultAsync(p => p.UserId == userId, ct);

        if (user is null || profile is null)
        {
            this.ThrowErrorWithCode(ErrorCodes.RoleNotAssigned, "You do not hold this role.");
            return;
        }

        var activeRoles = await coachRoleStatus.GetActiveRolesAsync(user, profile, ct);

        if (!activeRoles.Contains(role))
        {
            this.ThrowErrorWithCode(ErrorCodes.RoleNotAssigned, "You do not hold this role.");
            return;
        }

        if (activeRoles.Count == 1)
        {
            this.ThrowErrorWithCode(ErrorCodes.OnlyCoachRole, "This is your only coach role and cannot be removed.");
            return;
        }

        var counts = await CoachRoleCounts.LoadAsync(db, profile.Id, activeRoles, ct);

        // Stripe first: a role removed without its add-on scheduled would keep being billed with no marker on our side.
        var billedAddOn = await LoadBilledAddOnSubscriptionAsync(profile.Id, ct);

        if (billedAddOn is not null && !await TryScheduleAddOnRemovalAsync(billedAddOn, ct))
        {
            return;
        }

        var now = timeProvider.GetUtcNow();

        CoachRoleStatus.SetRemovedAt(profile, role, now.UtcDateTime);

        if (billedAddOn is not null)
        {
            billedAddOn.SecondRoleAddOnEndsAt = billedAddOn.CurrentPeriodEndsAt ?? now;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (Exception) when (billedAddOn is not null)
        {
            await TryUndoScheduledAddOnRemovalAsync(billedAddOn);
            throw;
        }

        await coachRoleRemover.NotifyClientsAsync(user, profile.Id, [role], ct);

        await audit.LogAsync(
            userId,
            "RemoveCoachRole",
            nameof(ApplicationUser),
            userId,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            newValues: $"{{\"removedRole\":\"{role}\"}}",
            ct: ct);

        await Send.OkAsync(new RemoveCoachRoleResponse { RemovedRole = role, Roles = counts }, ct);
    }

    /// <summary>
    /// The caller's subscription row when removing a role must schedule the add-on's end: a live provider subscription that
    /// pays the add-on on a plan without both roles, is not ending anyway and has no removal scheduled yet. Otherwise null.
    /// </summary>
    private async Task<CoachSubscription?> LoadBilledAddOnSubscriptionAsync(long profileId, CancellationToken ct)
    {
        var subscription = await db.CoachSubscriptions
            .Include(cs => cs.SubscriptionPlan)
            .Include(cs => cs.SubscriptionPlanPrice)
            .Include(cs => cs.PendingSubscriptionPlanPrice).ThenInclude(price => price!.SubscriptionPlan)
            .FirstOrDefaultAsync(cs => cs.ProfessionalProfileId == profileId, ct);

        return subscription is { PaysSecondRoleAddOn: true, CancelAtPeriodEnd: false, SecondRoleAddOnEndsAt: null }
               && subscription.HasLiveProviderSubscription
               && !subscription.SubscriptionPlan.BothRolesIncluded
            ? subscription
            : null;
    }

    /// <summary>
    /// Schedules the add-on's removal at the next renewal on the plan that applies then (the pending downgrade when there
    /// is one, else the current plan). Answers 503 or 502 and returns false when it could not.
    /// </summary>
    private async Task<bool> TryScheduleAddOnRemovalAsync(CoachSubscription subscription, CancellationToken ct)
    {
        if (!subscriptions.IsConfigured)
        {
            await this.SendProblemAsync(
                StatusCodes.Status503ServiceUnavailable, ErrorCodes.BillingNotConfigured, "Billing is not configured.", ct);
            return false;
        }

        var renewalPrice = subscription.PendingSubscriptionPlanPrice ?? subscription.SubscriptionPlanPrice;
        var renewalPlan = subscription.PendingSubscriptionPlanPrice?.SubscriptionPlan ?? subscription.SubscriptionPlan;

        try
        {
            if (renewalPrice.ExternalPriceId is null)
            {
                throw new BillingProviderException("The plan price is not set up at the provider.");
            }

            await subscriptions.ChangePlanAsync(
                subscription.ExternalSubscriptionId!,
                renewalPlan.Code,
                renewalPrice.ExternalPriceId,
                null,
                renewalPrice.Currency,
                renewalPrice.BillingInterval,
                atNextRenewal: true,
                ct);
        }
        catch (BillingProviderException)
        {
            await this.SendProblemAsync(
                StatusCodes.Status502BadGateway, ErrorCodes.BillingProviderError, "The payment provider failed.", ct);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Puts the provider schedule back as it was after the role removal failed to save, so the add-on keeps being billed
    /// with no marker on our side. Best effort: a failure is logged with ids only and never replaces the original error.
    /// </summary>
    private async Task TryUndoScheduledAddOnRemovalAsync(CoachSubscription subscription)
    {
        try
        {
            if (subscription.PendingSubscriptionPlanPrice is { ExternalPriceId: { } pendingPriceId } pending)
            {
                var pendingPlan = pending.SubscriptionPlan;
                long? addOnAmount = pendingPlan.BothRolesIncluded || pending.SecondRoleAddOnPriceMinorUnits is null or <= 0
                    ? null
                    : pending.SecondRoleAddOnPriceMinorUnits;

                await subscriptions.ChangePlanAsync(
                    subscription.ExternalSubscriptionId!,
                    pendingPlan.Code,
                    pendingPriceId,
                    addOnAmount,
                    pending.Currency,
                    pending.BillingInterval,
                    atNextRenewal: true,
                    CancellationToken.None);
            }
            else
            {
                await subscriptions.ClearScheduledPriceChangeAsync(subscription.ExternalSubscriptionId!, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "Undoing the scheduled add-on removal of subscription {SubscriptionId} failed", subscription.ExternalSubscriptionId);
        }
    }
}
