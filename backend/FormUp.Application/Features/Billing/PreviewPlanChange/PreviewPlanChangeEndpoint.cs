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
using Microsoft.Extensions.Options;

namespace FormUp.Application.Features.Billing.PreviewPlanChange;

/// <summary>
/// Previews, without changing anything, what moving the caller's own live subscription to another paid plan would do: an
/// upgrade shows today's prorated charge, a downgrade only when it starts. It is a POST so the removed-coach-role gate
/// covers it like the change itself.
/// </summary>
/// <param name="db">Application database context.</param>
/// <param name="subscriptions">The payment provider seam.</param>
/// <param name="options">Billing settings, for the tax mode.</param>
/// <param name="timeProvider">Clock.</param>
/// <param name="coachRoleStatus">Resolves the coach roles that are not removed.</param>
[RemovedCoachRole(RemovedCoachRoleMode.Refuse)]
internal sealed class PreviewPlanChangeEndpoint(
    IApplicationDbContext db,
    ISubscriptionService subscriptions,
    IOptions<BillingOptions> options,
    TimeProvider timeProvider,
    ICoachRoleStatus coachRoleStatus) : Endpoint<PreviewPlanChangeRequest, PreviewPlanChangeResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/billing/change-plan/preview");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Preview a plan change";
            s.Description = "For the caller's own live subscription and a target paid plan, returns whether the change applies now " +
                            "or at the next renewal, the prorated amount charged today (zero for a scheduled change), the regular " +
                            "bill and the date it starts, and whether the upgrade drops a scheduled switch to Free. Nothing is " +
                            "charged or changed.";
            s.Response<PreviewPlanChangeResponse>(StatusCodes.Status200OK, "The preview");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest,
                "Invalid body, or the plan cannot be bought: unknown, Free, inactive, not priced in Stripe, or the caller has two active roles and the plan has no add-on price (PLAN_NOT_PURCHASABLE)");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or unreadable caller claim");
            s.Response(StatusCodes.Status403Forbidden, "The caller is not a trainer or nutritionist, or the coach role was removed");
            s.Response(StatusCodes.Status409Conflict,
                "No live paid subscription (BILLING_NO_SUBSCRIPTION), an admin gift is in force (BILLING_GIFT_IN_FORCE), or the plan is the current one (BILLING_PLAN_UNCHANGED)");
            s.Response(StatusCodes.Status502BadGateway, "Stripe failed (BILLING_PROVIDER_ERROR)");
            s.Response(StatusCodes.Status503ServiceUnavailable, "Stripe is not configured on this server (BILLING_NOT_CONFIGURED)");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(PreviewPlanChangeRequest req, CancellationToken ct)
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

        var target = await this.LoadPlanChangeTargetOrRespondAsync(db, req.PlanCode, ct);

        if (target is null)
        {
            return;
        }

        var subscription = await this.LoadLiveSubscriptionOrRespondAsync(db, userId, timeProvider.GetUtcNow(), ct);

        if (subscription is null)
        {
            return;
        }

        var decision = await this.DecidePlanChangeOrRespondAsync(coachRoleStatus, userId, subscription, target, ct);

        if (decision is null)
        {
            return;
        }

        var addOnAmount = decision.CarryAddOn ? target.AddOnAmount : null;
        var today = new PlanChangePreview(0, 0, target.Currency);

        if (decision.IsUpgrade)
        {
            if (subscription.ExternalCustomerId is null)
            {
                await this.SendProblemAsync(
                    StatusCodes.Status409Conflict, ErrorCodes.BillingNoSubscription, "There is no live paid subscription.", ct);
                return;
            }

            try
            {
                today = await subscriptions.PreviewPlanChangeAsync(
                    subscription.ExternalCustomerId,
                    subscription.ExternalSubscriptionId!,
                    target.Code,
                    target.ExternalPriceId!,
                    addOnAmount,
                    target.Currency,
                    target.BillingInterval,
                    ct);
            }
            catch (BillingProviderException)
            {
                await this.RespondBillingProviderErrorAsync(ct);
                return;
            }
        }

        await Send.OkAsync(
            new PreviewPlanChangeResponse
            {
                PlanCode = target.Code,
                Timing = decision.IsUpgrade ? PlanChangeTiming.Now : PlanChangeTiming.AtNextRenewal,
                BillingInterval = target.BillingInterval,
                CurrentBillingInterval = subscription.SubscriptionPlanPrice.BillingInterval,
                Currency = target.Currency,
                TodayAmountMinorUnits = today.TodayAmountMinorUnits,
                TodayTaxMinorUnits = today.TodayTaxMinorUnits,
                NextAmountMinorUnits = target.PriceMinorUnits + (addOnAmount ?? 0),
                NextChargeAt = subscription.CurrentPeriodEndsAt,
                CancelsScheduledSwitchToFree = decision.IsUpgrade && subscription.CancelAtPeriodEnd,
                TaxMode = options.Value.TaxMode,
            },
            ct);
    }
}
