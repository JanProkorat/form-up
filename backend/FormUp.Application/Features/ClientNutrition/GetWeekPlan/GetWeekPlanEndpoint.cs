using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using Microsoft.EntityFrameworkCore;
using MongoDB.Driver;

namespace FormUp.Application.Features.ClientNutrition.GetWeekPlan;

/// <summary>
/// Endpoint that returns the client's active nutrition plan for the current week.
/// Cycles through plan weeks when the plan duration is exceeded.
/// </summary>
/// <param name="mongo">MongoDB context.</param>
/// <param name="db">Relational database context.</param>
/// <param name="timeProvider">Clock abstraction (#955) — lets tests pin the "now" instant deterministically.</param>
public class GetWeekPlanEndpoint(IMongoContext mongo, IApplicationDbContext db, TimeProvider timeProvider) : EndpointWithoutRequest<GetWeekPlanResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/client/nutrition/plan/week");
        Roles(AppRoles.Client);
        Summary(s =>
        {
            s.Summary = "Get current week's nutrition plan";
            s.Description = "Returns all days and meals for the current week from the client's active plan.";
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
        // the week calculation below on the client's local "today" rather than the server's
        // UTC day.
        var todayLocalUtc = await db.ResolveClientLocalDateUtcAsync(clientId, timeProvider.GetUtcNow().UtcDateTime, ct);

        // Find the Active plan whose date window contains today — a client may hold several
        // sequential, non-overlapping Active plans (#780).
        var filter = Builders<Domain.Documents.NutritionPlan>.Filter.And(
            Builders<Domain.Documents.NutritionPlan>.Filter.Eq(p => p.ClientId, clientId),
            Builders<Domain.Documents.NutritionPlan>.Filter.Eq(p => p.Status, NutritionPlanStatus.Active));

        var cursor = await mongo.NutritionPlans.FindAsync(filter, cancellationToken: ct);
        var activePlans = await cursor.ToListAsync(ct);
        var plan = PlanWindowResolver.ResolveCurrentPlan(activePlans, p => p.StartDate, p => p.Weeks.Count, todayLocalUtc);

        if (plan is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var publishedWeeks = plan.Weeks.Where(w => w.Status == WeekStatus.Published).ToList();

        if (publishedWeeks.Count == 0)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // publishedWeeks is non-empty here, which implies StartDate is set: PublishWeekEndpoint
        // refuses to publish a week without one (START_DATE_REQUIRED) and UpdatePlanEndpoint
        // refuses to clear one while any week is published (START_DATE_LOCKED), and publishing is
        // the only route to WeekStatus.Published. The legacy plan-level DatePublished cycling
        // branch this replaced was therefore unreachable (#1015).
        if (!plan.StartDate.HasValue)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var daysSinceStart = (int)(todayLocalUtc - plan.StartDate.Value.Date).TotalDays;

        if (daysSinceStart < 0)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var weekNum = daysSinceStart / 7 + 1;

        var week = publishedWeeks.FirstOrDefault(w => w.WeekNumber == weekNum)
                   ?? publishedWeeks[^1];

        await Send.OkAsync(new GetWeekPlanResponse
        {
            PlanId = plan.ExternalId,
            PlanVersion = plan.Version,
            PlanName = plan.Name,
            WeekNumber = week.WeekNumber,
            Days = week.Days,
            GlobalSettings = plan.GlobalSettings,
            RunsUntil = await this.ResolveRunsUntilForOverCapClientAsync(
                db, plan.NutritionistId, clientId, plan.StartDate, publishedWeeks.Select(w => w.WeekNumber), ct),
        }, ct);
    }
}
