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

namespace FormUp.Application.Features.AdminPromotions.CreatePromotion;

/// <summary>
/// Creates a promotion: the Stripe coupon first (and a promotion code in code mode), then our copy, then an audit row. A
/// Stripe failure saves nothing. Stripe enforces max uses, the claim end, the plan products and "new coaches only" at
/// Checkout; one use per coach and the claim start are enforced by our own checks.
/// </summary>
/// <param name="db">Application database context.</param>
/// <param name="subscriptions">The payment provider seam.</param>
/// <param name="audit">Audit logging service.</param>
/// <param name="timeProvider">Clock.</param>
/// <param name="logger">Logger; never receives the code.</param>
internal sealed class CreatePromotionEndpoint(
    IApplicationDbContext db,
    ISubscriptionService subscriptions,
    IAuditService audit,
    TimeProvider timeProvider,
    ILogger<CreatePromotionEndpoint> logger)
    : Endpoint<CreatePromotionRequest, PromotionDto>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/admin/promotions");
        Roles(AppRoles.Admin);
        Summary(s =>
        {
            s.Summary = "Create a promotion";
            s.Description = "Creates the Stripe coupon (and, in code mode, a promotion code), then saves our copy and writes an audit " +
                            "row. Max uses, the claim end, the covered plans and new-coaches-only are pushed to Stripe; when every " +
                            "purchasable plan (active, paid, priced in Stripe) is covered the coupon has no product restriction, so " +
                            "it also discounts the second-role add-on. " +
                            "One use per coach and the claim start are enforced by Form Up only. A Stripe failure saves nothing.";
            s.Response<PromotionDto>(StatusCodes.Status201Created, "The promotion that was created");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest,
                "Invalid request: name, value, code, month count, claim window, plans or max uses " +
                "(PROMOTION_VALUE_INVALID, PROMOTION_CODE_INVALID, PROMOTION_MODE_INVALID, PROMOTION_MONTHS_INVALID, " +
                "PROMOTION_WINDOW_INVALID, PROMOTION_PLANS_INVALID, PROMOTION_MAX_USES_INVALID)");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or invalid credentials");
            s.Response(StatusCodes.Status403Forbidden, "Caller is not an Admin");
            s.Response<ProblemDetails>(StatusCodes.Status409Conflict,
                "Another promotion that has not ended uses this code (PROMOTION_CODE_TAKEN)");
            s.Response<ProblemDetails>(StatusCodes.Status502BadGateway,
                "Stripe failed; nothing was saved (BILLING_PROVIDER_ERROR)");
            s.Response<ProblemDetails>(StatusCodes.Status503ServiceUnavailable,
                "Stripe is not configured on this server (BILLING_NOT_CONFIGURED)");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(CreatePromotionRequest req, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(AppClaims.UserId), out var adminId))
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

        var now = timeProvider.GetUtcNow();
        var plans = await LoadPaidPlansAsync(req.PlanCodes, ct);

        if (plans is null)
        {
            return;
        }

        var normalizedCode = req.Mode == PromotionMode.Code ? req.Code!.ToUpperInvariant() : null;

        if (normalizedCode is not null && await IsCodeTakenAsync(normalizedCode, now, ct))
        {
            await this.SendProblemAsync(
                StatusCodes.Status409Conflict, ErrorCodes.PromotionCodeTaken, "Another promotion already uses this code.", ct);
            return;
        }

        var publicId = Guid.NewGuid();
        var ids = await CreateInStripeAsync(req, plans, publicId, normalizedCode, ct);

        if (ids is null)
        {
            return;
        }

        var promotion = new Promotion
        {
            PublicId = publicId,
            Name = req.Name.Trim(),
            Kind = req.Kind,
            Value = req.Value,
            Currency = req.Kind == PromotionKind.FixedAmount ? CreatePromotionValidator.FixedAmountCurrency : null,
            Mode = req.Mode,
            Code = req.Mode == PromotionMode.Code ? req.Code : null,
            CodeNormalized = normalizedCode,
            Duration = req.Duration,
            DurationMonths = req.Duration == PromotionDuration.Months ? req.DurationMonths : null,
            ClaimFrom = req.ClaimFrom,
            ClaimUntil = req.ClaimUntil,
            PlanCodes = plans.Selected.Select(plan => plan.Code).ToList(),
            CoversSecondRoleAddOn = plans.CoversEveryPurchasablePlan,
            MaxUses = req.MaxUses,
            NewCoachesOnly = req.NewCoachesOnly,
            OneUsePerCoach = req.OneUsePerCoach,
            StripeCouponId = ids.CouponId,
            StripePromotionCodeId = ids.PromotionCodeId,
        };

        db.Promotions.Add(promotion);
        await db.SaveChangesAsync(ct);

        await audit.LogAsync(
            adminId,
            AdminPromotionAuditActions.Create,
            nameof(Promotion),
            promotion.PublicId,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            newValues: AuditPayload.WithReason(
                new Dictionary<string, object?>
                {
                    ["name"] = promotion.Name,
                    ["kind"] = promotion.Kind.ToString(),
                    ["value"] = promotion.Value,
                    ["mode"] = promotion.Mode.ToString(),
                    ["duration"] = promotion.Duration.ToString(),
                    ["claimFrom"] = promotion.ClaimFrom.ToString("O"),
                    ["planCount"] = promotion.PlanCodes.Count,
                },
                null),
            ct: ct);

        await Send.ResponseAsync(PromotionDto.FromEntity(promotion, 0, now), StatusCodes.Status201Created, ct);
    }

    /// <summary>
    /// Loads the active paid plans and resolves the selection (<see cref="PromotionPlanScope"/>). Answers 400
    /// <see cref="ErrorCodes.PromotionPlansInvalid"/> and returns null when a code is not an active paid plan or a plan
    /// has no Stripe price to restrict the coupon to.
    /// </summary>
    private async Task<SelectedPlans?> LoadPaidPlansAsync(List<string> requestedCodes, CancellationToken ct)
    {
        var paid = await db.SubscriptionPlanPrices
            .AsNoTracking()
            .Where(price => price.ValidTo == null
                            && price.SubscriptionPlan.IsActive
                            && price.SubscriptionPlan.Code != SubscriptionPlanCodes.Free
                            && price.PriceMinorUnits > 0)
            .Select(price => new PromotionPlanOption(price.SubscriptionPlan.Code, price.ExternalPriceId))
            .ToListAsync(ct);

        if (PromotionPlanScope.Resolve(paid, requestedCodes) is not var (selected, coversEveryPurchasablePlan))
        {
            this.ThrowErrorWithCode(ErrorCodes.PromotionPlansInvalid, "Select active paid plans that are priced in Stripe.");
            return null;
        }

        return new SelectedPlans(selected, coversEveryPurchasablePlan);
    }

    /// <summary>
    /// True when a promotion that has not ended already uses the code. Ended promotions free their code up.
    /// </summary>
    private async Task<bool> IsCodeTakenAsync(string normalizedCode, DateTimeOffset now, CancellationToken ct)
    {
        var sameCode = await db.Promotions
            .AsNoTracking()
            .Where(promotion => promotion.CodeNormalized == normalizedCode)
            .ToListAsync(ct);

        if (sameCode.Count == 0)
        {
            return false;
        }

        var ids = sameCode.Select(promotion => promotion.Id).ToList();
        var usedCounts = await db.PromotionRedemptions
            .AsNoTracking()
            .Where(r => ids.Contains(r.PromotionId))
            .GroupBy(r => r.PromotionId)
            .Select(group => new { PromotionId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.PromotionId, row => row.Count, ct);

        return sameCode.Any(promotion =>
            PromotionRules.StatusAt(promotion, usedCounts.GetValueOrDefault(promotion.Id), now) != PromotionStatus.Ended);
    }

    /// <summary>
    /// Creates the coupon and, in code mode, the promotion code. When the code fails the new coupon is deleted so no
    /// orphan is left behind. Answers 502 and returns null on a Stripe failure.
    /// </summary>
    private async Task<StripeIds?> CreateInStripeAsync(
        CreatePromotionRequest req, SelectedPlans plans, Guid publicId, string? normalizedCode, CancellationToken ct)
    {
        string couponId;

        try
        {
            couponId = await subscriptions.CreateCouponAsync(
                new CouponSpec(
                    req.Name.Trim(),
                    req.Kind,
                    req.Value,
                    req.Kind == PromotionKind.FixedAmount ? CreatePromotionValidator.FixedAmountCurrency : null,
                    req.Duration,
                    req.Duration == PromotionDuration.Months ? req.DurationMonths : null,
                    req.MaxUses,
                    req.ClaimUntil,
                    plans.CoversEveryPurchasablePlan ? null : plans.Selected.Select(plan => plan.StripePriceId!).ToList(),
                    $"formup-coupon-{publicId}"),
                ct);
        }
        catch (BillingProviderException)
        {
            await RespondProviderFailedAsync(ct);
            return null;
        }

        if (normalizedCode is null)
        {
            return new StripeIds(couponId, null);
        }

        try
        {
            var promotionCodeId = await subscriptions.CreatePromotionCodeAsync(
                couponId, normalizedCode, req.ClaimUntil, req.NewCoachesOnly, ct);

            return new StripeIds(couponId, promotionCodeId);
        }
        catch (BillingProviderException)
        {
            await TryDeleteCouponAsync(couponId, ct);
            await RespondProviderFailedAsync(ct);
            return null;
        }
    }

    private Task RespondProviderFailedAsync(CancellationToken ct) =>
        this.SendProblemAsync(
            StatusCodes.Status502BadGateway,
            ErrorCodes.BillingProviderError,
            "The payment provider failed; nothing was saved.",
            ct);

    private async Task TryDeleteCouponAsync(string couponId, CancellationToken ct)
    {
        try
        {
            await subscriptions.DeleteCouponAsync(couponId, ct);
        }
        catch (BillingProviderException ex)
        {
            logger.LogWarning(ex, "Coupon {CouponId} could not be deleted after its promotion code failed", couponId);
        }
    }

    private sealed record SelectedPlans(IReadOnlyList<PromotionPlanOption> Selected, bool CoversEveryPurchasablePlan);

    private sealed record StripeIds(string CouponId, string? PromotionCodeId);
}
