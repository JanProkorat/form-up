using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Domain.Services;

/// <summary>
/// Disables a coach account: it stays active until the paid period ends, or ends at once when there is no paid
/// period. Shared by the coach's own action and the admin action; each writes its own audit row.
/// </summary>
/// <param name="userManager">ASP.NET Identity user manager.</param>
/// <param name="db">Database context.</param>
/// <param name="coachRoleStatus">Resolves which coach roles are active.</param>
/// <param name="coachRoleRemover">Removes the coach roles when the account ends now.</param>
/// <param name="audit">Audit logging service.</param>
/// <param name="timeProvider">Clock.</param>
public class CoachAccountDisabler(
    UserManager<ApplicationUser> userManager,
    IApplicationDbContext db,
    ICoachRoleStatus coachRoleStatus,
    ICoachRoleRemover coachRoleRemover,
    IAuditService audit,
    TimeProvider timeProvider)
{
    /// <summary>Audit action when the coach disables their own account.</summary>
    public const string SelfAuditAction = "DisableCoachAccount";

    /// <summary>Audit action when an admin disables a coach account.</summary>
    public const string AdminAuditAction = "AdminDisableCoachAccount";

    /// <summary>
    /// Disables the coach account. Repeating the call while the end date is still ahead returns the same date,
    /// removes nothing and writes no second audit row. The audit row is filed under the coach, with
    /// <paramref name="actorUserId"/> as the user who acted.
    /// </summary>
    /// <param name="coachUserId">The coach's <c>ApplicationUser.Id</c>.</param>
    /// <param name="actorUserId">The user who acted: the coach themselves or an admin.</param>
    /// <param name="reason">The admin's reason; null for the coach's own action.</param>
    /// <param name="ipAddress">The caller's IP address.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The outcome, or null when the user has no active coach role.</returns>
    public async Task<CoachAccountDisableResult?> DisableAsync(
        Guid coachUserId, Guid actorUserId, string? reason, string? ipAddress, CancellationToken ct)
    {
        var user = await userManager.FindByIdAsync(coachUserId.ToString());
        var profile = await db.ProfessionalProfiles.FirstOrDefaultAsync(p => p.UserId == coachUserId, ct);

        if (user is null || profile is null)
        {
            return null;
        }

        var activeRoles = await coachRoleStatus.GetActiveRolesAsync(user, profile, ct);

        if (activeRoles.Count == 0)
        {
            return null;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;

        if (profile.CoachAccountActiveUntil is { } pendingUntil && pendingUntil > now)
        {
            return new CoachAccountDisableResult(pendingUntil, []);
        }

        var subscription = await db.CoachSubscriptions
            .FirstOrDefaultAsync(cs => cs.ProfessionalProfileId == profile.Id, ct);

        // A date that already passed (the sweeper has not run yet) resolves to now and ends the account.
        var activeUntil = profile.CoachAccountActiveUntil is { } passed
            ? passed
            : CoachAccountActiveUntilResolver.Resolve(subscription, now);

        profile.CoachAccountActiveUntil = activeUntil;

        if (subscription is not null)
        {
            subscription.CancelAtPeriodEnd = true;
        }

        await db.SaveChangesAsync(ct);

        IReadOnlyList<string> rolesRemoved = [];

        if (activeUntil <= now)
        {
            rolesRemoved = await coachRoleRemover.RemoveAsync(user, profile.Id, activeRoles, now, ct);
        }

        var byCoach = actorUserId == coachUserId;
        var fields = new Dictionary<string, object?> { ["activeUntil"] = activeUntil.ToString("O") };

        await audit.LogAsync(
            actorUserId,
            byCoach ? SelfAuditAction : AdminAuditAction,
            nameof(ApplicationUser),
            coachUserId,
            ipAddress,
            newValues: AuditPayload.WithReason(fields, reason),
            ct: ct);

        return new CoachAccountDisableResult(activeUntil, rolesRemoved.ToList());
    }
}
