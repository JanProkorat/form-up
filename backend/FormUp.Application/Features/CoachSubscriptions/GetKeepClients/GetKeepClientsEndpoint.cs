using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.CoachSubscriptions.Shared;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.CoachSubscriptions.GetKeepClients;

/// <summary>
/// Lists the live clients of a lapsed coach, or of a Free-plan coach over the cap, with the ones the Free plan keeps, for the keep-clients screen.
/// </summary>
/// <param name="db">Application database context.</param>
/// <param name="mongo">MongoDB context.</param>
/// <param name="entitlementService">Resolves the lapse state and the kept clients.</param>
/// <param name="timeProvider">Clock used to tell whether a client's published weeks are still running.</param>
internal sealed class GetKeepClientsEndpoint(
    IApplicationDbContext db,
    IMongoContext mongo,
    EntitlementService entitlementService,
    TimeProvider timeProvider)
    : EndpointWithoutRequest<GetKeepClientsResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/users/me/subscription/keep-clients");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "List the clients a lapsed plan keeps";
            s.Description = "The caller's live clients with their roles, the end of their published weeks, whether the Free plan " +
                            "keeps them and what happens to them after, plus the cap. Only while the paid plan has lapsed, the coach is on the Free plan " +
                            "with clients over its cap, or a plan choice is required " +
                            "(then the rows show what the Free plan would keep).";
            s.Response<GetKeepClientsResponse>(StatusCodes.Status200OK, "The live clients and the cap");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or unreadable caller claim");
            s.Response(StatusCodes.Status403Forbidden, "The caller is not a trainer or nutritionist, or the coach role was removed");
            s.Response(StatusCodes.Status404NotFound, "The caller has no professional profile");
            s.Response(StatusCodes.Status409Conflict, "The paid plan has not lapsed, the Free plan is within its cap and no plan choice is required (SUBSCRIPTION_NOT_LAPSED)");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(CancellationToken ct)
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

        if (state.IsPlanChoiceRequired)
        {
            state = await entitlementService.GetFreePlanKeepSplitAsync(profileId.Value, ct);
        }
        else if (!state.CanChooseKeptClients)
        {
            await this.SendProblemAsync(
                StatusCodes.Status409Conflict, ErrorCodes.SubscriptionNotLapsed, "The subscription has not lapsed.", ct);
            return;
        }

        var links = await db.ClientProfessionalLinks
            .AsNoTracking()
            .Where(link => link.ProfessionalProfileId == profileId && link.ConnectionState != ConnectionState.Ended)
            .Select(link => new
            {
                link.ClientProfileId,
                link.ProfessionalRole,
                link.ConnectionState,
                ClientPublicId = link.ClientProfile.PublicId,
                ClientUserId = link.ClientProfile.UserId,
                link.ClientProfile.User.FirstName,
                link.ClientProfile.User.LastName,
            })
            .ToListAsync(ct);

        var runsUntilByClientUser = await ClientPlanRunsUntilLoader.LoadAsync(mongo, userId, links.Select(link => link.ClientUserId).Distinct().ToList(), ct);
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var keptIds = state.KeptClientProfileIds.ToHashSet();

        var clients = links
            .GroupBy(link => link.ClientProfileId)
            .Select(group =>
            {
                var first = group.First();
                var isKept = keptIds.Contains(group.Key);
                DateOnly? runsUntil = runsUntilByClientUser.TryGetValue(first.ClientUserId, out var lastDay) ? lastDay : null;

                return new KeepClientDto
                {
                    ClientId = first.ClientPublicId,
                    FirstName = first.FirstName,
                    LastName = first.LastName,
                    Roles = group.Select(link => link.ProfessionalRole).Distinct().OrderBy(role => role).ToList(),
                    IsKept = isKept,
                    PublishedWeeksEnd = runsUntil,
                    AfterLapse = isKept
                        ? KeepClientAfterLapse.FullAccess
                        : runsUntil is { } endDay && ClientPlanRunsUntilLoader.StillRuns(endDay, today)
                            ? KeepClientAfterLapse.RunsUntilEndThenReadOnly
                            : KeepClientAfterLapse.ReadOnly,
                };
            })
            .OrderByDescending(client => client.IsKept)
            .ThenBy(client => client.LastName)
            .ThenBy(client => client.FirstName)
            .ToList();

        var isChoiceLocked = links.Any(link =>
            link.ConnectionState is ConnectionState.FreePlanKept or ConnectionState.FreePlanReleased);

        await Send.OkAsync(
            new GetKeepClientsResponse
            {
                Cap = state.Cap,
                IsChoiceLocked = isChoiceLocked,
                FreeSlotCount = state.FreeSlotCount,
                Clients = clients,
            },
            ct);
    }
}
