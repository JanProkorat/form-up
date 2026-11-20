using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
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
            s.Description = "Updates a subscription tier's fields. Code is immutable — reactivation/deactivation happens via the IsActive field here, not a separate endpoint. PriceMinorUnits and SecondRoleAddOnPriceMinorUnits are the monthly price; AnnualPriceMinorUnits and AnnualSecondRoleAddOnPriceMinorUnits the annual one. A change to the price, the add-on price or the currency closes that interval's open price row and opens a new one; coaches stay on their old row unless MoveExistingCoachesAtNextRenewal is set, which moves only coaches on the changed interval. Omitting the annual price closes the open annual row (the plan is no longer sold annually; coaches on it keep it). The Free plan must stay at price 0 and active. A new paid price row gets its Stripe price created here (the request's ExternalPriceId is ignored), and with MoveExistingCoachesAtNextRenewal the renewal of every coach with a Stripe subscription is scheduled at the new price. Without Stripe keys the change is saved locally without Stripe calls; a Stripe failure saves nothing.";
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

        var monthlyOpen = plan.Prices.FirstOrDefault(price => price.BillingInterval == BillingInterval.Monthly);
        var annualOpen = plan.Prices.FirstOrDefault(price => price.BillingInterval == BillingInterval.Annual);

        var opensNewMonthly = monthlyOpen is null
                              || monthlyOpen.PriceMinorUnits != req.PriceMinorUnits
                              || monthlyOpen.SecondRoleAddOnPriceMinorUnits != req.SecondRoleAddOnPriceMinorUnits
                              || monthlyOpen.Currency != req.Currency;
        var opensNewAnnual = req.AnnualPriceMinorUnits is not null
                             && (annualOpen is null
                                 || annualOpen.PriceMinorUnits != req.AnnualPriceMinorUnits
                                 || annualOpen.SecondRoleAddOnPriceMinorUnits != req.AnnualSecondRoleAddOnPriceMinorUnits
                                 || annualOpen.Currency != req.Currency);

        // Stripe first, so a failure leaves nothing saved. A zero price (the Free plan) never gets a Stripe price; an open
        // row that has none yet (saved while Stripe was unconfigured) gets one on the next update.
        var monthlyExternalPriceId = opensNewMonthly ? null : monthlyOpen!.ExternalPriceId;
        var annualExternalPriceId = opensNewAnnual ? null : annualOpen?.ExternalPriceId;

        try
        {
            if (req.PriceMinorUnits > 0 && monthlyExternalPriceId is null)
            {
                monthlyExternalPriceId = await TryCreateStripePriceAsync(
                    plan.Code, req.PriceMinorUnits, req.Currency, BillingInterval.Monthly, ct);
            }

            if (req.AnnualPriceMinorUnits is > 0 && annualExternalPriceId is null)
            {
                annualExternalPriceId = await TryCreateStripePriceAsync(
                    plan.Code, req.AnnualPriceMinorUnits.Value, req.Currency, BillingInterval.Annual, ct);
            }
        }
        catch (BillingProviderException)
        {
            await this.SendProblemAsync(
                StatusCodes.Status502BadGateway,
                ErrorCodes.BillingProviderError,
                "The payment provider failed; nothing was saved.",
                ct);
            return;
        }

        if (opensNewMonthly)
        {
            if (monthlyOpen is not null)
            {
                monthlyOpen.ValidTo = DateTime.UtcNow;
            }

            monthlyOpen = new SubscriptionPlanPrice
            {
                SubscriptionPlanId = plan.Id,
                BillingInterval = BillingInterval.Monthly,
                PriceMinorUnits = req.PriceMinorUnits,
                SecondRoleAddOnPriceMinorUnits = req.SecondRoleAddOnPriceMinorUnits,
                Currency = req.Currency,
                ValidFrom = DateTime.UtcNow,
            };
            plan.Prices.Add(monthlyOpen);
        }

        monthlyOpen!.ExternalPriceId = monthlyExternalPriceId;

        if (req.AnnualPriceMinorUnits is null)
        {
            // A PUT is a full update: no annual price means the plan is no longer sold annually. Coaches on the row keep it.
            if (annualOpen is not null)
            {
                annualOpen.ValidTo = DateTime.UtcNow;
                annualOpen = null;
            }
        }
        else
        {
            if (opensNewAnnual)
            {
                if (annualOpen is not null)
                {
                    annualOpen.ValidTo = DateTime.UtcNow;
                }

                annualOpen = new SubscriptionPlanPrice
                {
                    SubscriptionPlanId = plan.Id,
                    BillingInterval = BillingInterval.Annual,
                    PriceMinorUnits = req.AnnualPriceMinorUnits.Value,
                    SecondRoleAddOnPriceMinorUnits = req.AnnualSecondRoleAddOnPriceMinorUnits,
                    Currency = req.Currency,
                    ValidFrom = DateTime.UtcNow,
                };
                plan.Prices.Add(annualOpen);
            }

            annualOpen!.ExternalPriceId = annualExternalPriceId;
        }

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
        plan.CanUsePlanTemplates = req.CanUsePlanTemplates!.Value;
        plan.CanUseRecipes = req.CanUseRecipes!.Value;
        plan.CanUseIngredients = req.CanUseIngredients!.Value;
        plan.CanUseProgressPhotos = req.CanUseProgressPhotos!.Value;
        plan.CanUseWeightAndMeasurements = req.CanUseWeightAndMeasurements!.Value;
        plan.CanUseAutomations = req.CanUseAutomations!.Value;
        plan.CanUsePackages = req.CanUsePackages!.Value;
        plan.MaxActiveClients = req.MaxActiveClients.Value;
        plan.PriceMinorUnits = req.PriceMinorUnits;
        plan.SecondRoleAddOnPriceMinorUnits = req.SecondRoleAddOnPriceMinorUnits;
        plan.Currency = req.Currency;
        plan.IsActive = req.IsActive!.Value;

        if (req.MoveExistingCoachesAtNextRenewal)
        {
            var openRows = new[] { monthlyOpen, annualOpen }.OfType<SubscriptionPlanPrice>();

            foreach (var openRow in openRows)
            {
                var moved = await MarkCoachesToMoveAsync(plan, openRow, ct);

                if (!await TryScheduleRenewalPriceAsync(plan, openRow, moved, ct))
                {
                    await this.SendProblemAsync(
                        StatusCodes.Status502BadGateway,
                        ErrorCodes.BillingProviderError,
                        "The payment provider failed; nothing was saved.",
                        ct);
                    return;
                }
            }
        }

        // The plan's current-price fields and the open price rows change in this one save.
        await db.SaveChangesAsync(ct);

        var coachCount = await db.CoachSubscriptions
            .AsNoTracking()
            .CountAsync(cs => cs.SubscriptionPlanId == plan.Id && LiveSubscriptionStatuses.All.Contains(cs.Status), ct);
        var annualSnapshot = annualOpen is null
            ? null
            : new AnnualPriceSnapshot(annualOpen.PriceMinorUnits, annualOpen.SecondRoleAddOnPriceMinorUnits, annualOpen.ExternalPriceId);

        await Send.OkAsync(SubscriptionPlanDto.FromEntity(plan, monthlyOpen.ExternalPriceId, coachCount, annualSnapshot), ct);
    }

    /// <summary>Creates a Stripe price for a new price row; returns null when Stripe is not configured. Throws on a provider failure.</summary>
    private async Task<string?> TryCreateStripePriceAsync(
        string planCode, long amountMinorUnits, string currency, BillingInterval interval, CancellationToken ct)
    {
        if (!subscriptionService.IsConfigured)
        {
            logger.LogWarning("Stripe is not configured; plan {PlanCode} is saved without a Stripe price", planCode);

            return null;
        }

        return await subscriptionService.CreatePriceAsync(planCode, amountMinorUnits, currency, interval, ct);
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
                    openPrice.BillingInterval,
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
        // Only coaches on a row of the same interval move; a monthly coach never lands on the annual row or the reverse.
        var coaches = await db.CoachSubscriptions
            .Where(cs => cs.SubscriptionPlanId == plan.Id
                         && LiveSubscriptionStatuses.All.Contains(cs.Status)
                         && cs.SubscriptionPlanPrice.BillingInterval == openPrice.BillingInterval)
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
