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

namespace FormUp.Application.Features.ClientNutrition.GetFullPlan;

/// <summary>
/// Endpoint that returns all published weeks of the client's active nutrition plan,
/// with pre-computed date ranges and current position within the plan.
/// </summary>
/// <param name="mongo">MongoDB context.</param>
/// <param name="db">Relational database context.</param>
/// <param name="timeProvider">Clock abstraction (#955) — lets tests pin the "now" instant deterministically.</param>
public class GetFullPlanEndpoint(IMongoContext mongo, IApplicationDbContext db, TimeProvider timeProvider) : EndpointWithoutRequest<GetFullPlanResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/client/nutrition/plan/full");
        Roles(AppRoles.Client);
        Summary(s =>
        {
            s.Summary = "Get full nutrition plan";
            s.Description = "Returns all published weeks of the client's active nutrition plan for mobile browsing.";
            s.Responses[StatusCodes.Status200OK] = "Nutrition plan with its published weeks and eaten state";
            s.Responses[StatusCodes.Status401Unauthorized] = "Missing or unreadable caller claim";
            s.Responses[StatusCodes.Status404NotFound] = "Client profile or active plan with a published week not found";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);

        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var clientProfile = await db.ClientProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(cp => cp.UserId == Guid.Parse(userId), ct);

        if (clientProfile is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // Canonical client id on Mongo docs is ApplicationUser.Id (#840).
        var clientId = clientProfile.UserId;

        // Resolve the client's local calendar day (#935) — anchors plan-window resolution and
        // the current-week/day-of-week calculation below on the client's local "today".
        var todayLocalUtc = await db.ResolveClientLocalDateUtcAsync(clientId, timeProvider.GetUtcNow().UtcDateTime, ct);

        // Find the Active plan whose date window contains today — a client may hold several
        // sequential, non-overlapping Active plans (#780).
        var filter = Builders<NutritionPlan>.Filter.And(
            Builders<NutritionPlan>.Filter.Eq(p => p.ClientId, clientId),
            Builders<NutritionPlan>.Filter.Eq(p => p.Status, NutritionPlanStatus.Active));

        var cursor = await mongo.NutritionPlans.FindAsync(filter, cancellationToken: ct);
        var activePlans = await cursor.ToListAsync(ct);
        // Falls back to the earliest upcoming plan with a published week, so the client can browse it
        // before it starts; CurrentWeek stays null for it.
        var plan = PlanWindowResolver.ResolveCurrentOrUpcomingPlan(
            activePlans,
            p => p.StartDate,
            p => p.Weeks.Count,
            p => p.Weeks.Any(w => w.Status == WeekStatus.Published),
            p => p.DateCreated,
            todayLocalUtc);

        if (plan is null || !plan.Weeks.Any(w => w.Status == WeekStatus.Published))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // Every row of the plan: a partly eaten row has no EatenAt, so the state is derived from the items.
        var mealRows = await mongo.MealLogs
            .Find(Builders<MealLog>.Filter.And(
                Builders<MealLog>.Filter.Eq(m => m.ClientId, clientId),
                Builders<MealLog>.Filter.Eq(m => m.PlanId, plan.ExternalId)))
            .ToListAsync(ct);

        var response = GetFullPlanResponse.FromPlan(plan, todayLocalUtc, MealLogMerger.MergeForPlan(mealRows, plan));
        response.RunsUntil = await this.ResolveRunsUntilForOverCapClientAsync(
            db,
            plan.NutritionistId,
            clientId,
            plan.StartDate,
            plan.Weeks.Where(w => w.Status == WeekStatus.Published).Select(w => w.WeekNumber),
            ct);

        await Send.OkAsync(response, ct);
    }
}
