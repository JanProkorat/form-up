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

namespace FormUp.Application.Features.Billing.ScheduleSwitchToFree;

/// <summary>
/// Switches the caller's own paid subscription to Free by ending it at the close of the current period. The paid plan
/// stays until then; the deleted webhook moves the coach to Free.
/// </summary>
/// <param name="db">Application database context.</param>
/// <param name="subscriptions">The payment provider seam.</param>
/// <param name="timeProvider">Clock.</param>
[RemovedCoachRole(RemovedCoachRoleMode.Refuse)]
internal sealed class ScheduleSwitchToFreeEndpoint(
    IApplicationDbContext db, ISubscriptionService subscriptions, TimeProvider timeProvider)
    : EndpointWithoutRequest<ScheduleSwitchToFreeResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/billing/switch-to-free");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Switch my paid subscription to Free at period end";
            s.Description = "Cancels the caller's own live paid subscription at the end of the current period. There is no undo " +
                            "here; the coach resumes through the billing portal.";
            s.Response<ScheduleSwitchToFreeResponse>(StatusCodes.Status200OK, "The cancellation is scheduled");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or unreadable caller claim");
            s.Response(StatusCodes.Status403Forbidden, "The caller is not a trainer or nutritionist, or the coach role was removed");
            s.Response(StatusCodes.Status409Conflict,
                "No live paid subscription (BILLING_NO_SUBSCRIPTION), an admin gift is in force (BILLING_GIFT_IN_FORCE), or a cancellation is already pending (BILLING_CANCELLATION_PENDING)");
            s.Response(StatusCodes.Status502BadGateway, "Stripe failed; nothing was saved (BILLING_PROVIDER_ERROR)");
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

        if (subscription is null)
        {
            return;
        }

        if (subscription.CancelAtPeriodEnd)
        {
            await this.SendProblemAsync(
                StatusCodes.Status409Conflict,
                ErrorCodes.BillingCancellationPending,
                "The subscription is already set to end at the close of the period.",
                ct);
            return;
        }

        try
        {
            await subscriptions.CancelAtPeriodEndAsync(subscription.ExternalSubscriptionId!, ct);
        }
        catch (BillingProviderException)
        {
            await this.RespondBillingProviderErrorAsync(ct);
            return;
        }

        // The plan stays until the deleted webhook; a scheduled downgrade is moot because the coach is leaving.
        subscription.CancelAtPeriodEnd = true;
        subscription.PendingSubscriptionPlanPriceId = null;
        await db.SaveChangesAsync(ct);

        await Send.OkAsync(new ScheduleSwitchToFreeResponse { EndsAt = subscription.CurrentPeriodEndsAt }, ct);
    }
}
