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
                            "notification and a subscriptionchanged event; a failure to notify does not fail the call. A coach with a " +
                            "Stripe subscription gets a 100 % discount there first (ending on FreeUntil); if Stripe fails nothing is " +
                            "saved. Without Stripe keys, or without a Stripe subscription, only the local change is made.";
            s.Response<GiveCoachPlanFreeResponse>(StatusCodes.Status200OK, "The plan as it now stands");
            s.Response<ProblemDetails>(StatusCodes.Status502BadGateway,
                "Stripe failed; nothing was saved (BILLING_PROVIDER_ERROR)");
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

        if (!await TryApplyGiftInStripeAsync(subscription, req.FreeUntil, ct))
        {
            await this.SendProblemAsync(
                StatusCodes.Status502BadGateway, ErrorCodes.BillingProviderError, "The payment provider failed; nothing was saved.", ct);
            return;
        }

        subscription.SubscriptionPlanId = openPrice.SubscriptionPlanId;
        subscription.SubscriptionPlanPriceId = openPrice.Id;
        subscription.PendingSubscriptionPlanPriceId = null;
        subscription.GivenFreeAt = now;
        subscription.FreeUntil = req.FreeUntil;
        subscription.Status = SubscriptionStatus.Active;

        await CoachLapseLinkReset.ResetAsync(db, profileId.Value, ct);
        await db.SaveChangesAsync(ct);

        var fields = new Dictionary<string, object?>
        {
            ["planCode"] = req.PlanCode,
            ["freeUntil"] = req.FreeUntil?.ToString("O"),
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
        }, ct);
    }

    /// <summary>
    /// Applies the 100 % discount at Stripe when the coach has a Stripe subscription. Returns false when Stripe failed, so
    /// the caller saves nothing. Without keys the call is skipped with a warning and the local change still happens.
    /// </summary>
    private async Task<bool> TryApplyGiftInStripeAsync(CoachSubscription subscription, DateTimeOffset? freeUntil, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(subscription.ExternalSubscriptionId))
        {
            return true;
        }

        if (!subscriptionService.IsConfigured)
        {
            logger.LogWarning("Stripe is not configured; the given plan is saved locally without a Stripe discount");
            return true;
        }

        try
        {
            await subscriptionService.ApplyGivenFreeAsync(subscription.ExternalSubscriptionId, freeUntil, ct);

            return true;
        }
        catch (BillingProviderException)
        {
            return false;
        }
    }
}
