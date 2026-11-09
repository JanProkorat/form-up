using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.CoachSubscriptions.SetKeepClients;

/// <summary>
/// Saves which clients a lapsed coach keeps on the Free plan; the other live clients are released.
/// </summary>
/// <param name="db">Application database context.</param>
/// <param name="entitlementService">Resolves the lapse state and the Free plan's cap.</param>
[RemovedCoachRole(RemovedCoachRoleMode.Refuse)]
internal sealed class SetKeepClientsEndpoint(IApplicationDbContext db, EntitlementService entitlementService)
    : Endpoint<SetKeepClientsRequest>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Put("/users/me/subscription/keep-clients");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Choose the clients a lapsed plan keeps";
            s.Description = "Marks exactly min(cap, live clients) of the caller's live clients as kept and releases the rest. " +
                            "Can be changed any time while the plan stays lapsed.";
            s.Responses[StatusCodes.Status204NoContent] = "The choice was saved";
            s.Responses[StatusCodes.Status400BadRequest] =
                "Invalid body, a duplicate id, or the wrong number of clients (KEEP_CLIENTS_COUNT_INVALID)";
            s.Responses[StatusCodes.Status401Unauthorized] = "Missing or unreadable caller claim";
            s.Responses[StatusCodes.Status404NotFound] =
                "The caller has no professional profile, or an id is not one of the caller's live clients";
            s.Responses[StatusCodes.Status409Conflict] = "The paid plan has not lapsed (SUBSCRIPTION_NOT_LAPSED)";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(SetKeepClientsRequest req, CancellationToken ct)
    {
        if (!Guid.TryParse(User.FindFirstValue(AppClaims.UserId), out var userId))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var profileId = await db.ProfessionalProfiles
            .AsNoTracking()
            .Where(p => p.UserId == userId)
            .Select(p => (long?)p.Id)
            .FirstOrDefaultAsync(ct);

        if (profileId is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var state = await entitlementService.GetKeepClientsStateAsync(profileId.Value, ct);

        if (!state.IsLapsed)
        {
            await this.SendProblemAsync(
                StatusCodes.Status409Conflict, ErrorCodes.SubscriptionNotLapsed, "The subscription has not lapsed.", ct);
            return;
        }

        var links = await db.ClientProfessionalLinks
            .Include(link => link.ClientProfile)
            .Where(link => link.ProfessionalProfileId == profileId && link.ConnectionState != ConnectionState.Ended)
            .ToListAsync(ct);

        var liveClientCount = links.Select(link => link.ClientProfileId).Distinct().Count();
        var expectedCount = state.Cap is { } cap ? Math.Min(cap, liveClientCount) : liveClientCount;

        if (req.ClientIds.Distinct().Count() != req.ClientIds.Count || req.ClientIds.Count != expectedCount)
        {
            this.ThrowErrorWithCode(
                ErrorCodes.KeepClientsCountInvalid,
                $"Choose exactly {expectedCount} distinct clients to keep.");
            return;
        }

        var keepIds = req.ClientIds.ToHashSet();

        if (!keepIds.IsSubsetOf(links.Select(link => link.ClientProfile.PublicId)))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        foreach (var link in links)
        {
            link.ConnectionState = keepIds.Contains(link.ClientProfile.PublicId)
                ? ConnectionState.FreePlanKept
                : ConnectionState.FreePlanReleased;
        }

        await db.SaveChangesAsync(ct);
        await Send.NoContentAsync(ct);
    }
}
