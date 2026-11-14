using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.Billing.Shared;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Services.Billing;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.Billing.ApplyPromotionCode;

/// <summary>
/// Applies a promotion code to the caller's own live subscription after checking it against our promotion: claim window,
/// plan, max uses, new coaches only and one use per coach. A valid code replaces the current discount. The code is never
/// logged or echoed back, and the route is rate limited per account.
/// </summary>
/// <param name="db">Application database context.</param>
/// <param name="subscriptions">The payment provider seam.</param>
/// <param name="timeProvider">Clock.</param>
[RemovedCoachRole(RemovedCoachRoleMode.Refuse)]
internal sealed class ApplyPromotionCodeEndpoint(IApplicationDbContext db, ISubscriptionService subscriptions, TimeProvider timeProvider)
    : Endpoint<ApplyPromotionCodeRequest>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/billing/promotion-code");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist);
        Options(x => x.RequireRateLimiting(AppPolicies.PromotionCodeRateLimit));
        Summary(s =>
        {
            s.Summary = "Apply a promotion code to my subscription";
            s.Description = "Checks the code against our promotion and, when the caller qualifies, makes it the subscription's only " +
                            "discount at Stripe (the discount list is replaced, never stacked). Applying the code that is already " +
                            "the current promotion changes nothing. Rate limited per account.";
            s.Response(StatusCodes.Status204NoContent, "The code was applied, or is already the current promotion");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest,
                "The code is empty, too long or has invalid characters (PROMOTION_CODE_INVALID)");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or unreadable caller claim");
            s.Response(StatusCodes.Status403Forbidden, "The caller is not a trainer or nutritionist, or the coach role was removed");
            s.Response(StatusCodes.Status404NotFound, "No promotion has this code, its claim window has closed, or an admin ended it (PROMOTION_CODE_NOT_FOUND)");
            s.Response(StatusCodes.Status409Conflict,
                "No live paid subscription (BILLING_NO_SUBSCRIPTION), an admin gift is in force (BILLING_GIFT_IN_FORCE), or the " +
                "code fails a rule: PROMOTION_CODE_NOT_STARTED, PROMOTION_CODE_PLAN_NOT_ELIGIBLE, PROMOTION_CODE_USED_UP, " +
                "PROMOTION_CODE_NEW_COACHES_ONLY, PROMOTION_CODE_ALREADY_USED");
            s.Response(StatusCodes.Status429TooManyRequests, "The per-account rate limit was exceeded");
            s.Response(StatusCodes.Status502BadGateway, "Stripe failed (BILLING_PROVIDER_ERROR)");
            s.Response(StatusCodes.Status503ServiceUnavailable, "Stripe is not configured on this server (BILLING_NOT_CONFIGURED)");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(ApplyPromotionCodeRequest req, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(AppClaims.UserId), out var userId))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        if (await this.RespondIfBillingNotConfiguredAsync(subscriptions, ct))
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        var subscription = await this.LoadLiveSubscriptionOrRespondAsync(db, userId, now, ct);

        if (subscription is null)
        {
            return;
        }

        var promotion = await FindPromotionAsync(req.Code, now, ct);

        if (promotion is null)
        {
            await this.SendProblemAsync(
                StatusCodes.Status404NotFound, ErrorCodes.PromotionCodeNotFound, "The promotion code was not found.", ct);
            return;
        }

        if (subscription.CurrentPromotionId == promotion.Id)
        {
            await Send.NoContentAsync(ct);
            return;
        }

        if (!await IsEligibleOrRespondAsync(promotion, subscription, now, ct))
        {
            return;
        }

        try
        {
            await subscriptions.ApplyPromotionCodeAsync(subscription.ExternalSubscriptionId!, promotion.StripePromotionCodeId!, ct);
        }
        catch (BillingProviderException)
        {
            await this.RespondBillingProviderErrorAsync(ct);
            return;
        }

        await Send.NoContentAsync(ct);
    }

    /// <summary>
    /// The code-mode promotion with this code that no admin ended and whose claim window has not closed; the latest claim-from when several
    /// promotions once shared the code. Null when there is none.
    /// </summary>
    private async Task<Promotion?> FindPromotionAsync(string code, DateTimeOffset now, CancellationToken ct)
    {
        var normalized = code.ToUpperInvariant();
        var sameCode = await db.Promotions
            .AsNoTracking()
            .Where(p => p.Mode == PromotionMode.Code && p.CodeNormalized == normalized && p.StripePromotionCodeId != null
                        && p.EndedAt == null)
            .ToListAsync(ct);

        return sameCode
            .Where(p => !PromotionRules.HasClaimWindowClosed(p, now))
            .OrderByDescending(p => p.ClaimFrom)
            .ThenBy(p => p.Id)
            .FirstOrDefault();
    }

    /// <summary>
    /// Runs the coach-specific rules in a fixed order. Answers 409 with the code of the first rule the promotion fails and
    /// returns false; returns true when the coach qualifies.
    /// </summary>
    private async Task<bool> IsEligibleOrRespondAsync(
        Promotion promotion, CoachSubscription subscription, DateTimeOffset now, CancellationToken ct)
    {
        if (now < promotion.ClaimFrom)
        {
            await Conflict(ErrorCodes.PromotionCodeNotStarted, "The promotion has not started yet.", ct);
            return false;
        }

        if (!PromotionRules.CoversPlan(promotion, subscription.SubscriptionPlan.Code))
        {
            await Conflict(ErrorCodes.PromotionCodePlanNotEligible, "The promotion does not cover your plan.", ct);
            return false;
        }

        var usedCount = await db.PromotionRedemptions.CountAsync(r => r.PromotionId == promotion.Id, ct);

        if (PromotionRules.IsUsedUp(promotion, usedCount))
        {
            await Conflict(ErrorCodes.PromotionCodeUsedUp, "Every use of the promotion is taken.", ct);
            return false;
        }

        if (promotion.NewCoachesOnly && subscription.FirstPaidAt is not null)
        {
            await Conflict(ErrorCodes.PromotionCodeNewCoachesOnly, "The promotion is for new coaches only.", ct);
            return false;
        }

        if (promotion.OneUsePerCoach
            && await db.PromotionRedemptions.AnyAsync(
                r => r.PromotionId == promotion.Id && r.ProfessionalProfileId == subscription.ProfessionalProfileId, ct))
        {
            await Conflict(ErrorCodes.PromotionCodeAlreadyUsed, "You already used this promotion.", ct);
            return false;
        }

        return true;
    }

    private Task Conflict(string errorCode, string detail, CancellationToken ct) =>
        this.SendProblemAsync(StatusCodes.Status409Conflict, errorCode, detail, ct);
}
