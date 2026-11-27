using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.Billing.Shared;

/// <summary>
/// Guards and responses shared by the billing endpoints that act on the caller's own live subscription.
/// </summary>
internal static class BillingEndpointExtensions
{
    /// <summary>
    /// Answers 503 <see cref="ErrorCodes.BillingNotConfigured"/> when the payment provider has no keys. Returns true when
    /// it answered, so the caller returns.
    /// </summary>
    /// <param name="endpoint">The endpoint instance.</param>
    /// <param name="subscriptions">The payment provider seam.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<bool> RespondIfBillingNotConfiguredAsync(
        this IEndpoint endpoint, ISubscriptionService subscriptions, CancellationToken ct)
    {
        if (subscriptions.IsConfigured)
        {
            return false;
        }

        await endpoint.SendProblemAsync(
            StatusCodes.Status503ServiceUnavailable, ErrorCodes.BillingNotConfigured, "Billing is not configured.", ct);

        return true;
    }

    /// <summary>
    /// Answers 502 <see cref="ErrorCodes.BillingProviderError"/> after a failed provider call.
    /// </summary>
    /// <param name="endpoint">The endpoint instance.</param>
    /// <param name="ct">Cancellation token.</param>
    public static Task RespondBillingProviderErrorAsync(this IEndpoint endpoint, CancellationToken ct) =>
        endpoint.SendProblemAsync(
            StatusCodes.Status502BadGateway, ErrorCodes.BillingProviderError, "The payment provider failed.", ct);

    /// <summary>
    /// Answers 409 when the subscription has no second-role add-on left to buy: the plan includes both roles or has no
    /// add-on price (<see cref="ErrorCodes.BillingAddOnNotAvailable"/>), or the coach already pays for it
    /// (<see cref="ErrorCodes.BillingAddOnAlreadyPaid"/>). Returns true when it answered, so the caller returns.
    /// </summary>
    /// <param name="endpoint">The endpoint instance.</param>
    /// <param name="subscription">The caller's subscription row with plan and price loaded.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<bool> RespondIfAddOnNotPurchasableAsync(
        this IEndpoint endpoint, CoachSubscription subscription, CancellationToken ct)
    {
        if (subscription.PaysSecondRoleAddOn)
        {
            await endpoint.SendProblemAsync(
                StatusCodes.Status409Conflict, ErrorCodes.BillingAddOnAlreadyPaid, "The second-role add-on is already paid.", ct);

            return true;
        }

        if (subscription.SubscriptionPlan.BothRolesIncluded
            || subscription.SubscriptionPlanPrice.SecondRoleAddOnPriceMinorUnits is null or <= 0
            || subscription.ExternalCustomerId is null)
        {
            await endpoint.SendProblemAsync(
                StatusCodes.Status409Conflict,
                ErrorCodes.BillingAddOnNotAvailable,
                "This plan has no second-role add-on to buy.",
                ct);

            return true;
        }

        return false;
    }

    /// <summary>
    /// Loads the caller's own subscription row with its plan and price, tracked for update, when it is a live provider
    /// subscription and no admin gift is in force. Otherwise answers 409 (<see cref="ErrorCodes.BillingGiftInForce"/> or
    /// <see cref="ErrorCodes.BillingNoSubscription"/>) and returns null.
    /// </summary>
    /// <param name="endpoint">The endpoint instance.</param>
    /// <param name="db">Application database context.</param>
    /// <param name="userId">The caller's <c>ApplicationUser.Id</c>, from the token.</param>
    /// <param name="now">The current instant.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<CoachSubscription?> LoadLiveSubscriptionOrRespondAsync(
        this IEndpoint endpoint, IApplicationDbContext db, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        var subscription = await db.CoachSubscriptions
            .Include(cs => cs.SubscriptionPlan)
            .Include(cs => cs.SubscriptionPlanPrice)
            .FirstOrDefaultAsync(cs => cs.ProfessionalProfile.UserId == userId, ct);

        if (subscription is not null && subscription.IsGiftInForce(now))
        {
            await endpoint.SendProblemAsync(
                StatusCodes.Status409Conflict,
                ErrorCodes.BillingGiftInForce,
                "The plan was given by Form Up and cannot be changed here.",
                ct);

            return null;
        }

        if (subscription is null || !subscription.HasLiveProviderSubscription)
        {
            await endpoint.SendProblemAsync(
                StatusCodes.Status409Conflict, ErrorCodes.BillingNoSubscription, "There is no live paid subscription.", ct);

            return null;
        }

        return subscription;
    }
}
