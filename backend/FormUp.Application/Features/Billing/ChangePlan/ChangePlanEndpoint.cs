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

namespace FormUp.Application.Features.Billing.ChangePlan;

/// <summary>
/// Moves the caller's own live paid subscription to another paid plan. An upgrade applies now and charges the prorated
/// difference immediately (and drops a scheduled switch to Free); a downgrade is scheduled for the next renewal. Our own row is
/// written after Stripe succeeds.
/// </summary>
/// <param name="db">Application database context.</param>
/// <param name="subscriptions">The payment provider seam.</param>
/// <param name="timeProvider">Clock.</param>
/// <param name="coachRoleStatus">Resolves the coach roles that are not removed.</param>
/// <param name="coachEvents">Records an upgrade that applies now in the same save as the row change.</param>
[RemovedCoachRole(RemovedCoachRoleMode.Refuse)]
internal sealed class ChangePlanEndpoint(
    IApplicationDbContext db,
    ISubscriptionService subscriptions,
    TimeProvider timeProvider,
    ICoachRoleStatus coachRoleStatus,
    ICoachEventRecorder coachEvents)
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
                            "plan applies now and its prorated difference is charged at once (a scheduled switch to Free is dropped); a card " +
                            "that needs authentication answers AwaitingPayment with Stripe's hosted payment page in PaymentUrl and the plan " +
                            "changes only once the coach pays there; a cheaper " +
                            "or equal one is scheduled for the next renewal. BillingInterval picks the target's cadence (Monthly when omitted): " +
                            "annual to monthly always waits for the renewal, monthly to annual applies now for the same plan or a plan " +
                            "that is not a step down, and a switch that waits replaces an earlier scheduled one. A coach " +
                            "with two active roles pays the second-role add-on on a plan that does not include both roles.";
            s.Response<ChangePlanResponse>(StatusCodes.Status200OK, "The change was applied or scheduled, or awaits payment on the hosted page (Timing AwaitingPayment, PaymentUrl set)");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest,
                "Invalid body (including a missing AttemptId), or the plan cannot be bought: unknown, Free, inactive, no open price for the interval, not priced in Stripe, or the caller has two active roles and the plan has no add-on price (PLAN_NOT_PURCHASABLE)");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or unreadable caller claim");
            s.Response(StatusCodes.Status403Forbidden, "The caller is not a trainer or nutritionist, or the coach role was removed");
            s.Response(StatusCodes.Status409Conflict,
                "No live paid subscription (BILLING_NO_SUBSCRIPTION), an admin gift is in force (BILLING_GIFT_IN_FORCE), or the plan is the current one at the current interval (BILLING_PLAN_UNCHANGED)");
            s.Response(StatusCodes.Status502BadGateway, "Stripe failed or the card was declined; nothing was changed or saved (BILLING_PROVIDER_ERROR)");
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

        var target = await this.LoadPlanChangeTargetOrRespondAsync(db, req.PlanCode, req.BillingInterval, ct);

        if (target is null)
        {
            return;
        }

        var subscription = await this.LoadLiveSubscriptionOrRespondAsync(db, userId, timeProvider.GetUtcNow(), ct);

        if (subscription is null)
        {
            return;
        }

        var decision = await this.DecidePlanChangeOrRespondAsync(db, coachRoleStatus, userId, subscription, target, ct);

        if (decision is null)
        {
            return;
        }

        var upgradeKey = $"formup-plan-upgrade-{subscription.ExternalSubscriptionId}-{target.ExternalPriceId}-{req.AttemptId:N}";
        PlanUpgradeOutcome? upgrade = null;

        try
        {
            if (decision.IsUpgrade)
            {
                upgrade = await subscriptions.UpgradePlanNowAsync(
                    subscription.ExternalSubscriptionId!,
                    target.Code,
                    target.ExternalPriceId!,
                    decision.CarryAddOn ? target.AddOnAmount : null,
                    target.Currency,
                    target.BillingInterval,
                    upgradeKey,
                    ct,
                    await PlanChangeEndpointExtensions.ResolveDiscountChangeAsync(db, subscription, target, ct));
            }
            else
            {
                await subscriptions.ChangePlanAsync(
                    subscription.ExternalSubscriptionId!,
                    target.Code,
                    target.ExternalPriceId!,
                    decision.CarryAddOn ? target.AddOnAmount : null,
                    target.Currency,
                    target.BillingInterval,
                    atNextRenewal: true,
                    ct,
                    pendingPhaseCouponId: await PlanChangeEndpointExtensions.ResolvePendingPhaseCouponIdAsync(db, subscription, target, ct));
            }
        }
        catch (BillingProviderException)
        {
            await this.RespondBillingProviderErrorAsync(ct);
            return;
        }

        // The card needs authentication: Stripe applies the plan when the coach pays, and the webhook then writes our row.
        if (upgrade is { IsAwaitingPayment: true })
        {
            await Send.OkAsync(
                new ChangePlanResponse { PlanCode = target.Code, Timing = PlanChangeTiming.AwaitingPayment, PaymentUrl = upgrade.PaymentUrl },
                ct);
            return;
        }

        // Our own row is written now; LastExternalEventAt stays, so the webhook that follows still applies.
        if (decision.IsUpgrade)
        {
            var before = await coachEvents.CaptureAsync(subscription, ct);

            subscription.SubscriptionPlanId = target.PlanId;
            subscription.SubscriptionPlanPriceId = target.PriceId;
            subscription.PendingSubscriptionPlanPriceId = null;
            subscription.PaysSecondRoleAddOn = decision.CarryAddOn;
            subscription.CancelAtPeriodEnd = false;
            subscription.RenewalCancelledByAdminAt = null;

            // The upgrade rebuilt the items without the add-on, so a scheduled removal of it is moot.
            if (!decision.CarryAddOn)
            {
                subscription.SecondRoleAddOnEndsAt = null;
            }

            // Only an upgrade that applies now is an event here; the webhook that follows finds the row already updated.
            var after = await coachEvents.CaptureAsync(subscription, ct);
            await coachEvents.RecordAsync(
                CoachEventType.Upgraded,
                userId,
                $"change:{req.AttemptId:N}",
                ct,
                after.PlanCode,
                before.PlanCode,
                before.Amount,
                after.Amount);
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
                Timing = decision.IsUpgrade ? PlanChangeTiming.Now : PlanChangeTiming.AtNextRenewal,
                EffectiveAt = decision.IsUpgrade ? timeProvider.GetUtcNow() : subscription.CurrentPeriodEndsAt,
            },
            ct);
    }
}
