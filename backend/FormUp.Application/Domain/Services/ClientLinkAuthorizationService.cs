using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Domain.Services;

/// <summary>
/// Single entry point for "what may this professional see about this client" — consolidates the
/// seven near-synonymous checks every link-authorization call site now resolves through directly
/// (the epic's #958–#964 migration).
/// Cross-database: reads PostgreSQL (professional/client profiles, links) to authorize MongoDB
/// plan access.
/// </summary>
public class ClientLinkAuthorizationService(IApplicationDbContext db) : IClientLinkAuthorizationService
{
    /// <inheritdoc />
    public async Task<LinkCapabilities?> GetCapabilitiesByClientPublicIdAsync(
        Guid professionalUserId, Guid clientPublicId, CancellationToken ct)
    {
        var professionalProfile = await db.ProfessionalProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(pp => pp.UserId == professionalUserId, ct);

        if (professionalProfile is null)
        {
            return null;
        }

        var clientProfile = await db.ClientProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(cp => cp.PublicId == clientPublicId, ct);

        if (clientProfile is null)
        {
            return null;
        }

        return await LoadCapabilitiesAsync(professionalProfile.Id, clientProfile.Id, ct);
    }

    /// <inheritdoc />
    public async Task<LinkCapabilities?> GetCapabilitiesByClientUserIdAsync(
        Guid professionalUserId, Guid clientUserId, CancellationToken ct)
    {
        var professionalProfile = await db.ProfessionalProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(pp => pp.UserId == professionalUserId, ct);

        if (professionalProfile is null)
        {
            return null;
        }

        var clientProfile = await db.ClientProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(cp => cp.UserId == clientUserId, ct);

        if (clientProfile is null)
        {
            return null;
        }

        return await LoadCapabilitiesAsync(professionalProfile.Id, clientProfile.Id, ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<(Guid ClientUserId, LinkCapabilities Capabilities)>> GetAccessibleClientsAsync(
        Guid professionalUserId, CancellationToken ct, LinkCapabilityScope? requiredScope = null)
    {
        var professionalProfile = await db.ProfessionalProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(pp => pp.UserId == professionalUserId, ct);

        if (professionalProfile is null)
        {
            return [];
        }

        var activeLinks = db.ClientProfessionalLinks
            .AsNoTracking()
            .Where(cpl => cpl.ProfessionalProfileId == professionalProfile.Id && cpl.ConnectionState != ConnectionState.Ended);

        // null means "every active link, including one that grants neither domain" — see
        // LinkCapabilities.GrantsNothing — and is deliberately NOT the same as
        // LinkCapabilityScope.Both, which requires both flags. Collapsing null into Both would
        // silently drop every GrantsNothing row from this unfiltered batch query. Pushing a domain
        // requirement down here (rather than filtering the unfiltered result afterward) is what
        // keeps the plan LIST routes' query a single indexed lookup instead of an over-fetch.
        var filteredLinks = requiredScope switch
        {
            LinkCapabilityScope.TrainingOnly => activeLinks.Where(cpl => cpl.CanViewTrainingPlans),
            LinkCapabilityScope.NutritionOnly => activeLinks.Where(cpl => cpl.CanViewNutritionPlans),
            LinkCapabilityScope.Both => activeLinks.Where(cpl => cpl.CanViewTrainingPlans && cpl.CanViewNutritionPlans),
            null => activeLinks
        };

        var rows = await filteredLinks
            .Join(
                db.ClientProfiles,
                cpl => cpl.ClientProfileId,
                cp => cp.Id,
                (cpl, cp) => new { cp.UserId, cpl.CanViewNutritionPlans, cpl.CanViewTrainingPlans })
            .ToListAsync(ct);

        return rows
            .Select(row => (row.UserId, new LinkCapabilities(row.CanViewNutritionPlans, row.CanViewTrainingPlans)))
            .ToList();
    }

    /// <summary>
    /// Loads the capability flags for the active link between the given profile pair, keeping
    /// "no active link" (<see langword="null"/>) distinct from "an active link that grants
    /// neither domain" (a non-null <see cref="LinkCapabilities"/> whose
    /// <see cref="LinkCapabilities.GrantsNothing"/> is <see langword="true"/>).
    /// </summary>
    /// <remarks>
    /// Projected to an anonymous (reference) type deliberately: <see cref="LinkCapabilities"/> is a
    /// struct, so projecting straight to it would make a link carrying neither flag
    /// indistinguishable from no link at all — both would come back as <see langword="default"/>.
    /// </remarks>
    private async Task<LinkCapabilities?> LoadCapabilitiesAsync(
        long professionalProfileId, long clientProfileId, CancellationToken ct)
    {
        var flags = await db.ClientProfessionalLinks
            .AsNoTracking()
            .Where(cpl =>
                cpl.ProfessionalProfileId == professionalProfileId &&
                cpl.ClientProfileId == clientProfileId &&
                cpl.ConnectionState != ConnectionState.Ended)
            .Select(cpl => new { cpl.CanViewNutritionPlans, cpl.CanViewTrainingPlans })
            .FirstOrDefaultAsync(ct);

        return flags is null
            ? null
            : new LinkCapabilities(flags.CanViewNutritionPlans, flags.CanViewTrainingPlans);
    }
}
