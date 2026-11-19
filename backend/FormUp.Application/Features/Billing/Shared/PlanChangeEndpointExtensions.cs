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
    /// Loads the open price of the paid plan with the given code at the given billing interval. Answers 400
    /// <see cref="ErrorCodes.PlanNotPurchasable"/> and returns null when the plan is unknown, Free, inactive, has no open
    /// row for that interval, is unpriced or is not set up at the provider.
    /// </summary>
    /// <param name="endpoint">The endpoint instance.</param>
    /// <param name="db">Application database context.</param>
    /// <param name="planCode">Code of the plan to move to.</param>
    /// <param name="interval">Billing interval the coach wants.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<PlanChangeTarget?> LoadPlanChangeTargetOrRespondAsync(
        this IEndpoint endpoint, IApplicationDbContext db, string planCode, BillingInterval interval, CancellationToken ct)
    {
        var target = await db.SubscriptionPlanPrices
            .AsNoTracking()
            .Where(p => p.SubscriptionPlan.Code == planCode && p.ValidTo == null
                        && p.BillingInterval == interval)
            .Select(p => new PlanChangeTarget(
                p.Id,
                p.SubscriptionPlanId,
                p.SubscriptionPlan.Code,
                p.SubscriptionPlan.IsActive,
                p.SubscriptionPlan.BothRolesIncluded,
                p.BillingInterval,
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
    /// Decides how moving the caller's subscription to the target plan and interval plays out; the rule is on
    /// <see cref="PlanChangeDecision"/>. Answers 409 <see cref="ErrorCodes.BillingPlanUnchanged"/> for the current plan at
    /// the current interval and 400 <see cref="ErrorCodes.PlanNotPurchasable"/> when the caller must pay the second-role
    /// add-on and the target row has no add-on price; either way it returns null.
    /// </summary>
    /// <param name="endpoint">The endpoint instance.</param>
    /// <param name="db">Application database context.</param>
    /// <param name="coachRoleStatus">Resolves the coach roles that are not removed.</param>
    /// <param name="userId">The caller's <c>ApplicationUser.Id</c>.</param>
    /// <param name="subscription">The caller's subscription row with plan and price loaded.</param>
    /// <param name="target">The plan and interval to move to.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<PlanChangeDecision?> DecidePlanChangeOrRespondAsync(
        this IEndpoint endpoint,
        IApplicationDbContext db,
        ICoachRoleStatus coachRoleStatus,
        Guid userId,
        CoachSubscription subscription,
        PlanChangeTarget target,
        CancellationToken ct)
    {
        var current = subscription.SubscriptionPlanPrice;

        if (subscription.SubscriptionPlanId == target.PlanId && current.BillingInterval == target.BillingInterval)
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

        return new PlanChangeDecision(await AppliesNowAsync(db, subscription, target, carryAddOn, ct), carryAddOn);
    }

    private static async Task<bool> AppliesNowAsync(
        IApplicationDbContext db, CoachSubscription subscription, PlanChangeTarget target, bool carryAddOn, CancellationToken ct)
    {
        var current = subscription.SubscriptionPlanPrice;

        if (current.BillingInterval == target.BillingInterval)
        {
            return PerYear(target.PriceMinorUnits, target.AddOnAmount, carryAddOn, target.BillingInterval)
                   > PerYear(current.PriceMinorUnits, current.SecondRoleAddOnPriceMinorUnits, subscription.PaysSecondRoleAddOn, current.BillingInterval);
        }

        if (current.BillingInterval == BillingInterval.Annual)
        {
            return false;
        }

        // Monthly to annual: the same plan is a pure saving, so it applies now; another plan applies now only when it is not a step down.
        return subscription.SubscriptionPlanId == target.PlanId
               || await TargetRanksAtLeastAsync(db, subscription, target, carryAddOn, ct);
    }

    /// <summary>
    /// Compares the two plans at one interval, so a monthly-to-annual switch is never judged by an annual price against a
    /// monthly one. The annual rows are compared when the current plan has an open annual row, otherwise the open monthly
    /// rows. When the target plan has no open row at that interval the plans cannot be ranked and the change waits for renewal.
    /// </summary>
    private static async Task<bool> TargetRanksAtLeastAsync(
        IApplicationDbContext db, CoachSubscription subscription, PlanChangeTarget target, bool carryAddOn, CancellationToken ct)
    {
        var rows = await db.SubscriptionPlanPrices
            .AsNoTracking()
            .Where(p => p.ValidTo == null
                        && (p.SubscriptionPlanId == subscription.SubscriptionPlanId || p.SubscriptionPlanId == target.PlanId))
            .Select(p => new OpenPrice(p.SubscriptionPlanId, p.BillingInterval, p.PriceMinorUnits, p.SecondRoleAddOnPriceMinorUnits))
            .ToListAsync(ct);

        var interval = rows.Any(row => row.PlanId == subscription.SubscriptionPlanId && row.Interval == BillingInterval.Annual)
            ? BillingInterval.Annual
            : BillingInterval.Monthly;
        var currentRow = rows.FirstOrDefault(row => row.PlanId == subscription.SubscriptionPlanId && row.Interval == interval);
        var targetRow = rows.FirstOrDefault(row => row.PlanId == target.PlanId && row.Interval == interval);

        if (currentRow is null || targetRow is null)
        {
            return false;
        }

        return PerYear(targetRow.Price, targetRow.AddOn, carryAddOn, interval)
               >= PerYear(currentRow.Price, currentRow.AddOn, subscription.PaysSecondRoleAddOn, interval);
    }

    private static long PerYear(long price, long? addOn, bool includeAddOn, BillingInterval interval) =>
        (price + (includeAddOn ? addOn ?? 0 : 0)) * (interval == BillingInterval.Annual ? 1 : 12);

    private sealed record OpenPrice(long PlanId, BillingInterval Interval, long Price, long? AddOn);
}
