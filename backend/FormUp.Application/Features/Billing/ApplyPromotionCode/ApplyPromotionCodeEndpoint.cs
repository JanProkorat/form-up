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
/// Applies a promotion code to the caller's own live subscription after checking it against our promotion
/// (<see cref="PromotionEligibility"/>): claim window, plan, billing interval, max uses, new coaches only and one use per
/// coach. A valid code attaches the coupon of the subscription's interval and replaces the current discount. The code is
/// never logged or echoed back, and the route is rate limited per account.
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
                "code fails a rule: PROMOTION_CODE_NOT_STARTED, PROMOTION_CODE_PLAN_NOT_ELIGIBLE, PROMOTION_CODE_INTERVAL_NOT_ELIGIBLE, PROMOTION_CODE_USED_UP, " +
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

        var promotion = await PromotionEligibility.FindByCodeAsync(db, req.Code, now, ct);

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

        var refusal = await PromotionEligibility.CheckAsync(
            db,
            promotion,
            subscription.SubscriptionPlan.Code,
            subscription.SubscriptionPlanPrice.BillingInterval,
            subscription.ProfessionalProfileId,
            subscription.FirstPaidAt,
            now,
            ct);

        if (refusal is not null)
        {
            await this.SendProblemAsync(StatusCodes.Status409Conflict, refusal.ErrorCode, refusal.Detail, ct);
            return;
        }

        try
        {
            await subscriptions.ApplyCouponAsync(
                subscription.ExternalSubscriptionId!,
                PromotionRules.CouponIdFor(promotion, subscription.SubscriptionPlanPrice.BillingInterval)!,
                ct);
        }
        catch (BillingProviderException)
        {
            await this.RespondBillingProviderErrorAsync(ct);
            return;
        }

        await Send.NoContentAsync(ct);
    }

}
