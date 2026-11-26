using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.SubscriptionPlans.Shared;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.SubscriptionPlans.GetPublicSubscriptionPlans;

/// <summary>
/// Lists the offered subscription plans for the public pricing page. Anonymous and web-only.
/// </summary>
/// <param name="db">Application database context.</param>
/// <param name="timeProvider">Clock.</param>
internal sealed class GetPublicSubscriptionPlansEndpoint(IApplicationDbContext db, TimeProvider timeProvider)
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
                            "mobile app never calls this route. Only offered plans are returned, cheapest monthly price first, with no " +
                            "ids or coach counts. Each plan also carries its annual price lines, discounted values and saving percent, " +
                            "all null when the plan is not sold annually. Also carries the active automatic promotion as a new coach would get it, " +
                            "with display-only discounted prices on the plans it covers; it never depends on the caller, so " +
                            "the shared cache stays valid. The response is cacheable for five minutes.";
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

        var annualPrices = await db.SubscriptionPlanPrices
            .AsNoTracking()
            .Where(price => activePlanIds.Contains(price.SubscriptionPlanId)
                            && price.ValidTo == null
                            && price.BillingInterval == BillingInterval.Annual)
            .Select(price => new
            {
                price.SubscriptionPlanId,
                Price = new AnnualPriceSnapshot(price.PriceMinorUnits, price.SecondRoleAddOnPriceMinorUnits, price.ExternalPriceId),
            })
            .ToDictionaryAsync(row => row.SubscriptionPlanId, row => row.Price, ct);

        var mostPopularCode = MostPopularPlanPicker.Pick(plans.Select(plan =>
            (plan.Code, plan.PriceMinorUnits, coachCounts.GetValueOrDefault(plan.Id))));

        // Never the caller's profile: the response is shared-cached, so every caller must get the same body.
        var promotion = await PromotionSelector.PickForCoachAsync(db, profileId: null, timeProvider.GetUtcNow(), ct);

        HttpContext.Response.Headers.CacheControl = "public, max-age=300";

        await Send.OkAsync(new GetPublicSubscriptionPlansResponse
        {
            Promotion = promotion is null ? null : ToDto(promotion),
            Plans = plans.Select(plan => ToDto(plan, annualPrices.GetValueOrDefault(plan.Id), promotion, mostPopularCode)).ToList(),
        }, ct);
    }

    private static PublicSubscriptionPlanDto ToDto(
        SubscriptionPlan plan, AnnualPriceSnapshot? annualPrice, Promotion? promotion, string? mostPopularCode)
    {
        var addOnPrice = plan.BothRolesIncluded ? null : plan.SecondRoleAddOnPriceMinorUnits;
        var annualAddOnPrice = plan.BothRolesIncluded ? null : annualPrice?.SecondRoleAddOnPriceMinorUnits;
        long? discountedPrice = null;
        long? discountedAddOn = null;
        long? annualDiscountedPrice = null;
        long? annualDiscountedAddOn = null;

        if (promotion is not null)
        {
            if (PromotionPricing.Applies(promotion, plan.Code, plan.Currency, BillingInterval.Monthly))
            {
                var (planPrice, addOnDiscounted) = PromotionPricing.Apply(promotion, plan.PriceMinorUnits, addOnPrice);

                discountedPrice = planPrice;
                discountedAddOn = addOnDiscounted;
            }

            if (annualPrice is not null
                && PromotionPricing.Applies(promotion, plan.Code, plan.Currency, BillingInterval.Annual))
            {
                var (annualPlanPrice, annualAddOnDiscounted) =
                    PromotionPricing.Apply(promotion, annualPrice.PriceMinorUnits, annualAddOnPrice);

                annualDiscountedPrice = annualPlanPrice;
                annualDiscountedAddOn = annualAddOnDiscounted;
            }
        }

        return new PublicSubscriptionPlanDto
        {
            Code = plan.Code,
            NameCs = plan.NameCs,
            NameEn = plan.NameEn,
            NameDe = plan.NameDe,
            PriceMinorUnits = plan.PriceMinorUnits,
            SecondRoleAddOnPriceMinorUnits = addOnPrice,
            DiscountedPriceMinorUnits = discountedPrice,
            DiscountedSecondRoleAddOnPriceMinorUnits = discountedAddOn,
            AnnualPriceMinorUnits = annualPrice?.PriceMinorUnits,
            AnnualSecondRoleAddOnPriceMinorUnits = annualAddOnPrice,
            AnnualDiscountedPriceMinorUnits = annualDiscountedPrice,
            AnnualDiscountedSecondRoleAddOnPriceMinorUnits = annualDiscountedAddOn,
            AnnualSavingPercent = AnnualSaving.Percent(plan.PriceMinorUnits, annualPrice?.PriceMinorUnits),
            BothRolesIncluded = plan.BothRolesIncluded,
            Currency = plan.Currency,
            BillingInterval = BillingInterval.Monthly,
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
        };
    }

    private static PublicPromotionDto ToDto(Promotion promotion) => new()
    {
        Name = promotion.Name,
        NameEn = promotion.NameEn,
        NameDe = promotion.NameDe,
        Kind = promotion.Kind,
        Value = promotion.Value,
        Currency = promotion.Currency,
        Duration = promotion.Duration,
        DurationMonths = promotion.DurationMonths,
        BillingPeriod = promotion.BillingPeriod,
        ClaimUntil = promotion.ClaimUntil,
        PlanCodes = promotion.PlanCodes.ToList(),
    };
}
