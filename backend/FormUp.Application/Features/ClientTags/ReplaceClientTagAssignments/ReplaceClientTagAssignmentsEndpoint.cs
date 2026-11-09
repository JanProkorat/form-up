using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Enums;
using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Features.ClientTags.Shared;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FormUp.Application.Features.ClientTags.ReplaceClientTagAssignments;

/// <summary>
/// Replaces the full set of tags assigned to a client, scoped to the caller's own link. Tagging is
/// coach-private relationship metadata — it requires a live link but not either
/// <c>CanView*</c> capability flag.
/// </summary>
/// <param name="db">Application database context.</param>
[RemovedCoachRole(RemovedCoachRoleMode.Refuse)]
public class ReplaceClientTagAssignmentsEndpoint(IApplicationDbContext db)
    : Endpoint<ReplaceClientTagAssignmentsRequest, ReplaceClientTagAssignmentsResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Put("/trainer/clients/{ClientId}/tags");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Replace a client's tag assignments";
            s.Description = "Replaces the full set of tags assigned to the client for the caller's own link.";
            s.Response<ReplaceClientTagAssignmentsResponse>(StatusCodes.Status200OK, "The resulting tag set");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest, "Invalid request body");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or invalid credentials");
            s.Response(StatusCodes.Status404NotFound,
                "Client not found, caller has no active link to the client, or one or more tags were not found");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(ReplaceClientTagAssignmentsRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);

        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var context = await LoadReplaceContextOrRespondAsync(Guid.Parse(userId), req, ct);

        if (context is null)
        {
            return;
        }

        var (_, clientProfile, link, ownedTags) = context.Value;

        var desiredTagIds = ownedTags.Select(t => t.Id).ToHashSet();

        // Removals are a set-based bulk delete, not a tracked Remove()+SaveChanges. That matters
        // for concurrency: a tracked Remove() expects the DELETE to affect exactly one row and
        // throws DbUpdateConcurrencyException if a concurrent replace already removed the same
        // row (0 rows affected). ExecuteDeleteAsync has no such expectation — a concurrent
        // duplicate delete just removes 0 more rows and never throws. This also means the
        // removal step needs no retry: it is already idempotent.
        await db.ClientTagAssignments
            .Where(a => a.ClientProfessionalLinkId == link.Id && !desiredTagIds.Contains(a.ClientTagId))
            .ExecuteDeleteAsync(ct);

        // Insertion is the only step that can race with a concurrent replace targeting an
        // overlapping tag set (both insert the same (ClientTagId, ClientProfessionalLinkId) row).
        // Each attempt re-reads the currently-persisted tag ids for this link — never a
        // pre-collision snapshot — so a retry only adds rows nobody has persisted yet, and a
        // caller can never receive success for a set that the database does not actually hold:
        // RemoveRange+AddRange staged in one SaveChanges could report a set that a rolled-back
        // transaction never committed; re-reading before every attempt (and once more below to
        // build the response) rules that out.
        //
        // Accepted 500 surface: the catch below retries only a unique-index violation on
        // (ClientTagId, ClientProfessionalLinkId) (SqlState 23505), and only while
        // attempt < maxAttempts. Two distinct races still escape as an uncaught 500 after the
        // set-based removal above has already committed: (1) retry exhaustion — a concurrent
        // replace keeps winning the same insert race across all three attempts; and (2) a
        // DbUpdateException that is not a unique violation at all, e.g. the owning coach
        // concurrently deleting one of the requested tags between the ownership read and this
        // insert, which raises a foreign-key violation (SqlState 23503) instead. Path (2) needs
        // only a single concurrent request to reach, versus three for path (1). Neither is folded
        // into the catch filter — a bare DbUpdateException catch would also swallow genuine
        // faults — so both remain a 500, and both require a second concurrent request from the
        // same coach to reach at all.
        const int maxAttempts = 3;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var currentTagIds = await db.ClientTagAssignments
                .AsNoTracking()
                .Where(a => a.ClientProfessionalLinkId == link.Id)
                .Select(a => a.ClientTagId)
                .ToListAsync(ct);

            var toAdd = desiredTagIds
                .Except(currentTagIds)
                .Select(tagId => new ClientTagAssignment { ClientTagId = tagId, ClientProfessionalLinkId = link.Id })
                .ToList();

            if (toAdd.Count == 0)
            {
                break;
            }

            db.ClientTagAssignments.AddRange(toAdd);

            try
            {
                await db.SaveChangesAsync(ct);
                break;
            }
            catch (DbUpdateException ex) when (attempt < maxAttempts && IsUniqueViolation(ex))
            {
                // A concurrent replace inserted one of the same rows first. This attempt's
                // failed inserts are still tracked as Added — left alone, they would poison
                // every subsequent SaveChangesAsync with the same doomed insert. Remove() on an
                // entity that was never actually saved (still Added, not yet in the database)
                // detaches it rather than issuing a DELETE, which is exactly the cleanup needed
                // before looping back to re-read + retry with the now-current state.
                db.ClientTagAssignments.RemoveRange(toAdd);
            }
        }

        // Re-query rather than trust the staged `ownedTags`/`toAdd` sets — the response must
        // reflect what is actually persisted, never intent that a retry exhausted or a
        // still-in-flight concurrent write superseded. Joining to ClientTags here (instead of
        // filtering `ownedTags`, which only holds the tags THIS request resolved from its own
        // TagIds) matters under a divergent concurrent replace on the same link: a tag a
        // different concurrent request just persisted would not be in `ownedTags` at all, so
        // filtering that set would under-report a tag the database actually holds.
        var assignedTags = await db.ClientTagAssignments
            .AsNoTracking()
            .Where(a => a.ClientProfessionalLinkId == link.Id)
            .Select(a => a.ClientTag)
            .ToListAsync(ct);

        await Send.OkAsync(new ReplaceClientTagAssignmentsResponse
        {
            ClientId = clientProfile.PublicId,
            Tags = assignedTags
                .OrderBy(t => t.Name)
                .Select(ClientTagDto.FromEntity)
                .ToList(),
        }, ct);
    }

    /// <summary>
    /// Runs the endpoint's five sequential guards — caller has a professional profile, the client
    /// exists, the caller holds a live link to it, and every requested tag id resolves to a tag the
    /// caller owns — and resolves the values the rest of <see cref="HandleAsync"/> needs. Returns
    /// <see langword="null"/> when a guard has already written a response; the caller must just
    /// return.
    /// </summary>
    private async Task<ReplaceContext?> LoadReplaceContextOrRespondAsync(
        Guid userId, ReplaceClientTagAssignmentsRequest req, CancellationToken ct)
    {
        var professionalProfile = await db.ProfessionalProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);

        if (professionalProfile is null)
        {
            await Send.NotFoundAsync(ct);
            return null;
        }

        // Resolve client by PublicId. Existence is not distinguished from "no active link" below —
        // both collapse to a bare 404 so client existence does not leak to a coach with no
        // relationship to them.
        var clientProfile = await db.ClientProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(cp => cp.PublicId == req.ClientId, ct);

        if (clientProfile is null)
        {
            await Send.NotFoundAsync(ct);
            return null;
        }

        // Tagging requires a live link but not either CanView* capability flag — a tag is
        // coach-private relationship metadata, not plan data. Resolved by a direct query rather
        // than IClientLinkAuthorizationService because the assignment needs the link's own row id,
        // which the capability-only service does not expose.
        var link = await db.ClientProfessionalLinks
            .AsNoTracking()
            .FirstOrDefaultAsync(l =>
                l.ProfessionalProfileId == professionalProfile.Id &&
                l.ClientProfileId == clientProfile.Id &&
                l.ConnectionState != ConnectionState.Ended, ct);

        if (link is null)
        {
            await Send.NotFoundAsync(ct);
            return null;
        }

        var distinctTagIds = req.TagIds.Distinct().ToList();

        var ownedTags = await db.ClientTags
            .AsNoTracking()
            .Where(t => t.OwnerProfessionalProfileId == professionalProfile.Id && distinctTagIds.Contains(t.PublicId))
            .ToListAsync(ct);

        if (ownedTags.Count != distinctTagIds.Count)
        {
            await this.SendProblemAsync(
                StatusCodes.Status404NotFound,
                ErrorCodes.ClientTagNotFound,
                "One or more tags were not found.",
                ct);
            return null;
        }

        return new ReplaceContext(professionalProfile, clientProfile, link, ownedTags);
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException pgEx && pgEx.SqlState == "23505";

    /// <summary>
    /// Values resolved by the endpoint's guard sequence that the rest of <see cref="HandleAsync"/>
    /// needs. <see cref="ProfessionalProfile"/> is carried for completeness even though only the
    /// guards themselves currently read it.
    /// </summary>
    private readonly record struct ReplaceContext(
        ProfessionalProfile ProfessionalProfile,
        ClientProfile ClientProfile,
        ClientProfessionalLink Link,
        List<ClientTag> OwnedTags);
}
