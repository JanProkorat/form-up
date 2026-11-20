using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Infrastructure.Data;
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
public class DeleteAccountEndpoint(
    UserManager<ApplicationUser> userManager,
    IApplicationDbContext db,
    IAuditService audit) : EndpointWithoutRequest
{
    /// <inheritdoc />
    public override void Configure()
    {
        Delete("/users/me");
        Summary(s =>
        {
            s.Summary = "Delete current user account";
            s.Description = "Permanently deletes the authenticated user's account and all associated data (GDPR Art. 17).";
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
        var professionalProfile = await db.ProfessionalProfiles
            .FirstOrDefaultAsync(pp => pp.UserId == userId, ct);

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
            ct: ct);

        await Send.NoContentAsync(ct);
    }
}
