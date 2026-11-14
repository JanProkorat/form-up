using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Features.SubscriptionPlans.Shared;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Services.Billing;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.SubscriptionPlans.UpdateSubscriptionPlan;

/// <summary>
/// Updates an existing subscription tier's fields. <c>Code</c> is the route key and cannot be
/// changed — see <see cref="UpdateSubscriptionPlanRequest"/>.
/// </summary>
/// <param name="db">Application database context.</param>
/// <param name="subscriptionService">The payment provider seam.</param>
/// <param name="logger">Logger.</param>
internal sealed class UpdateSubscriptionPlanEndpoint(
    IApplicationDbContext db,
    ISubscriptionService subscriptionService,
    ILogger<UpdateSubscriptionPlanEndpoint> logger)
    : Endpoint<UpdateSubscriptionPlanRequest, SubscriptionPlanDto>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Put("/admin/subscription-plans/{Code}");
        Roles(AppRoles.Admin);
        Summary(s =>
        {
            s.Summary = "Update subscription plan";
            s.Description = "Updates a subscription tier's fields. Code is immutable — reactivation/deactivation happens via the IsActive field here, not a separate endpoint. A change to the price, the second-role add-on price or the currency closes the open price row and opens a new one; coaches stay on their old row unless MoveExistingCoachesAtNextRenewal is set. The Free plan must stay at price 0 and active. A new paid price row gets its Stripe price created here (the request's ExternalPriceId is ignored), and with MoveExistingCoachesAtNextRenewal the renewal of every coach with a Stripe subscription is scheduled at the new price. Without Stripe keys the change is saved locally without Stripe calls; a Stripe failure saves nothing.";
            s.Response<SubscriptionPlanDto>(StatusCodes.Status200OK, "Subscription plan updated");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest, "Invalid request body");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or invalid credentials");
            s.Response(StatusCodes.Status403Forbidden, "Caller is not an Admin");
            s.Response(StatusCodes.Status404NotFound, "No plan with this Code");
            s.Response(StatusCodes.Status409Conflict, "The Free plan cannot be repriced or deactivated");
            s.Response<ProblemDetails>(StatusCodes.Status502BadGateway,
                "Stripe failed; nothing was saved (BILLING_PROVIDER_ERROR)");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(UpdateSubscriptionPlanRequest req, CancellationToken ct)
    {
        var plan = await db.SubscriptionPlans
            .Include(p => p.Prices.Where(price => price.ValidTo == null))
            .FirstOrDefaultAsync(p => p.Code == req.Code, ct);

        if (plan is null)
        {
            await this.SendProblemAsync(
                StatusCodes.Status404NotFound,
                ErrorCodes.SubscriptionPlanNotFound,
                "No subscription plan with this Code.",
                ct);
            return;
        }

        if (plan.Code == SubscriptionPlanCodes.Free && (req.PriceMinorUnits != 0 || !req.IsActive!.Value))
        {
            await this.SendProblemAsync(
                StatusCodes.Status409Conflict,
                ErrorCodes.FreePlanLocked,
                "The Free plan must stay at price 0 and cannot be deactivated.",
                ct);
            return;
        }

        var openPrice = plan.Prices.FirstOrDefault();
        var opensNewRow = openPrice is null
                          || openPrice.PriceMinorUnits != req.PriceMinorUnits
                          || openPrice.SecondRoleAddOnPriceMinorUnits != req.SecondRoleAddOnPriceMinorUnits
                          || openPrice.Currency != req.Currency;

        // Stripe first, so a failure leaves nothing saved. A zero price (the Free plan) never gets a Stripe price; an open
        // row that has none yet (saved while Stripe was unconfigured) gets one on the next update.
        string? externalPriceId = opensNewRow ? null : openPrice!.ExternalPriceId;

        if (req.PriceMinorUnits > 0 && externalPriceId is null)
        {
            var (createdPriceId, priceFailed) = await TryCreateStripePriceAsync(plan.Code, req, ct);

            externalPriceId = createdPriceId;

            if (priceFailed)
            {
                await this.SendProblemAsync(
                    StatusCodes.Status502BadGateway,
                    ErrorCodes.BillingProviderError,
                    "The payment provider failed; nothing was saved.",
                    ct);
                return;
            }
        }

        if (opensNewRow)
        {
            if (openPrice is not null)
            {
                openPrice.ValidTo = DateTime.UtcNow;
            }

            openPrice = new SubscriptionPlanPrice
            {
                SubscriptionPlanId = plan.Id,
                PriceMinorUnits = req.PriceMinorUnits,
                SecondRoleAddOnPriceMinorUnits = req.SecondRoleAddOnPriceMinorUnits,
                Currency = req.Currency,
                ValidFrom = DateTime.UtcNow,
            };
            plan.Prices.Add(openPrice);
        }

        openPrice!.ExternalPriceId = externalPriceId;

        plan.NameCs = req.NameCs;
        plan.NameEn = req.NameEn;
        plan.NameDe = req.NameDe;
        plan.BothRolesIncluded = req.BothRolesIncluded!.Value;
        plan.CanCreatePlans = req.CanCreatePlans!.Value;
        plan.CanMessage = req.CanMessage!.Value;
        plan.CanSendQuestionnaires = req.CanSendQuestionnaires!.Value;
        plan.CanUseWeeklyCheckIns = req.CanUseWeeklyCheckIns!.Value;
        plan.CanUsePerClientCheckInConfig = req.CanUsePerClientCheckInConfig!.Value;
        plan.CanUsePayments = req.CanUsePayments!.Value;
        plan.MaxActiveClients = req.MaxActiveClients.Value;
        plan.PriceMinorUnits = req.PriceMinorUnits;
        plan.SecondRoleAddOnPriceMinorUnits = req.SecondRoleAddOnPriceMinorUnits;
        plan.Currency = req.Currency;
        plan.BillingInterval = req.BillingInterval;
        plan.IsActive = req.IsActive!.Value;

        if (req.MoveExistingCoachesAtNextRenewal)
        {
            var moved = await MarkCoachesToMoveAsync(plan, openPrice, ct);

            if (!await TryScheduleRenewalPriceAsync(plan, openPrice, moved, ct))
            {
                await this.SendProblemAsync(
                    StatusCodes.Status502BadGateway,
                    ErrorCodes.BillingProviderError,
                    "The payment provider failed; nothing was saved.",
                    ct);
                return;
            }
        }

        // The plan's current-price fields and the open price row change in this one save.
        await db.SaveChangesAsync(ct);

        var coachCount = await db.CoachSubscriptions
            .AsNoTracking()
            .CountAsync(cs => cs.SubscriptionPlanId == plan.Id && LiveSubscriptionStatuses.All.Contains(cs.Status), ct);

        await Send.OkAsync(SubscriptionPlanDto.FromEntity(plan, openPrice.ExternalPriceId, coachCount), ct);
    }

    /// <summary>Creates the Stripe price for a new price row; a failure is reported instead of thrown.</summary>
    private async Task<(string? PriceId, bool Failed)> TryCreateStripePriceAsync(
        string planCode, UpdateSubscriptionPlanRequest req, CancellationToken ct)
    {
        if (!subscriptionService.IsConfigured)
        {
            logger.LogWarning("Stripe is not configured; plan {PlanCode} is saved without a Stripe price", planCode);

            return (null, false);
        }

        try
        {
            return (await subscriptionService.CreatePriceAsync(
                planCode, req.PriceMinorUnits, req.Currency, req.BillingInterval, ct), false);
        }
        catch (BillingProviderException)
        {
            return (null, true);
        }
    }

    /// <summary>
    /// Schedules the renewal of each moved coach that has a Stripe subscription at the new price. Returns false when Stripe
    /// failed. Skipped with a warning without keys or without a Stripe price on the new row.
    /// </summary>
    private async Task<bool> TryScheduleRenewalPriceAsync(
        SubscriptionPlan plan, SubscriptionPlanPrice openPrice, List<CoachSubscription> moved, CancellationToken ct)
    {
        var withStripe = moved.Where(coach => !string.IsNullOrEmpty(coach.ExternalSubscriptionId)).ToList();

        if (withStripe.Count == 0)
        {
            return true;
        }

        if (!subscriptionService.IsConfigured || string.IsNullOrEmpty(openPrice.ExternalPriceId))
        {
            logger.LogWarning(
                "Plan {PlanCode}: {Count} Stripe subscriptions were not scheduled to the new price (Stripe is not configured or the price has no Stripe id)",
                plan.Code,
                withStripe.Count);

            return true;
        }

        try
        {
            foreach (var coach in withStripe)
            {
                await subscriptionService.ChangePlanAsync(
                    coach.ExternalSubscriptionId!,
                    plan.Code,
                    openPrice.ExternalPriceId,
                    coach.PaysSecondRoleAddOn ? openPrice.SecondRoleAddOnPriceMinorUnits : null,
                    openPrice.Currency,
                    plan.BillingInterval,
                    atNextRenewal: true,
                    ct);
            }

            return true;
        }
        catch (BillingProviderException)
        {
            return false;
        }
    }

    private async Task<List<CoachSubscription>> MarkCoachesToMoveAsync(
        SubscriptionPlan plan, SubscriptionPlanPrice openPrice, CancellationToken ct)
    {
        var coaches = await db.CoachSubscriptions
            .Where(cs => cs.SubscriptionPlanId == plan.Id && LiveSubscriptionStatuses.All.Contains(cs.Status))
            .ToListAsync(ct);
        var moved = new List<CoachSubscription>();

        foreach (var coach in coaches)
        {
            if (openPrice.Id == 0 || coach.SubscriptionPlanPriceId != openPrice.Id)
            {
                coach.PendingSubscriptionPlanPrice = openPrice;
                moved.Add(coach);
            }
        }

        return moved;
    }
}
