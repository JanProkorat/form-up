using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Features.Billing.Shared;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Services.Billing;
using Microsoft.Extensions.Options;

namespace FormUp.Application.Features.Billing.PreviewSecondRoleAddOn;

/// <summary>
/// Previews, without changing anything, what adding the second-role add-on to the caller's own live subscription would
/// charge today. It is a POST only so the removed-coach-role gate covers it like the confirm step.
/// </summary>
/// <param name="db">Application database context.</param>
/// <param name="subscriptions">The payment provider seam.</param>
/// <param name="options">Billing settings, for the tax mode.</param>
/// <param name="timeProvider">Clock.</param>
[RemovedCoachRole(RemovedCoachRoleMode.Refuse)]
internal sealed class PreviewSecondRoleAddOnEndpoint(
    IApplicationDbContext db,
    ISubscriptionService subscriptions,
    IOptions<BillingOptions> options,
    TimeProvider timeProvider) : EndpointWithoutRequest<PreviewSecondRoleAddOnResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/billing/second-role/preview");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Preview adding the second role";
            s.Description = "Returns the plan and add-on lines, the new recurring total and today's prorated charge for the " +
                            "caller's own live subscription. Nothing is charged or changed.";
            s.Response<PreviewSecondRoleAddOnResponse>(StatusCodes.Status200OK, "The preview");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or unreadable caller claim");
            s.Response(StatusCodes.Status403Forbidden, "The caller is not a trainer or nutritionist, or the coach role was removed");
            s.Response(StatusCodes.Status409Conflict,
                "No live paid subscription (BILLING_NO_SUBSCRIPTION), an admin gift is in force (BILLING_GIFT_IN_FORCE), " +
                "the plan has no add-on (BILLING_ADD_ON_NOT_AVAILABLE) or the add-on is already paid (BILLING_ADD_ON_ALREADY_PAID)");
            s.Response(StatusCodes.Status502BadGateway, "Stripe failed (BILLING_PROVIDER_ERROR)");
            s.Response(StatusCodes.Status503ServiceUnavailable, "Stripe is not configured on this server (BILLING_NOT_CONFIGURED)");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(CancellationToken ct)
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

        if (subscription is null || await this.RespondIfAddOnNotPurchasableAsync(subscription, ct))
        {
            return;
        }

        var price = subscription.SubscriptionPlanPrice;
        var addOnAmount = price.SecondRoleAddOnPriceMinorUnits!.Value;
        SecondRoleAddOnPreview preview;

        try
        {
            preview = await subscriptions.PreviewSecondRoleAddOnAsync(
                subscription.ExternalCustomerId!,
                subscription.ExternalSubscriptionId!,
                subscription.SubscriptionPlan.Code,
                addOnAmount,
                price.Currency,
                subscription.SubscriptionPlan.BillingInterval,
                ct);
        }
        catch (BillingProviderException)
        {
            await this.RespondBillingProviderErrorAsync(ct);
            return;
        }

        await Send.OkAsync(
            new PreviewSecondRoleAddOnResponse
            {
                PlanAmountMinorUnits = price.PriceMinorUnits,
                AddOnAmountMinorUnits = addOnAmount,
                NewTotalMinorUnits = price.PriceMinorUnits + addOnAmount,
                BillingInterval = subscription.SubscriptionPlan.BillingInterval,
                Currency = price.Currency,
                TodayAmountMinorUnits = preview.TodayAmountMinorUnits,
                TodayTaxMinorUnits = preview.TodayTaxMinorUnits,
                PeriodEndsAt = subscription.CurrentPeriodEndsAt,
                TaxMode = options.Value.TaxMode,
            },
            ct);
    }
}
