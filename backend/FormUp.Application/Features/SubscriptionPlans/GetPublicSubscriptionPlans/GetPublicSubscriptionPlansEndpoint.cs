using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Features.SubscriptionPlans.Shared;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.SubscriptionPlans.GetPublicSubscriptionPlans;

/// <summary>
/// Lists the offered subscription plans for the public pricing page. Anonymous and web-only.
/// </summary>
/// <param name="db">Application database context.</param>
internal sealed class GetPublicSubscriptionPlansEndpoint(IApplicationDbContext db)
    : EndpointWithoutRequest<GetPublicSubscriptionPlansResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/subscription-plans");
        AllowAnonymous();
        Options(x => x.RequireRateLimiting(AppPolicies.PublicPricingRateLimit));
        Summary(s =>
        {
            s.Summary = "List the offered plans for the public pricing page";
            s.Description = "Anonymous on purpose: prices are public marketing content for the web pricing page, and the " +
                            "mobile app never calls this route. Only offered plans are returned, cheapest first, with no " +
                            "ids or coach counts. The response is cacheable for five minutes.";
            s.Response<GetPublicSubscriptionPlansResponse>(StatusCodes.Status200OK, "The offered plans, cheapest first");
            s.Response(StatusCodes.Status429TooManyRequests, "Rate limit exceeded");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(CancellationToken ct)
    {
        var plans = await db.SubscriptionPlans
            .AsNoTracking()
            .Where(plan => plan.IsActive)
            .OrderBy(plan => plan.PriceMinorUnits)
            .ThenBy(plan => plan.Code)
            .ToListAsync(ct);

        var activePlanIds = plans.Select(plan => plan.Id).ToList();

        var coachCounts = await db.CoachSubscriptions
            .AsNoTracking()
            .Where(subscription => activePlanIds.Contains(subscription.SubscriptionPlanId)
                                   && LiveSubscriptionStatuses.All.Contains(subscription.Status)
                                   && subscription.GivenFreeAt == null)
            .GroupBy(subscription => subscription.SubscriptionPlanId)
            .Select(group => new { PlanId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.PlanId, row => row.Count, ct);

        var mostPopularCode = MostPopularPlanPicker.Pick(plans.Select(plan =>
            (plan.Code, plan.PriceMinorUnits, coachCounts.GetValueOrDefault(plan.Id))));

        HttpContext.Response.Headers.CacheControl = "public, max-age=300";

        await Send.OkAsync(new GetPublicSubscriptionPlansResponse
        {
            Plans = plans.Select(plan => new PublicSubscriptionPlanDto
            {
                Code = plan.Code,
                NameCs = plan.NameCs,
                NameEn = plan.NameEn,
                NameDe = plan.NameDe,
                PriceMinorUnits = plan.PriceMinorUnits,
                SecondRoleAddOnPriceMinorUnits = plan.BothRolesIncluded ? null : plan.SecondRoleAddOnPriceMinorUnits,
                BothRolesIncluded = plan.BothRolesIncluded,
                Currency = plan.Currency,
                BillingInterval = plan.BillingInterval,
                MaxActiveClients = plan.MaxActiveClients,
                CanCreatePlans = plan.CanCreatePlans,
                CanMessage = plan.CanMessage,
                CanSendQuestionnaires = plan.CanSendQuestionnaires,
                CanUseWeeklyCheckIns = plan.CanUseWeeklyCheckIns,
                CanUsePerClientCheckInConfig = plan.CanUsePerClientCheckInConfig,
                CanUsePayments = plan.CanUsePayments,
                CanUsePlanTemplates = plan.CanUsePlanTemplates,
                CanUseRecipes = plan.CanUseRecipes,
                CanUseIngredients = plan.CanUseIngredients,
                CanUseProgressPhotos = plan.CanUseProgressPhotos,
                CanUseWeightAndMeasurements = plan.CanUseWeightAndMeasurements,
                CanUseAutomations = plan.CanUseAutomations,
                CanUsePackages = plan.CanUsePackages,
                IsMostPopular = plan.Code == mostPopularCode,
            }).ToList(),
        }, ct);
    }
}
