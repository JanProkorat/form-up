using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Features.Billing.Shared;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Services.Billing;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.Billing.ChangePlan;

/// <summary>
/// Moves the caller's own live paid subscription to another paid plan. An upgrade applies now with proration; a
/// downgrade is scheduled for the next renewal. Our own row is written after Stripe succeeds.
/// </summary>
/// <param name="db">Application database context.</param>
/// <param name="subscriptions">The payment provider seam.</param>
/// <param name="timeProvider">Clock.</param>
[RemovedCoachRole(RemovedCoachRoleMode.Refuse)]
internal sealed class ChangePlanEndpoint(IApplicationDbContext db, ISubscriptionService subscriptions, TimeProvider timeProvider)
    : Endpoint<ChangePlanRequest, ChangePlanResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/billing/change-plan");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Change the plan of my paid subscription";
            s.Description = "Moves the caller's own live paid subscription to another paid plan without a new Checkout. A dearer " +
                            "plan applies now with proration; a cheaper or equal one is scheduled for the next renewal. A paid " +
                            "second-role add-on is carried to the new plan.";
            s.Response<ChangePlanResponse>(StatusCodes.Status200OK, "The change was applied or scheduled");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest,
                "Invalid body, or the plan cannot be bought: unknown, Free, inactive, not priced in Stripe, or it has no add-on price for a paid add-on (PLAN_NOT_PURCHASABLE)");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or unreadable caller claim");
            s.Response(StatusCodes.Status403Forbidden, "The caller is not a trainer or nutritionist, or the coach role was removed");
            s.Response(StatusCodes.Status409Conflict,
                "No live paid subscription (BILLING_NO_SUBSCRIPTION), an admin gift is in force (BILLING_GIFT_IN_FORCE), or the plan is the current one (BILLING_PLAN_UNCHANGED)");
            s.Response(StatusCodes.Status502BadGateway, "Stripe failed; nothing was saved (BILLING_PROVIDER_ERROR)");
            s.Response(StatusCodes.Status503ServiceUnavailable, "Stripe is not configured on this server (BILLING_NOT_CONFIGURED)");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(ChangePlanRequest req, CancellationToken ct)
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

        var target = await db.SubscriptionPlanPrices
            .AsNoTracking()
            .Where(p => p.SubscriptionPlan.Code == req.PlanCode && p.ValidTo == null)
            .Select(p => new TargetPlan(
                p.Id,
                p.SubscriptionPlanId,
                p.SubscriptionPlan.Code,
                p.SubscriptionPlan.IsActive,
                p.SubscriptionPlan.BothRolesIncluded,
                p.SubscriptionPlan.BillingInterval,
                p.PriceMinorUnits,
                p.SecondRoleAddOnPriceMinorUnits,
                p.Currency,
                p.ExternalPriceId))
            .FirstOrDefaultAsync(ct);

        if (target is null
            || target.Code == SubscriptionPlanCodes.Free
            || !target.IsActive
            || target.PriceMinorUnits <= 0
            || string.IsNullOrEmpty(target.ExternalPriceId))
        {
            this.ThrowErrorWithCode(ErrorCodes.PlanNotPurchasable, "This plan cannot be bought.");
            return;
        }

        var subscription = await this.LoadLiveSubscriptionOrRespondAsync(db, userId, timeProvider.GetUtcNow(), ct);

        if (subscription is null)
        {
            return;
        }

        if (subscription.SubscriptionPlanId == target.PlanId)
        {
            await this.SendProblemAsync(
                StatusCodes.Status409Conflict, ErrorCodes.BillingPlanUnchanged, "The coach is already on this plan.", ct);
            return;
        }

        var carryAddOn = subscription.PaysSecondRoleAddOn && !target.BothRolesIncluded;

        if (carryAddOn && target.AddOnAmount is null or <= 0)
        {
            this.ThrowErrorWithCode(ErrorCodes.PlanNotPurchasable, "This plan has no second-role add-on to carry over.");
            return;
        }

        var currentPerYear = subscription.SubscriptionPlanPrice.PriceMinorUnits
                             * (subscription.SubscriptionPlan.BillingInterval == BillingInterval.Annual ? 1 : 12);
        var targetPerYear = target.PriceMinorUnits * (target.BillingInterval == BillingInterval.Annual ? 1 : 12);
        var isUpgrade = targetPerYear > currentPerYear;

        try
        {
            await subscriptions.ChangePlanAsync(
                subscription.ExternalSubscriptionId!,
                target.Code,
                target.ExternalPriceId,
                carryAddOn ? target.AddOnAmount : null,
                target.Currency,
                target.BillingInterval,
                atNextRenewal: !isUpgrade,
                ct);
        }
        catch (BillingProviderException)
        {
            await this.RespondBillingProviderErrorAsync(ct);
            return;
        }

        // Our own row is written now; LastExternalEventAt stays, so the webhook that follows still applies.
        if (isUpgrade)
        {
            subscription.SubscriptionPlanId = target.PlanId;
            subscription.SubscriptionPlanPriceId = target.PriceId;
            subscription.PendingSubscriptionPlanPriceId = null;
            subscription.PaysSecondRoleAddOn = carryAddOn;
        }
        else
        {
            subscription.PendingSubscriptionPlanPriceId = target.PriceId;
        }

        await db.SaveChangesAsync(ct);

        await Send.OkAsync(
            new ChangePlanResponse
            {
                PlanCode = target.Code,
                Timing = isUpgrade ? PlanChangeTiming.Now : PlanChangeTiming.AtNextRenewal,
                EffectiveAt = isUpgrade ? timeProvider.GetUtcNow() : subscription.CurrentPeriodEndsAt,
            },
            ct);
    }

    private sealed record TargetPlan(
        long PriceId,
        long PlanId,
        string Code,
        bool IsActive,
        bool BothRolesIncluded,
        BillingInterval BillingInterval,
        long PriceMinorUnits,
        long? AddOnAmount,
        string Currency,
        string? ExternalPriceId);
}
