using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.Billing.Shared;

/// <summary>
/// The target lookup and the upgrade rule shared by the change-plan endpoint and its preview, so the two can never
/// disagree on whether a change applies now.
/// </summary>
internal static class PlanChangeEndpointExtensions
{
    /// <summary>
    /// Loads the open price of the paid plan with the given code. Answers 400 <see cref="ErrorCodes.PlanNotPurchasable"/> and
    /// returns null when the plan is unknown, Free, inactive, unpriced or not set up at the provider.
    /// </summary>
    /// <param name="endpoint">The endpoint instance.</param>
    /// <param name="db">Application database context.</param>
    /// <param name="planCode">Code of the plan to move to.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<PlanChangeTarget?> LoadPlanChangeTargetOrRespondAsync(
        this IEndpoint endpoint, IApplicationDbContext db, string planCode, CancellationToken ct)
    {
        var target = await db.SubscriptionPlanPrices
            .AsNoTracking()
            .Where(p => p.SubscriptionPlan.Code == planCode && p.ValidTo == null)
            .Select(p => new PlanChangeTarget(
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
            endpoint.ThrowErrorWithCode(ErrorCodes.PlanNotPurchasable, "This plan cannot be bought.");

            return null;
        }

        return target;
    }

    /// <summary>
    /// Decides how moving the caller's subscription to the target plays out. Answers 409
    /// <see cref="ErrorCodes.BillingPlanUnchanged"/> for the current plan and 400 <see cref="ErrorCodes.PlanNotPurchasable"/>
    /// when the caller must pay the second-role add-on and the plan has no add-on price; either way it returns null.
    /// </summary>
    /// <param name="endpoint">The endpoint instance.</param>
    /// <param name="coachRoleStatus">Resolves the coach roles that are not removed.</param>
    /// <param name="userId">The caller's <c>ApplicationUser.Id</c>.</param>
    /// <param name="subscription">The caller's subscription row with plan and price loaded.</param>
    /// <param name="target">The plan to move to.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<PlanChangeDecision?> DecidePlanChangeOrRespondAsync(
        this IEndpoint endpoint,
        ICoachRoleStatus coachRoleStatus,
        Guid userId,
        CoachSubscription subscription,
        PlanChangeTarget target,
        CancellationToken ct)
    {
        if (subscription.SubscriptionPlanId == target.PlanId)
        {
            await endpoint.SendProblemAsync(
                StatusCodes.Status409Conflict, ErrorCodes.BillingPlanUnchanged, "The coach is already on this plan.", ct);

            return null;
        }

        var activeRoles = await coachRoleStatus.GetActiveRolesAsync(userId, ct);
        var carryAddOn = activeRoles.Count >= 2 && !target.BothRolesIncluded;

        if (carryAddOn && target.AddOnAmount is null or <= 0)
        {
            endpoint.ThrowErrorWithCode(ErrorCodes.PlanNotPurchasable, "This plan has no second-role add-on price.");

            return null;
        }

        var currentPerYear = subscription.SubscriptionPlanPrice.PriceMinorUnits
                             * (subscription.SubscriptionPlan.BillingInterval == BillingInterval.Annual ? 1 : 12);
        var targetPerYear = target.PriceMinorUnits * (target.BillingInterval == BillingInterval.Annual ? 1 : 12);

        return new PlanChangeDecision(targetPerYear > currentPerYear, carryAddOn);
    }
}
