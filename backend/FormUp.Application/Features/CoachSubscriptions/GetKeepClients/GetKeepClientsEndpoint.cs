using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using Microsoft.EntityFrameworkCore;
using MongoDB.Driver;

namespace FormUp.Application.Features.CoachSubscriptions.GetKeepClients;

/// <summary>
/// Lists a lapsed coach's live clients with the ones the Free plan keeps, for the keep-clients screen.
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
                            "keeps them and what happens to them after, plus the cap. Only while the paid plan has lapsed.";
            s.Responses[StatusCodes.Status200OK] = "The live clients and the cap";
            s.Responses[StatusCodes.Status401Unauthorized] = "Missing or unreadable caller claim";
            s.Responses[StatusCodes.Status404NotFound] = "The caller has no professional profile";
            s.Responses[StatusCodes.Status409Conflict] = "The paid plan has not lapsed (SUBSCRIPTION_NOT_LAPSED)";
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

        if (!state.IsLapsed)
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
                ClientPublicId = link.ClientProfile.PublicId,
                ClientUserId = link.ClientProfile.UserId,
                link.ClientProfile.User.FirstName,
                link.ClientProfile.User.LastName,
            })
            .ToListAsync(ct);

        var runsUntilByClientUser = await LoadRunsUntilAsync(userId, links.Select(link => link.ClientUserId).Distinct().ToList(), ct);
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var keptIds = state.KeptClientProfileIds.ToHashSet();

        var clients = links
            .GroupBy(link => link.ClientProfileId)
            .Select(group =>
            {
                var first = group.First();
                var isKept = keptIds.Contains(group.Key);
                var runsUntil = runsUntilByClientUser.GetValueOrDefault(first.ClientUserId);

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
                        : runsUntil is { } lastDay && today <= lastDay
                            ? KeepClientAfterLapse.RunsUntilEndThenReadOnly
                            : KeepClientAfterLapse.ReadOnly,
                };
            })
            .OrderByDescending(client => client.IsKept)
            .ThenBy(client => client.LastName)
            .ThenBy(client => client.FirstName)
            .ToList();

        await Send.OkAsync(new GetKeepClientsResponse { Cap = state.Cap, Clients = clients }, ct);
    }

    /// <summary>
    /// The latest end of published weeks across the caller's own nutrition and training plans, per client
    /// (keyed by <c>ApplicationUser.Id</c>, as Mongo plans store it). Archived plans no longer run.
    /// </summary>
    private async Task<Dictionary<Guid, DateOnly>> LoadRunsUntilAsync(
        Guid coachUserId, IReadOnlyCollection<Guid> clientUserIds, CancellationToken ct)
    {
        var runsUntil = new Dictionary<Guid, DateOnly>();

        var nutritionPlans = await mongo.NutritionPlans
            .Find(Builders<NutritionPlan>.Filter.Eq(p => p.NutritionistId, coachUserId)
                  & Builders<NutritionPlan>.Filter.In(p => p.ClientId, clientUserIds)
                  & Builders<NutritionPlan>.Filter.Ne(p => p.Status, NutritionPlanStatus.Archived))
            .Project(p => new { p.ClientId, p.StartDate, p.Weeks })
            .ToListAsync(ct);

        foreach (var plan in nutritionPlans)
        {
            Record(runsUntil, plan.ClientId, PlanRunsUntilResolver.Resolve(
                plan.StartDate, plan.Weeks.Where(w => w.Status == WeekStatus.Published).Select(w => w.WeekNumber)));
        }

        var trainingPlans = await mongo.TrainingPlans
            .Find(Builders<TrainingPlan>.Filter.Eq(p => p.TrainerId, coachUserId)
                  & Builders<TrainingPlan>.Filter.In(p => p.ClientId, clientUserIds)
                  & Builders<TrainingPlan>.Filter.Ne(p => p.Status, TrainingPlanStatus.Archived))
            .Project(p => new { p.ClientId, p.StartDate, p.Weeks })
            .ToListAsync(ct);

        foreach (var plan in trainingPlans)
        {
            Record(runsUntil, plan.ClientId, PlanRunsUntilResolver.Resolve(
                plan.StartDate, plan.Weeks.Where(w => w.Status == WeekStatus.Published).Select(w => w.WeekNumber)));
        }

        return runsUntil;
    }

    private static void Record(Dictionary<Guid, DateOnly> runsUntil, Guid clientUserId, DateOnly? planRunsUntil)
    {
        if (planRunsUntil is { } lastDay
            && (!runsUntil.TryGetValue(clientUserId, out var current) || lastDay > current))
        {
            runsUntil[clientUserId] = lastDay;
        }
    }
}
