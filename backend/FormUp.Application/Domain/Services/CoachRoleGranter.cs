using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Domain.Services;

/// <summary>
/// Adds a coach role to a professional without saving, so a caller can commit it with its own changes.
/// </summary>
public static class CoachRoleGranter
{
    /// <summary>True for the two roles a coach can hold.</summary>
    /// <param name="role">The role name to test.</param>
    public static bool IsCoachRole(string? role) => role is AppRoles.Trainer or AppRoles.Nutritionist;

    /// <summary>
    /// Stages the role row (when the user lacks it), clears the role's removal marker and undoes a pending account disable.
    /// Safe to repeat: an existing role row is never duplicated. The caller saves.
    /// </summary>
    /// <param name="db">Database context.</param>
    /// <param name="profile">The tracked professional profile of the coach.</param>
    /// <param name="role">Trainer or Nutritionist.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>False when the role is not a coach role or does not exist in the role table; nothing is staged then.</returns>
    public static async Task<bool> GrantAsync(IApplicationDbContext db, ProfessionalProfile profile, string role, CancellationToken ct)
    {
        if (!IsCoachRole(role))
        {
            return false;
        }

        var roleId = await db.Roles
            .AsNoTracking()
            .Where(candidate => candidate.Name == role)
            .Select(candidate => (Guid?)candidate.Id)
            .FirstOrDefaultAsync(ct);

        if (roleId is null)
        {
            return false;
        }

        var alreadyHeld = await db.UserRoles
            .AnyAsync(userRole => userRole.UserId == profile.UserId && userRole.RoleId == roleId, ct);

        if (!alreadyHeld)
        {
            db.UserRoles.Add(new IdentityUserRole<Guid> { UserId = profile.UserId, RoleId = roleId.Value });
        }

        CoachRoleStatus.SetRemovedAt(profile, role, null);

        // The cancel flag is only ever set together with the disable date.
        if (profile.CoachAccountActiveUntil is not null)
        {
            profile.CoachAccountActiveUntil = null;

            var subscription = await db.CoachSubscriptions
                .FirstOrDefaultAsync(cs => cs.ProfessionalProfileId == profile.Id, ct);

            if (subscription is not null)
            {
                // An admin-cancelled renewal stays cancelled; only the coach's own disable is undone.
                subscription.CancelAtPeriodEnd = subscription.RenewalCancelledByAdminAt is not null;
            }
        }

        return true;
    }
}
