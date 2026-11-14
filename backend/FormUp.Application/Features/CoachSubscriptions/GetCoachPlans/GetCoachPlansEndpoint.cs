using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Services.Billing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FormUp.Application.Features.CoachSubscriptions.GetCoachPlans;

/// <summary>
/// Lists the active subscription plans with their current prices, for a coach choosing a plan. Coach-only: the
/// client app never sees a price. Carries the caller's winning automatic promotion and the display-only discounted prices.
/// </summary>
/// <param name="db">Application database context.</param>
/// <param name="billingOptions">Billing settings, for the tax mode.</param>
/// <param name="timeProvider">Clock.</param>
internal sealed class GetCoachPlansEndpoint(IApplicationDbContext db, IOptions<BillingOptions> billingOptions, TimeProvider timeProvider)
    : EndpointWithoutRequest<GetCoachPlansResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/users/me/subscription/plans");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "List the plans a coach can choose";
            s.Description = "Active plans with names in all locales, current price, second-role add-on price, currency, " +
                            "client cap, feature flags and whether both roles are included. When an automatic promotion applies to " +
                            "the caller (the latest claim-from among those they qualify for) it is returned with a display-only " +
                            "discounted price per plan it covers, second-role line included; other plans show no discounted price.";
            s.Response<GetCoachPlansResponse>(StatusCodes.Status200OK, "The active plans, cheapest first");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or unreadable caller claim");
            s.Response(StatusCodes.Status403Forbidden, "The caller is not a trainer or nutritionist");
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

        var plans = await db.SubscriptionPlans
            .AsNoTracking()
            .Where(p => p.IsActive)
            .OrderBy(p => p.PriceMinorUnits)
            .ThenBy(p => p.Code)
            .Select(p => new CoachPlanDto
            {
                Code = p.Code,
                NameCs = p.NameCs,
                NameEn = p.NameEn,
                NameDe = p.NameDe,
                PriceMinorUnits = p.PriceMinorUnits,
                SecondRoleAddOnPriceMinorUnits = p.SecondRoleAddOnPriceMinorUnits,
                Currency = p.Currency,
                BillingInterval = p.BillingInterval,
                MaxActiveClients = p.MaxActiveClients,
                BothRolesIncluded = p.BothRolesIncluded,
                CanCreatePlans = p.CanCreatePlans,
                CanMessage = p.CanMessage,
                CanSendQuestionnaires = p.CanSendQuestionnaires,
                CanUseWeeklyCheckIns = p.CanUseWeeklyCheckIns,
                CanUsePerClientCheckInConfig = p.CanUsePerClientCheckInConfig,
                CanUsePayments = p.CanUsePayments,
            })
            .ToListAsync(ct);

        var profileId = await db.ProfessionalProfiles
            .AsNoTracking()
            .Where(p => p.UserId == userId)
            .Select(p => (long?)p.Id)
            .FirstOrDefaultAsync(ct);
        var winner = await PromotionSelector.PickForCoachAsync(db, profileId, timeProvider.GetUtcNow(), ct);

        if (winner is not null)
        {
            ApplyPromotion(plans, winner);
        }

        await Send.OkAsync(new GetCoachPlansResponse
        {
            Plans = plans,
            TaxMode = billingOptions.Value.TaxMode,
            Promotion = winner is null ? null : ToDto(winner),
        }, ct);
    }

    private static void ApplyPromotion(List<CoachPlanDto> plans, Promotion winner)
    {
        foreach (var plan in plans.Where(plan => PromotionRules.CoversPlan(winner, plan.Code)))
        {
            var (planPrice, addOnPrice) = PromotionPricing.Apply(winner, plan.PriceMinorUnits, plan.SecondRoleAddOnPriceMinorUnits);

            plan.DiscountedPriceMinorUnits = planPrice;
            plan.DiscountedSecondRoleAddOnPriceMinorUnits = addOnPrice;
        }
    }

    private static CoachPromotionDto ToDto(Promotion promotion) => new()
    {
        Name = promotion.Name,
        Kind = promotion.Kind,
        Value = promotion.Value,
        Currency = promotion.Currency,
        Duration = promotion.Duration,
        DurationMonths = promotion.DurationMonths,
        ClaimUntil = promotion.ClaimUntil,
        PlanCodes = promotion.PlanCodes.ToList(),
    };
}
