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
                            "A coach's provider subscription is cancelled at once and its unused days are refunded, then the " +
                            "provider customer is deleted; when the payment provider fails, nothing is deleted.";
            s.Response(StatusCodes.Status204NoContent, "Account deleted");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest, "Account deletion failed");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or unreadable caller claim");
            s.Response(StatusCodes.Status404NotFound, "User not found");
            s.Response(StatusCodes.Status502BadGateway, "The payment provider failed; nothing was deleted (BILLING_PROVIDER_ERROR)");
            s.Response(StatusCodes.Status503ServiceUnavailable, "A provider subscription must be cancelled or a provider customer deleted and Stripe is not configured on this server (BILLING_NOT_CONFIGURED)");
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
        var customerDeleted = false;

        if (professionalProfile is not null)
        {
            var subscription = await db.CoachSubscriptions
                .AsNoTracking()
                .FirstOrDefaultAsync(cs => cs.ProfessionalProfileId == professionalProfile.Id, ct);

            var cancellableId = subscription?.HasCancellableProviderSubscription ?? false
                ? subscription.ExternalSubscriptionId
                : null;

            // A marker left by an earlier failed attempt still owes a refund, even once the provider's webhook has
            // cleared the live subscription id.
            var pendingId = subscription?.PendingRefundSubscriptionId;

            if (subscription is not null && (cancellableId is not null || pendingId is not null))
            {
                var settled = await TrySettleAtProviderAsync(subscription.Id, pendingId, cancellableId, ct);

                if (settled is null)
                {
                    return;
                }

                cancelRefund = settled;
                cancelsAtProvider = true;
            }

            // After the cancel and refund: deleting a customer makes Stripe cancel its subscriptions without our refund logic.
            // The customer goes for GDPR cleanliness; Stripe keeps invoices and charges for its own legal records, and our
            // admin reports read stored snapshots, so reporting is unaffected.
            if (subscription?.ExternalCustomerId is { } customerId)
            {
                if (!await TryDeleteCustomerAsync(customerId, ct))
                {
                    return;
                }

                customerDeleted = true;
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

        // The row changes and the Identity delete commit together: a failed delete rolls everything back. Identity's
        // store and db share one scoped context, so both enlist in this transaction.
        await using var transaction = await db.BeginTransactionAsync(ct);

        await db.SaveChangesAsync(ct);

        // Delete the Identity user (cascades to user_roles, user_claims, user_logins, user_tokens)
        var result = await userManager.DeleteAsync(user);

        if (!result.Succeeded)
        {
            ThrowError("Account deletion failed.");
            return;
        }

        await transaction.CommitAsync(ct);

        // Audit log the deletion (user is gone, record it for compliance)
        await audit.LogAsync(
            userId,
            "Delete",
            nameof(ApplicationUser),
            entityId: userId,
            ipAddress: ipAddress,
            newValues: cancelsAtProvider || customerDeleted
                ? AuditPayload.WithReason(ProviderAuditFields(cancelsAtProvider, cancelRefund, customerDeleted), null)
                : null,
            ct: ct);

        await Send.NoContentAsync(ct);
    }

    private static Dictionary<string, object?> ProviderAuditFields(
        bool cancelsAtProvider, CancelRefundResult cancelRefund, bool customerDeleted)
    {
        var fields = new Dictionary<string, object?>();

        if (cancelsAtProvider)
        {
            fields["providerSubscriptionCancelled"] = true;
            fields["refundedMinorUnits"] = cancelRefund.RefundedMinorUnits;
            fields["refundCurrency"] = cancelRefund.Currency;
        }

        if (customerDeleted)
        {
            fields["providerCustomerDeleted"] = true;
        }

        return fields;
    }

    /// <summary>
    /// Deletes the provider customer. Answers 503 or 502 and returns false when it could not.
    /// </summary>
    private async Task<bool> TryDeleteCustomerAsync(string customerId, CancellationToken ct)
    {
        if (!subscriptions.IsConfigured)
        {
            await this.SendProblemAsync(
                StatusCodes.Status503ServiceUnavailable, ErrorCodes.BillingNotConfigured, "Billing is not configured.", ct);
            return false;
        }

        try
        {
            await subscriptions.DeleteCustomerAsync(customerId, ct);
        }
        catch (BillingProviderException)
        {
            await this.SendProblemAsync(
                StatusCodes.Status502BadGateway, ErrorCodes.BillingProviderError, "The payment provider failed.", ct);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Settles the pending marker id and the live subscription id at the payment provider, each at most once: cancels
    /// now and refunds unused days (an already-cancelled subscription is only refunded). The live id is written to the
    /// marker first, so a retry can still refund after the provider's webhook cleared it. Answers 503 or 502 and
    /// returns null when it could not.
    /// </summary>
    private async Task<CancelRefundResult?> TrySettleAtProviderAsync(
        long subscriptionRowId, string? pendingId, string? cancellableId, CancellationToken ct)
    {
        if (!subscriptions.IsConfigured)
        {
            await this.SendProblemAsync(
                StatusCodes.Status503ServiceUnavailable, ErrorCodes.BillingNotConfigured, "Billing is not configured.", ct);
            return null;
        }

        var refundedMinorUnits = 0L;
        string? currency = null;

        try
        {
            if (pendingId is not null && pendingId != cancellableId)
            {
                var earlier = await subscriptions.CancelNowAsync(pendingId, ct);
                refundedMinorUnits += earlier.RefundedMinorUnits;
                currency ??= earlier.Currency;
            }

            if (cancellableId is not null)
            {
                if (pendingId != cancellableId)
                {
                    await db.CoachSubscriptions
                        .Where(cs => cs.Id == subscriptionRowId)
                        .ExecuteUpdateAsync(setters => setters.SetProperty(cs => cs.PendingRefundSubscriptionId, cancellableId), ct);
                }

                var current = await subscriptions.CancelNowAsync(cancellableId, ct);
                refundedMinorUnits += current.RefundedMinorUnits;
                currency ??= current.Currency;
            }
        }
        catch (BillingProviderException)
        {
            await this.SendProblemAsync(
                StatusCodes.Status502BadGateway, ErrorCodes.BillingProviderError, "The payment provider failed.", ct);
            return null;
        }

        return new CancelRefundResult(refundedMinorUnits, currency);
    }
}
