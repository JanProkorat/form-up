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
[RemovedCoachRole(RemovedCoachRoleMode.Refuse)]
internal sealed class CreateCheckoutSessionEndpoint(IApplicationDbContext db, ISubscriptionService subscriptions, TimeProvider timeProvider)
    : Endpoint<CreateCheckoutSessionRequest, CreateCheckoutSessionResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/billing/checkout-session");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Start a Stripe Checkout";
            s.Description = "Creates a hosted Checkout session for a paid plan, with an optional second-role add-on line and no trial. " +
                            "Promotion codes are allowed unless an automatic promotion applies (the latest claim-from the coach qualifies " +
                            "for, covering the chosen plan), whose coupon is then attached. Billing address and tax id are collected. " +
                            "Acts only on the caller's own profile. Reachable while a plan choice is required.";
            s.Response<CreateCheckoutSessionResponse>(StatusCodes.Status200OK, "The Checkout page to redirect to");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest,
                "Invalid body, or the plan cannot be bought: unknown, Free, inactive, not priced in Stripe, or an add-on on a plan that includes both roles (PLAN_NOT_PURCHASABLE)");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or unreadable caller claim");
            s.Response(StatusCodes.Status403Forbidden, "The caller is not a trainer or nutritionist, or the coach role was removed");
            s.Response(StatusCodes.Status404NotFound, "The caller has no professional profile, or the Free plan is missing");
            s.Response(StatusCodes.Status409Conflict, "The coach already has a live paid subscription (BILLING_ALREADY_SUBSCRIBED)");
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
            .Where(p => p.SubscriptionPlan.Code == req.PlanCode && p.ValidTo == null)
            .Select(p => new PlanOffer(
                p.SubscriptionPlan.Code,
                p.SubscriptionPlan.IsActive,
                p.SubscriptionPlan.BothRolesIncluded,
                p.SubscriptionPlan.BillingInterval,
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

        if (req.IncludeSecondRoleAddOn && (price.BothRolesIncluded || price.AddOnAmount is null or <= 0))
        {
            this.ThrowErrorWithCode(ErrorCodes.PlanNotPurchasable, "This plan has no second-role add-on to buy.");
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
                req.IncludeSecondRoleAddOn ? price.AddOnAmount : null,
                price.Currency,
                price.BillingInterval,
                await AutomaticCouponIdAsync(coach.ProfileId, price.Code, ct),
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
    /// The Stripe coupon of the coach's winning automatic promotion, or null. The winner is picked per coach, so a plan the
    /// winner does not cover gets no coupon and keeps the promotion-code field on Checkout.
    /// </summary>
    private async Task<string?> AutomaticCouponIdAsync(long profileId, string planCode, CancellationToken ct)
    {
        var winner = await PromotionSelector.PickForCoachAsync(db, profileId, timeProvider.GetUtcNow(), ct);

        return winner is not null && PromotionRules.CoversPlan(winner, planCode) ? winner.StripeCouponId : null;
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
                .Where(p => p.SubscriptionPlan.Code == SubscriptionPlanCodes.Free && p.ValidTo == null)
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

    private sealed record CoachIdentity(long ProfileId, string? Email, string FirstName, string LastName, string? Language);
}
