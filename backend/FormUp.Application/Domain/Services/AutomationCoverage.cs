using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Domain.Services;

/// <summary>
/// Decides which clients a coach's automations may reach. A client is covered only while the coach
/// holds a live link to them that grants at least one domain; a link that grants nothing is never
/// counted and never receives a send.
/// </summary>
public static class AutomationCoverage
{
    /// <summary>
    /// Whether the capabilities of a live link make the client reachable by automations.
    /// </summary>
    public static bool IsCovered(LinkCapabilities capabilities) => !capabilities.GrantsNothing;

    /// <summary>
    /// Loads every client the coach's automations may reach, with the link facts the endpoints and
    /// the scheduler need.
    /// </summary>
    /// <param name="db">Application database context.</param>
    /// <param name="professionalProfileId">The coach's professional profile id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="excludeRemovedRoles">
    /// Whether links of a coach role that was removed are left out. A removed role keeps its links
    /// active until the plans end, but nothing may be sent through it.
    /// </param>
    /// <param name="onlyClientProfileId">Restricts the result to one client.</param>
    public static async Task<List<AutomationCoveredClient>> LoadCoveredClientsAsync(
        IApplicationDbContext db,
        long professionalProfileId,
        CancellationToken ct,
        bool excludeRemovedRoles = false,
        long? onlyClientProfileId = null)
    {
        var linkQuery = db.ClientProfessionalLinks
            .AsNoTracking()
            .Where(l => l.ProfessionalProfileId == professionalProfileId && l.ConnectionState != ConnectionState.Ended);

        if (onlyClientProfileId is not null)
        {
            linkQuery = linkQuery.Where(l => l.ClientProfileId == onlyClientProfileId.Value);
        }

        var links = await linkQuery
            .Select(l => new LinkRow(
                l.Id,
                l.ClientProfileId,
                l.ClientProfile.UserId,
                l.ClientProfile.User.TimeZone,
                l.DateCreated,
                l.ActivatedAtUtc,
                l.ProfessionalRole,
                l.CanViewNutritionPlans,
                l.CanViewTrainingPlans))
            .ToListAsync(ct);

        if (excludeRemovedRoles && links.Count > 0)
        {
            var removal = await db.ProfessionalProfiles
                .AsNoTracking()
                .Where(p => p.Id == professionalProfileId)
                .Select(p => new { p.TrainerRoleRemovedAt, p.NutritionistRoleRemovedAt })
                .FirstOrDefaultAsync(ct);

            links = links
                .Where(l => removal is not null && !IsRoleRemoved(l.ProfessionalRole, removal.TrainerRoleRemovedAt, removal.NutritionistRoleRemovedAt))
                .ToList();
        }

        var clientLinks = links
            .GroupBy(l => l.ClientProfileId)
            .Where(g => g.Any(l => IsCovered(new LinkCapabilities(l.CanViewNutritionPlans, l.CanViewTrainingPlans))))
            .ToList();

        if (clientLinks.Count == 0)
        {
            return [];
        }

        var linkIds = clientLinks.SelectMany(g => g.Select(l => l.LinkId)).ToList();

        var assignments = await db.ClientTagAssignments
            .AsNoTracking()
            .Where(a => linkIds.Contains(a.ClientProfessionalLinkId))
            .Select(a => new { a.ClientProfessionalLinkId, a.ClientTagId })
            .ToListAsync(ct);

        var tagsByLink = assignments
            .GroupBy(a => a.ClientProfessionalLinkId)
            .ToDictionary(g => g.Key, g => g.Select(a => a.ClientTagId).ToList());

        return clientLinks
            .Select(group =>
            {
                var primary = group
                    .Where(l => IsCovered(new LinkCapabilities(l.CanViewNutritionPlans, l.CanViewTrainingPlans)))
                    .OrderBy(l => l.DateCreated)
                    .ThenBy(l => l.LinkId)
                    .First();

                return new AutomationCoveredClient
                {
                    ClientProfileId = group.Key,
                    ClientUserId = group.First().ClientUserId,
                    TimeZoneId = group.First().TimeZoneId,
                    LinkedSince = group.Min(l => l.DateCreated),
                    CollaborationStartedAtUtc = group.Min(l => l.ActivatedAtUtc ?? l.DateCreated),
                    PrimaryLinkId = primary.LinkId,
                    PrimaryLinkRole = primary.ProfessionalRole,
                    CanViewNutritionPlans = group.Any(l => l.CanViewNutritionPlans),
                    CanViewTrainingPlans = group.Any(l => l.CanViewTrainingPlans),
                    TagIds = group
                        .SelectMany(l => tagsByLink.TryGetValue(l.LinkId, out var tags) ? tags : [])
                        .ToHashSet(),
                };
            })
            .ToList();
    }

    private static bool IsRoleRemoved(UserRole role, DateTime? trainerRemovedAt, DateTime? nutritionistRemovedAt) => role switch
    {
        UserRole.Trainer => trainerRemovedAt is not null,
        UserRole.Nutritionist => nutritionistRemovedAt is not null,
        _ => false,
    };

    private sealed record LinkRow(
        long LinkId,
        long ClientProfileId,
        Guid ClientUserId,
        string? TimeZoneId,
        DateTime DateCreated,
        DateTime? ActivatedAtUtc,
        UserRole ProfessionalRole,
        bool CanViewNutritionPlans,
        bool CanViewTrainingPlans);
}
