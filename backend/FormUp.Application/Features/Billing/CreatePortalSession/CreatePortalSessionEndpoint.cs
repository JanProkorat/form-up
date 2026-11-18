using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Services.Billing;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.Billing.CreatePortalSession;

/// <summary>
/// Opens the Stripe Customer Portal for the caller's own Stripe customer. The customer id is read from the caller's
/// subscription, never from the request, and the return URL comes from configuration.
/// </summary>
/// <param name="db">Application database context.</param>
/// <param name="subscriptions">The payment provider seam.</param>
[RemovedCoachRole(RemovedCoachRoleMode.Exempt, Reason = "A coach who removed their role may still need to manage or cancel what they pay for.")]
internal sealed class CreatePortalSessionEndpoint(IApplicationDbContext db, ISubscriptionService subscriptions)
    : EndpointWithoutRequest<CreatePortalSessionResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/billing/portal-session");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Open the Stripe Customer Portal";
            s.Description = "Creates a Customer Portal session for the caller's own Stripe customer so they can manage payment " +
                            "methods, invoices and the subscription.";
            s.Response<CreatePortalSessionResponse>(StatusCodes.Status200OK, "The portal page to redirect to");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or unreadable caller claim");
            s.Response(StatusCodes.Status403Forbidden, "The caller is not a trainer or nutritionist");
            s.Response(StatusCodes.Status404NotFound, "The coach has no Stripe customer yet (BILLING_CUSTOMER_NOT_FOUND)");
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

        if (!subscriptions.IsConfigured)
        {
            await this.SendProblemAsync(
                StatusCodes.Status503ServiceUnavailable, ErrorCodes.BillingNotConfigured, "Billing is not configured.", ct);
            return;
        }

        var coach = await db.CoachSubscriptions
            .AsNoTracking()
            .Where(cs => cs.ProfessionalProfile.UserId == userId)
            .Select(cs => new { cs.ExternalCustomerId, cs.ProfessionalProfile.User.Language })
            .FirstOrDefaultAsync(ct);

        if (coach?.ExternalCustomerId is null)
        {
            await this.SendProblemAsync(
                StatusCodes.Status404NotFound, ErrorCodes.BillingCustomerNotFound, "The coach has no billing customer yet.", ct);
            return;
        }

        try
        {
            var url = await subscriptions.CreatePortalSessionAsync(coach.ExternalCustomerId, coach.Language, ct);

            await Send.OkAsync(new CreatePortalSessionResponse { Url = url }, ct);
        }
        catch (BillingProviderException)
        {
            await this.SendProblemAsync(
                StatusCodes.Status502BadGateway, ErrorCodes.BillingProviderError, "The payment provider failed.", ct);
        }
    }
}
