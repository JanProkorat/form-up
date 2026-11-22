using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Services.Billing;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.Users.DeleteAccount;

/// <summary>
/// Endpoint for deleting the authenticated user's account and all associated data.
/// Implements GDPR right to be forgotten (Art. 17).
/// </summary>
/// <param name="userManager">ASP.NET Identity user manager.</param>
/// <param name="db">Database context.</param>
/// <param name="audit">Audit logging service.</param>
/// <param name="subscriptions">Payment provider subscriptions.</param>
public class DeleteAccountEndpoint(
    UserManager<ApplicationUser> userManager,
    IApplicationDbContext db,
    IAuditService audit,
    ISubscriptionService subscriptions) : EndpointWithoutRequest
{
    /// <inheritdoc />
    public override void Configure()
    {
        Delete("/users/me");
        Summary(s =>
        {
            s.Summary = "Delete current user account";
            s.Description = "Permanently deletes the authenticated user's account and all associated data (GDPR Art. 17). " +
                            "A coach's provider subscription is cancelled at once and its unused days are refunded; " +
                            "when the payment provider fails, nothing is deleted.";
            s.Response(StatusCodes.Status204NoContent, "Account deleted");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest, "Account deletion failed");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or unreadable caller claim");
            s.Response(StatusCodes.Status404NotFound, "User not found");
            s.Response(StatusCodes.Status502BadGateway, "The payment provider failed; nothing was deleted (BILLING_PROVIDER_ERROR)");
            s.Response(StatusCodes.Status503ServiceUnavailable, "A provider subscription must be cancelled and Stripe is not configured on this server (BILLING_NOT_CONFIGURED)");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(CancellationToken ct)
    {
        var userIdStr = User.FindFirstValue(AppClaims.UserId);

        if (userIdStr is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var userId = Guid.Parse(userIdStr);
        var user = await userManager.FindByIdAsync(userIdStr);

        if (user is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();

        var professionalProfile = await db.ProfessionalProfiles
            .FirstOrDefaultAsync(pp => pp.UserId == userId, ct);

        // Provider first, read untracked: a refusal must leave every row in place. Incomplete counts too, since a later
        // payment on it would leave a billable subscription nobody owns. The row itself goes with the profile (cascade).
        var cancelRefund = CancelRefundResult.None;
        var cancelsAtProvider = false;

        if (professionalProfile is not null)
        {
            var subscription = await db.CoachSubscriptions
                .AsNoTracking()
                .FirstOrDefaultAsync(cs => cs.ProfessionalProfileId == professionalProfile.Id, ct);

            if (subscription?.HasCancellableProviderSubscription ?? false)
            {
                var cancelled = await TryCancelAtProviderAsync(subscription.ExternalSubscriptionId!, ct);

                if (cancelled is null)
                {
                    return;
                }

                cancelRefund = cancelled;
                cancelsAtProvider = true;
            }
        }

        // Delete client profile and all related data (measurements, photos, links)
        var clientProfile = await db.ClientProfiles
            .FirstOrDefaultAsync(cp => cp.UserId == userId, ct);

        if (clientProfile is not null)
        {
            var measurements = await db.BodyMeasurements
                .Where(bm => bm.ClientProfileId == clientProfile.Id)
                .ToListAsync(ct);
            db.BodyMeasurements.RemoveRange(measurements);

            var photos = await db.PlanPhotos
                .Where(pp => pp.ClientProfileId == clientProfile.Id)
                .ToListAsync(ct);
            db.PlanPhotos.RemoveRange(photos);

            var clientLinks = await db.ClientProfessionalLinks
                .Where(cpl => cpl.ClientProfileId == clientProfile.Id)
                .ToListAsync(ct);
            db.ClientProfessionalLinks.RemoveRange(clientLinks);

            db.ClientProfiles.Remove(clientProfile);
        }

        // Delete professional profile and all related data (invitations, links)
        if (professionalProfile is not null)
        {
            var invitations = await db.InvitationTokens
                .Where(it => it.ProfessionalProfileId == professionalProfile.Id)
                .ToListAsync(ct);
            db.InvitationTokens.RemoveRange(invitations);

            var professionalLinks = await db.ClientProfessionalLinks
                .Where(cpl => cpl.ProfessionalProfileId == professionalProfile.Id)
                .ToListAsync(ct);
            db.ClientProfessionalLinks.RemoveRange(professionalLinks);

            // Terms reference the coach's packages and discounts with RESTRICT, so the payment assignments
            // (which cascade to terms and periods) go first, in the same batch as the profile.
            var paymentAssignments = await db.ClientPaymentAssignments
                .Where(assignment => assignment.ProfessionalProfileId == professionalProfile.Id)
                .ToListAsync(ct);
            db.ClientPaymentAssignments.RemoveRange(paymentAssignments);

            db.ProfessionalProfiles.Remove(professionalProfile);
        }

        // Delete refresh tokens
        var refreshTokens = await db.RefreshTokens
            .Where(rt => rt.UserId == userId)
            .ToListAsync(ct);
        db.RefreshTokens.RemoveRange(refreshTokens);

        // Coach events keep the Reports funnel whole: the FK nulls the user id, so give all of this coach's events one
        // random key (not derived from the user id) in the same save as the rest of the deletion.
        var coachEvents = await db.CoachEvents
            .Where(coachEvent => coachEvent.CoachUserId == userId)
            .ToListAsync(ct);

        if (coachEvents.Count > 0)
        {
            var anonymousCoachKey = Guid.NewGuid();

            foreach (var coachEvent in coachEvents)
            {
                coachEvent.AnonymousCoachKey = anonymousCoachKey;
            }
        }

        await db.SaveChangesAsync(ct);

        // Delete the Identity user (cascades to user_roles, user_claims, user_logins, user_tokens)
        var result = await userManager.DeleteAsync(user);

        if (!result.Succeeded)
        {
            ThrowError("Account deletion failed.");
            return;
        }

        // Audit log the deletion (user is gone, record it for compliance)
        await audit.LogAsync(
            userId,
            "Delete",
            nameof(ApplicationUser),
            entityId: userId,
            ipAddress: ipAddress,
            newValues: cancelsAtProvider
                ? AuditPayload.WithReason(
                    new Dictionary<string, object?>
                    {
                        ["providerSubscriptionCancelled"] = true,
                        ["refundedMinorUnits"] = cancelRefund.RefundedMinorUnits,
                        ["refundCurrency"] = cancelRefund.Currency,
                    },
                    null)
                : null,
            ct: ct);

        await Send.NoContentAsync(ct);
    }

    /// <summary>
    /// Cancels the subscription at the payment provider now and refunds its unused days. Answers 503 or 502 and returns
    /// null when it could not.
    /// </summary>
    private async Task<CancelRefundResult?> TryCancelAtProviderAsync(string externalSubscriptionId, CancellationToken ct)
    {
        if (!subscriptions.IsConfigured)
        {
            await this.SendProblemAsync(
                StatusCodes.Status503ServiceUnavailable, ErrorCodes.BillingNotConfigured, "Billing is not configured.", ct);
            return null;
        }

        try
        {
            return await subscriptions.CancelNowAsync(externalSubscriptionId, ct);
        }
        catch (BillingProviderException)
        {
            await this.SendProblemAsync(
                StatusCodes.Status502BadGateway, ErrorCodes.BillingProviderError, "The payment provider failed.", ct);
            return null;
        }
    }
}
