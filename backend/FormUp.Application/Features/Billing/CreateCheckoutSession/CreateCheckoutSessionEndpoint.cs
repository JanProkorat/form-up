using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Services.Billing;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.Billing.CreateCheckoutSession;

/// <summary>
/// Starts a hosted Stripe Checkout for the caller's own subscription. The customer is created or reused per profile and
/// the redirect URLs come from configuration, never from the request.
/// </summary>
/// <param name="db">Application database context.</param>
/// <param name="subscriptions">The payment provider seam.</param>
/// <param name="timeProvider">Clock.</param>
/// <param name="coachRoleStatus">Resolves the coach roles that are not removed.</param>
[RemovedCoachRole(RemovedCoachRoleMode.Refuse)]
internal sealed class CreateCheckoutSessionEndpoint(
    IApplicationDbContext db, ISubscriptionService subscriptions, TimeProvider timeProvider, ICoachRoleStatus coachRoleStatus)
    : Endpoint<CreateCheckoutSessionRequest, CreateCheckoutSessionResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/billing/checkout-session");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist);
        Options(x => x.RequireRateLimiting(AppPolicies.PromotionCodeRateLimit));
        Summary(s =>
        {
            s.Summary = "Start a Stripe Checkout";
            s.Description = "Creates a hosted Checkout session for a paid plan with no trial, billed monthly (the default) or annually " +
                            "(BillingInterval). A coach with two active roles pays the " +
                            "second-role add-on line on a plan that does not include both roles, at the same interval as the plan; the request flag is ignored. " +
                            "An optional Code is checked against our promotion (claim window, plan, billing interval, max uses, new " +
                            "coaches only, one use per coach) before anything is sent to Stripe, and its coupon replaces the automatic " +
                            "promotion. Without a code, the automatic promotion the coach qualifies for (the latest claim-from, covering " +
                            "the chosen plan and interval) is attached. Stripe's own promotion-code field is never shown. Billing address " +
                            "and tax id are collected. Acts only on the caller's own profile. Reachable while a plan choice is required. " +
                            "Rate limited per account, because a typed code can be probed.";
            s.Response<CreateCheckoutSessionResponse>(StatusCodes.Status200OK, "The Checkout page to redirect to");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest,
                "Invalid body (a Code that is too long or has invalid characters is PROMOTION_CODE_INVALID), or the plan cannot be bought: unknown, Free, inactive, no open price for the interval, not priced in Stripe, or the caller has two active roles and the plan has no add-on price (PLAN_NOT_PURCHASABLE)");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or unreadable caller claim");
            s.Response(StatusCodes.Status403Forbidden, "The caller is not a trainer or nutritionist, or the coach role was removed");
            s.Response(StatusCodes.Status404NotFound,
                "The caller has no professional profile, or the Free plan is missing, or no promotion has the Code, its claim window has closed, or an admin ended it (PROMOTION_CODE_NOT_FOUND)");
            s.Response(StatusCodes.Status409Conflict,
                "The coach already has a live paid subscription (BILLING_ALREADY_SUBSCRIBED), or the Code fails a rule: " +
                "PROMOTION_CODE_NOT_STARTED, PROMOTION_CODE_PLAN_NOT_ELIGIBLE, PROMOTION_CODE_INTERVAL_NOT_ELIGIBLE, " +
                "PROMOTION_CODE_USED_UP, PROMOTION_CODE_NEW_COACHES_ONLY, PROMOTION_CODE_ALREADY_USED");
            s.Response(StatusCodes.Status429TooManyRequests, "The per-account rate limit was exceeded");
            s.Response(StatusCodes.Status502BadGateway, "Stripe failed; nothing was started (BILLING_PROVIDER_ERROR)");
            s.Response(StatusCodes.Status503ServiceUnavailable, "Stripe is not configured on this server (BILLING_NOT_CONFIGURED)");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(CreateCheckoutSessionRequest req, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(AppClaims.UserId), out var userId))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        if (!subscriptions.IsConfigured)
        {
            await this.SendProblemAsync(
                StatusCodes.Status503ServiceUnavailable, ErrorCodes.BillingNotConfigured, "Billing is not configured.", ct);
            return;
        }

        var price = await db.SubscriptionPlanPrices
            .AsNoTracking()
            .Where(p => p.SubscriptionPlan.Code == req.PlanCode && p.ValidTo == null
                        && p.BillingInterval == req.BillingInterval)
            .Select(p => new PlanOffer(
                p.SubscriptionPlan.Code,
                p.SubscriptionPlan.IsActive,
                p.SubscriptionPlan.BothRolesIncluded,
                p.BillingInterval,
                p.PriceMinorUnits,
                p.SecondRoleAddOnPriceMinorUnits,
                p.Currency,
                p.ExternalPriceId))
            .FirstOrDefaultAsync(ct);

        if (price is null
            || price.Code == SubscriptionPlanCodes.Free
            || !price.IsActive
            || price.PriceMinorUnits <= 0
            || string.IsNullOrEmpty(price.ExternalPriceId))
        {
            this.ThrowErrorWithCode(ErrorCodes.PlanNotPurchasable, "This plan cannot be bought.");
            return;
        }

        var activeRoles = await coachRoleStatus.GetActiveRolesAsync(userId, ct);
        var carryAddOn = activeRoles.Count >= 2 && !price.BothRolesIncluded;

        if (carryAddOn && price.AddOnAmount is null or <= 0)
        {
            this.ThrowErrorWithCode(ErrorCodes.PlanNotPurchasable, "This plan has no second-role add-on price.");
            return;
        }

        var coach = await db.ProfessionalProfiles
            .AsNoTracking()
            .Where(p => p.UserId == userId)
            .Select(p => new CoachIdentity(p.Id, p.User.Email, p.User.FirstName, p.User.LastName, p.User.Language))
            .FirstOrDefaultAsync(ct);

        if (coach is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var subscription = await db.CoachSubscriptions.FirstOrDefaultAsync(cs => cs.ProfessionalProfileId == coach.ProfileId, ct);

        if (subscription is { ExternalSubscriptionId: not null, Status: SubscriptionStatus.Active or SubscriptionStatus.Trialing or SubscriptionStatus.PastDue })
        {
            await this.SendProblemAsync(
                StatusCodes.Status409Conflict, ErrorCodes.BillingAlreadySubscribed, "The coach already has a paid subscription.", ct);
            return;
        }

        // Everything that can refuse the code runs here, before the first call to Stripe (no customer, no session).
        var coupon = await ResolveCouponOrRespondAsync(req.Code, price, coach.ProfileId, subscription?.FirstPaidAt, ct);

        if (coupon is null)
        {
            return;
        }

        try
        {
            var customerId = subscription?.ExternalCustomerId ?? await CreateCustomerAsync(coach, userId, subscription, ct);

            if (customerId is null)
            {
                await this.SendProblemAsync(
                    StatusCodes.Status404NotFound, ErrorCodes.SubscriptionPlanNotFound, "Subscription plan not found.", ct);
                return;
            }

            var url = await subscriptions.CreateCheckoutSessionAsync(
                customerId,
                userId,
                price.Code,
                price.ExternalPriceId,
                carryAddOn ? price.AddOnAmount : null,
                price.Currency,
                price.BillingInterval,
                coupon.CouponId,
                req.Flow,
                coach.Language,
                ct);

            await Send.OkAsync(new CreateCheckoutSessionResponse { Url = url }, ct);
        }
        catch (BillingProviderException)
        {
            await this.SendProblemAsync(
                StatusCodes.Status502BadGateway, ErrorCodes.BillingProviderError, "The payment provider failed.", ct);
        }
    }

    /// <summary>
    /// The coupon to attach. A typed code is looked up and checked with <see cref="PromotionEligibility"/>; a code that is
    /// unknown (404) or fails a rule (409) is answered here and null is returned. Without a code the coach's winning
    /// automatic promotion is used when it covers the plan and interval. The winner is per coach, so a plan or interval the
    /// winner does not cover gets no coupon rather than a fallback to another promotion.
    /// </summary>
    private async Task<ResolvedCoupon?> ResolveCouponOrRespondAsync(
        string? code, PlanOffer price, long profileId, DateTimeOffset? firstPaidAt, CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();

        if (string.IsNullOrEmpty(code))
        {
            var winner = await PromotionSelector.PickForCoachAsync(db, profileId, now, ct);

            return new ResolvedCoupon(
                winner is not null && PromotionRules.CoversPlan(winner, price.Code)
                    ? PromotionRules.CouponIdFor(winner, price.BillingInterval)
                    : null);
        }

        var promotion = await PromotionEligibility.FindByCodeAsync(db, code, now, ct);

        if (promotion is null)
        {
            await this.SendProblemAsync(
                StatusCodes.Status404NotFound, ErrorCodes.PromotionCodeNotFound, "The promotion code was not found.", ct);
            return null;
        }

        var refusal = await PromotionEligibility.CheckAsync(
            db, promotion, price.Code, price.BillingInterval, profileId, firstPaidAt, now, ct);

        if (refusal is not null)
        {
            await this.SendProblemAsync(StatusCodes.Status409Conflict, refusal.ErrorCode, refusal.Detail, ct);
            return null;
        }

        return new ResolvedCoupon(PromotionRules.CouponIdFor(promotion, price.BillingInterval));
    }

    /// <summary>
    /// Creates the provider customer and stores its id on the coach's subscription, adding a Free-plan row when the coach has
    /// none. Returns null when that row cannot be added because the Free plan is missing.
    /// </summary>
    private async Task<string?> CreateCustomerAsync(
        CoachIdentity coach, Guid userId, CoachSubscription? subscription, CancellationToken ct)
    {
        if (subscription is null)
        {
            var free = await db.SubscriptionPlanPrices
                .AsNoTracking()
                .Where(p => p.SubscriptionPlan.Code == SubscriptionPlanCodes.Free && p.ValidTo == null
                        && p.BillingInterval == BillingInterval.Monthly)
                .Select(p => new { PriceId = p.Id, p.SubscriptionPlanId })
                .FirstOrDefaultAsync(ct);

            if (free is null)
            {
                return null;
            }

            subscription = new CoachSubscription
            {
                ProfessionalProfileId = coach.ProfileId,
                SubscriptionPlanId = free.SubscriptionPlanId,
                SubscriptionPlanPriceId = free.PriceId,
                Status = SubscriptionStatus.Active,
            };
            db.CoachSubscriptions.Add(subscription);
        }

        var name = $"{coach.FirstName} {coach.LastName}".Trim();
        var customerId = await subscriptions.CreateCustomerAsync(
            userId, coach.Email ?? string.Empty, name.Length == 0 ? null : name, coach.Language, ct);

        subscription.ExternalCustomerId = customerId;
        await db.SaveChangesAsync(ct);

        return customerId;
    }

    private sealed record PlanOffer(
        string Code,
        bool IsActive,
        bool BothRolesIncluded,
        BillingInterval BillingInterval,
        long PriceMinorUnits,
        long? AddOnAmount,
        string Currency,
        string? ExternalPriceId);

    private sealed record ResolvedCoupon(string? CouponId);

    private sealed record CoachIdentity(long ProfileId, string? Email, string FirstName, string LastName, string? Language);
}
