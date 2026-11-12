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
                            "The first choice after a plan change is free. After that the kept clients are locked: the request must " +
                            "contain every currently kept client and may only add restricted clients to empty slots; a new plan change unlocks it.";
            s.Response(StatusCodes.Status204NoContent, "The choice was saved");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest,
                "Invalid body, a duplicate id, or the wrong number of clients (KEEP_CLIENTS_COUNT_INVALID)");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or unreadable caller claim");
            s.Response(StatusCodes.Status403Forbidden, "The caller is not a trainer or nutritionist, or the coach role was removed");
            s.Response(StatusCodes.Status404NotFound,
                "The caller has no professional profile, or an id is not one of the caller's live clients");
            s.Response<ProblemDetails>(StatusCodes.Status409Conflict,
                "The paid plan has not lapsed and the Free plan is within its cap (SUBSCRIPTION_NOT_LAPSED), " +
                "or the choice is locked and the request drops a kept client or adds an unrestricted one (KEEP_CLIENTS_CHOICE_LOCKED)");
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

        if (!state.CanChooseKeptClients)
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

        // Once a choice is saved the kept clients stay kept: the request may only add restricted clients.
        var isChoiceLocked = links.Any(link =>
            link.ConnectionState is ConnectionState.FreePlanKept or ConnectionState.FreePlanReleased);

        if (isChoiceLocked)
        {
            var requestedProfileIds = links
                .Where(link => keepIds.Contains(link.ClientProfile.PublicId))
                .Select(link => link.ClientProfileId)
                .ToHashSet();
            var currentlyKept = state.KeptClientProfileIds.ToHashSet();
            var overCap = state.OverCapClientProfileIds.ToHashSet();

            if (!currentlyKept.IsSubsetOf(requestedProfileIds)
                || !requestedProfileIds.Except(currentlyKept).All(overCap.Contains))
            {
                await this.SendProblemAsync(
                    StatusCodes.Status409Conflict,
                    ErrorCodes.KeepClientsChoiceLocked,
                    "The choice is already made: kept clients cannot be swapped, only free slots can be filled.",
                    ct);
                return;
            }
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
