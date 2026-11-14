using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Features.SubscriptionPlans.Shared;
using FormUp.Application.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.SubscriptionPlans.UpdateSubscriptionPlan;

/// <summary>
/// Updates an existing subscription tier's fields. <c>Code</c> is the route key and cannot be
/// changed — see <see cref="UpdateSubscriptionPlanRequest"/>.
/// </summary>
/// <param name="db">Application database context.</param>
internal sealed class UpdateSubscriptionPlanEndpoint(IApplicationDbContext db)
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
            s.Description = "Updates a subscription tier's fields. Code is immutable — reactivation/deactivation happens via the IsActive field here, not a separate endpoint. A change to the price, the second-role add-on price or the currency closes the open price row and opens a new one; coaches stay on their old row unless MoveExistingCoachesAtNextRenewal is set. The Free plan must stay at price 0 and active.";
            s.Response<SubscriptionPlanDto>(StatusCodes.Status200OK, "Subscription plan updated");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest, "Invalid request body");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or invalid credentials");
            s.Response(StatusCodes.Status403Forbidden, "Caller is not an Admin");
            s.Response(StatusCodes.Status404NotFound, "No plan with this Code");
            s.Response(StatusCodes.Status409Conflict, "The Free plan cannot be repriced or deactivated");
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

        if (openPrice is null
            || openPrice.PriceMinorUnits != req.PriceMinorUnits
            || openPrice.SecondRoleAddOnPriceMinorUnits != req.SecondRoleAddOnPriceMinorUnits
            || openPrice.Currency != req.Currency)
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

        openPrice.ExternalPriceId = req.ExternalPriceId;

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
        plan.BillingInterval = req.BillingInterval;
        plan.IsActive = req.IsActive!.Value;

        if (req.MoveExistingCoachesAtNextRenewal)
        {
            await MarkCoachesToMoveAsync(plan, openPrice, ct);
        }

        // The plan's current-price fields and the open price row change in this one save.
        await db.SaveChangesAsync(ct);

        var coachCount = await db.CoachSubscriptions
            .AsNoTracking()
            .CountAsync(cs => cs.SubscriptionPlanId == plan.Id && LiveSubscriptionStatuses.All.Contains(cs.Status), ct);

        await Send.OkAsync(SubscriptionPlanDto.FromEntity(plan, openPrice.ExternalPriceId, coachCount), ct);
    }

    private async Task MarkCoachesToMoveAsync(SubscriptionPlan plan, SubscriptionPlanPrice openPrice, CancellationToken ct)
    {
        var coaches = await db.CoachSubscriptions
            .Where(cs => cs.SubscriptionPlanId == plan.Id && LiveSubscriptionStatuses.All.Contains(cs.Status))
            .ToListAsync(ct);

        foreach (var coach in coaches)
        {
            if (openPrice.Id == 0 || coach.SubscriptionPlanPriceId != openPrice.Id)
            {
                coach.PendingSubscriptionPlanPrice = openPrice;
            }
        }
    }
}
