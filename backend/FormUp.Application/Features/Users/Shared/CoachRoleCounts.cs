using FormUp.Application.Domain.Constants;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Features.Users.Shared;

/// <summary>
/// Computes per-role client counts from link capability flags for the roles endpoints.
/// </summary>
internal static class CoachRoleCounts
{
    /// <summary>
    /// Builds one summary per role in <paramref name="activeRoles"/> from the profile's active links.
    /// A link is shared when it also grants the other discipline and the other role is active.
    /// </summary>
    /// <param name="db">Database context.</param>
    /// <param name="professionalProfileId">Internal id of the caller's professional profile.</param>
    /// <param name="activeRoles">The coach roles to summarise.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<List<CoachRoleSummaryDto>> LoadAsync(
        IApplicationDbContext db,
        long professionalProfileId,
        IReadOnlyList<string> activeRoles,
        CancellationToken ct)
    {
        var flags = await db.ClientProfessionalLinks
            .AsNoTracking()
            .Where(l => l.ProfessionalProfileId == professionalProfileId && l.ConnectionState != ConnectionState.Ended)
            .Select(l => new { l.CanViewTrainingPlans, l.CanViewNutritionPlans })
            .ToListAsync(ct);

        var trainerActive = activeRoles.Contains(AppRoles.Trainer);
        var nutritionistActive = activeRoles.Contains(AppRoles.Nutritionist);

        return activeRoles
            .Select(role => role == AppRoles.Trainer
                ? new CoachRoleSummaryDto
                {
                    Role = role,
                    ClientCount = flags.Count(f => f.CanViewTrainingPlans),
                    SharedWithOtherRoleCount = nutritionistActive
                        ? flags.Count(f => f.CanViewTrainingPlans && f.CanViewNutritionPlans)
                        : 0,
                }
                : new CoachRoleSummaryDto
                {
                    Role = role,
                    ClientCount = flags.Count(f => f.CanViewNutritionPlans),
                    SharedWithOtherRoleCount = trainerActive
                        ? flags.Count(f => f.CanViewNutritionPlans && f.CanViewTrainingPlans)
                        : 0,
                })
            .ToList();
    }
}
