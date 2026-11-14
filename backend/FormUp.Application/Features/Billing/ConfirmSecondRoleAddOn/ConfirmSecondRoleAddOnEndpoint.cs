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

namespace FormUp.Application.Features.Billing.ConfirmSecondRoleAddOn;

/// <summary>
/// Charges the second-role add-on to the caller's own live subscription now. A declined card or an authentication
/// challenge applies nothing and answers 402. The role itself is added by the existing add-role endpoint.
/// </summary>
/// <param name="db">Application database context.</param>
/// <param name="subscriptions">The payment provider seam.</param>
/// <param name="timeProvider">Clock.</param>
[RemovedCoachRole(RemovedCoachRoleMode.Refuse)]
internal sealed class ConfirmSecondRoleAddOnEndpoint(
    IApplicationDbContext db, ISubscriptionService subscriptions, TimeProvider timeProvider)
    : Endpoint<ConfirmSecondRoleAddOnRequest, ConfirmSecondRoleAddOnResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/billing/second-role/confirm");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Pay for the second role";
            s.Description = "Adds the second-role add-on to the caller's own live subscription and charges the prorated amount " +
                            "immediately. Idempotent per attempt id. Does not create the role: the client then calls POST /users/me/roles.";
            s.Response<ConfirmSecondRoleAddOnResponse>(StatusCodes.Status200OK, "The add-on is paid");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest, "Invalid body");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or unreadable caller claim");
            s.Response(StatusCodes.Status402PaymentRequired,
                "The card was declined (BILLING_PAYMENT_DECLINED) or needs authentication (BILLING_PAYMENT_AUTHENTICATION_REQUIRED); nothing was applied");
            s.Response(StatusCodes.Status403Forbidden, "The caller is not a trainer or nutritionist, or the coach role was removed");
            s.Response(StatusCodes.Status409Conflict,
                "No live paid subscription (BILLING_NO_SUBSCRIPTION), an admin gift is in force (BILLING_GIFT_IN_FORCE), " +
                "the plan has no add-on (BILLING_ADD_ON_NOT_AVAILABLE), the add-on is already paid (BILLING_ADD_ON_ALREADY_PAID) " +
                "or a plan change is scheduled (BILLING_PLAN_CHANGE_PENDING)");
            s.Response(StatusCodes.Status502BadGateway, "Stripe failed; nothing was saved (BILLING_PROVIDER_ERROR)");
            s.Response(StatusCodes.Status503ServiceUnavailable, "Stripe is not configured on this server (BILLING_NOT_CONFIGURED)");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(ConfirmSecondRoleAddOnRequest req, CancellationToken ct)
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

        if (subscription.PendingSubscriptionPlanPriceId is not null)
        {
            await this.SendProblemAsync(
                StatusCodes.Status409Conflict,
                ErrorCodes.BillingPlanChangePending,
                "A plan change is scheduled for the next renewal; add the second role after it applies.",
                ct);
            return;
        }

        AddOnChargeOutcome outcome;

        try
        {
            outcome = await subscriptions.ChargeSecondRoleAddOnAsync(
                subscription.ExternalSubscriptionId!,
                subscription.SubscriptionPlan.Code,
                subscription.SubscriptionPlanPrice.SecondRoleAddOnPriceMinorUnits!.Value,
                subscription.SubscriptionPlanPrice.Currency,
                subscription.SubscriptionPlan.BillingInterval,
                $"formup-second-role-{subscription.ExternalSubscriptionId}-{req.AttemptId:N}",
                ct);
        }
        catch (BillingProviderException)
        {
            await this.RespondBillingProviderErrorAsync(ct);
            return;
        }

        if (outcome != AddOnChargeOutcome.Charged)
        {
            var authenticationRequired = outcome == AddOnChargeOutcome.AuthenticationRequired;

            await this.SendProblemAsync(
                StatusCodes.Status402PaymentRequired,
                authenticationRequired ? ErrorCodes.BillingPaymentAuthenticationRequired : ErrorCodes.BillingPaymentDeclined,
                authenticationRequired ? "The card needs authentication." : "The card was declined.",
                ct);
            return;
        }

        // Our own row is written now; LastExternalEventAt stays, so the webhook that follows still applies.
        subscription.PaysSecondRoleAddOn = true;
        await db.SaveChangesAsync(ct);

        await Send.OkAsync(new ConfirmSecondRoleAddOnResponse { PaysSecondRoleAddOn = true }, ct);
    }
}
