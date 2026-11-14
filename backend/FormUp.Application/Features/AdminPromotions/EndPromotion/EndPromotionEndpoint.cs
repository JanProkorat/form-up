using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.AdminPromotions.Shared;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Services.Billing;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.AdminPromotions.EndPromotion;

/// <summary>
/// Ends a promotion now. A code promotion first has its Stripe promotion code deactivated (a failure saves nothing); an
/// automatic one has nothing at Stripe to change. The coupon stays, so coaches who already have the discount keep it. The
/// end is saved with a conditional update, so two admins ending it at once write one audit row.
/// </summary>
/// <param name="db">Application database context.</param>
/// <param name="subscriptions">The payment provider seam.</param>
/// <param name="audit">Audit logging service.</param>
/// <param name="timeProvider">Clock.</param>
internal sealed class EndPromotionEndpoint(
    IApplicationDbContext db,
    ISubscriptionService subscriptions,
    IAuditService audit,
    TimeProvider timeProvider)
    : Endpoint<EndPromotionRequest, PromotionDto>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/admin/promotions/{PromotionId}/end");
        Roles(AppRoles.Admin);
        Summary(s =>
        {
            s.Summary = "End a promotion";
            s.Description = "Stops the promotion applying at checkout and claiming, records when it ended and writes an audit row. " +
                            "A code promotion has its Stripe promotion code deactivated first; an automatic promotion ends without " +
                            "a Stripe call. Coaches who already have the discount keep it. A Stripe failure saves nothing.";
            s.Response<PromotionDto>(StatusCodes.Status200OK, "The promotion, now Ended");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest, "The promotion id is empty");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or invalid credentials");
            s.Response(StatusCodes.Status403Forbidden, "Caller is not an Admin");
            s.Response(StatusCodes.Status404NotFound, "No promotion has this id");
            s.Response<ProblemDetails>(StatusCodes.Status409Conflict, "The promotion has already ended (PROMOTION_ALREADY_ENDED)");
            s.Response<ProblemDetails>(StatusCodes.Status502BadGateway,
                "Stripe failed; nothing was saved (BILLING_PROVIDER_ERROR)");
            s.Response<ProblemDetails>(StatusCodes.Status503ServiceUnavailable,
                "A code promotion needs Stripe, which is not configured on this server (BILLING_NOT_CONFIGURED)");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(EndPromotionRequest req, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(AppClaims.UserId), out var adminId))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var promotion = await db.Promotions.AsNoTracking().FirstOrDefaultAsync(p => p.PublicId == req.PromotionId, ct);

        if (promotion is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var now = timeProvider.GetUtcNow();
        var usedCount = await db.PromotionRedemptions.CountAsync(r => r.PromotionId == promotion.Id, ct);

        if (PromotionRules.StatusAt(promotion, usedCount, now) == PromotionStatus.Ended)
        {
            await RespondAlreadyEndedAsync(ct);
            return;
        }

        if (promotion.Mode == PromotionMode.Code && !await DeactivateCodeOrRespondAsync(promotion, ct))
        {
            return;
        }

        var updated = await db.Promotions
            .Where(p => p.Id == promotion.Id && p.EndedAt == null)
            .ExecuteUpdateAsync(
                set => set.SetProperty(p => p.EndedAt, now).SetProperty(p => p.DateUpdated, now.UtcDateTime), ct);

        if (updated == 0)
        {
            await RespondAlreadyEndedAsync(ct);
            return;
        }

        promotion.EndedAt = now;

        await audit.LogAsync(
            adminId,
            AdminPromotionAuditActions.End,
            nameof(Promotion),
            promotion.PublicId,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            newValues: AuditPayload.WithReason(
                new Dictionary<string, object?>
                {
                    ["name"] = promotion.Name,
                    ["mode"] = promotion.Mode.ToString(),
                    ["usedCount"] = usedCount,
                    ["endedAt"] = now.ToString("O"),
                },
                null),
            ct: ct);

        await Send.OkAsync(PromotionDto.FromEntity(promotion, usedCount, now), ct);
    }

    /// <summary>
    /// Deactivates the Stripe promotion code. Answers 503 or 502 and returns false when Stripe is not configured or fails.
    /// </summary>
    private async Task<bool> DeactivateCodeOrRespondAsync(Promotion promotion, CancellationToken ct)
    {
        if (!subscriptions.IsConfigured)
        {
            await this.SendProblemAsync(
                StatusCodes.Status503ServiceUnavailable, ErrorCodes.BillingNotConfigured, "Billing is not configured.", ct);
            return false;
        }

        try
        {
            await subscriptions.DeactivatePromotionCodeAsync(promotion.StripePromotionCodeId!, ct);
        }
        catch (BillingProviderException)
        {
            await this.SendProblemAsync(
                StatusCodes.Status502BadGateway,
                ErrorCodes.BillingProviderError,
                "The payment provider failed; nothing was saved.",
                ct);
            return false;
        }

        return true;
    }

    private Task RespondAlreadyEndedAsync(CancellationToken ct) =>
        this.SendProblemAsync(
            StatusCodes.Status409Conflict, ErrorCodes.PromotionAlreadyEnded, "The promotion has already ended.", ct);
}
