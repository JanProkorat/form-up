using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Features.Billing.Shared;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Services.Billing;

namespace FormUp.Application.Features.Billing.ApplyPromotionCode;

/// <summary>
/// Applies a promotion code to the caller's own live subscription. The code is never logged or echoed back, and the
/// route is rate limited per account.
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
            s.Description = "Applies a Stripe promotion code to the caller's own live paid subscription. Rate limited per account.";
            s.Response(StatusCodes.Status204NoContent, "The code was applied");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest,
                "The code is empty, too long or has invalid characters (PROMOTION_CODE_INVALID)");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or unreadable caller claim");
            s.Response(StatusCodes.Status403Forbidden, "The caller is not a trainer or nutritionist, or the coach role was removed");
            s.Response(StatusCodes.Status404NotFound, "The code is unknown or no longer active (PROMOTION_CODE_NOT_FOUND)");
            s.Response(StatusCodes.Status409Conflict,
                "No live paid subscription (BILLING_NO_SUBSCRIPTION), or an admin gift is in force (BILLING_GIFT_IN_FORCE)");
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

        var subscription = await this.LoadLiveSubscriptionOrRespondAsync(db, userId, timeProvider.GetUtcNow(), ct);

        if (subscription is null)
        {
            return;
        }

        try
        {
            if (!await subscriptions.ApplyPromotionCodeAsync(subscription.ExternalSubscriptionId!, req.Code, ct))
            {
                await this.SendProblemAsync(
                    StatusCodes.Status404NotFound, ErrorCodes.PromotionCodeNotFound, "The promotion code was not found.", ct);
                return;
            }
        }
        catch (BillingProviderException)
        {
            await this.RespondBillingProviderErrorAsync(ct);
            return;
        }

        await Send.NoContentAsync(ct);
    }
}
