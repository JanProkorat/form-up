using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.ClientNutrition.GetFullPlan;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using MongoDB.Driver;

namespace FormUp.Application.Features.ClientNutrition.GetClientNutritionPlan;

/// <summary>
/// Returns one of the caller's own Active or Completed nutrition plans by id, in the same shape as the full plan.
/// </summary>
/// <param name="mongo">MongoDB context.</param>
/// <param name="db">Relational database context.</param>
/// <param name="timeProvider">Clock abstraction.</param>
internal sealed class GetClientNutritionPlanEndpoint(IMongoContext mongo, IApplicationDbContext db, TimeProvider timeProvider)
    : Endpoint<GetClientNutritionPlanRequest, GetFullPlanResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/client/nutrition/plans/{PlanId}");
        Roles(AppRoles.Client);
        Summary(s =>
        {
            s.Summary = "Get one of the caller's nutrition plans";
            s.Description = "Returns the published weeks of one of the caller's own Active or Completed nutrition plans. "
                + "The current week and day are null when the plan is upcoming, completed, or past its window.";
            s.Response<GetFullPlanResponse>(StatusCodes.Status200OK, "Nutrition plan with its published weeks");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest, "Invalid plan id");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or unreadable caller claim");
            s.Response(StatusCodes.Status404NotFound,
                "Plan not found, not owned by the caller, not Active or Completed, or without a published week");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(GetClientNutritionPlanRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);

        if (userId is null || !Guid.TryParse(userId, out var clientId))
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        // Canonical client id on Mongo docs is ApplicationUser.Id, stored as a binary UUID.
        var plan = await mongo.NutritionPlans
            .Find(Builders<NutritionPlan>.Filter.And(
                Builders<NutritionPlan>.Filter.Eq(p => p.ClientId, clientId),
                Builders<NutritionPlan>.Filter.Eq(p => p.ExternalId, req.PlanId),
                Builders<NutritionPlan>.Filter.In(p => p.Status, [NutritionPlanStatus.Active, NutritionPlanStatus.Completed])))
            .FirstOrDefaultAsync(ct);

        if (plan is null || !plan.Weeks.Any(w => w.Status == WeekStatus.Published))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var todayLocalUtc = await db.ResolveClientLocalDateUtcAsync(clientId, timeProvider.GetUtcNow().UtcDateTime, ct);

        var mealRows = await mongo.MealLogs
            .Find(Builders<MealLog>.Filter.And(
                Builders<MealLog>.Filter.Eq(m => m.ClientId, clientId),
                Builders<MealLog>.Filter.Eq(m => m.PlanId, plan.ExternalId)))
            .ToListAsync(ct);

        var response = GetFullPlanResponse.FromPlan(plan, todayLocalUtc, MealLogMerger.MergeForPlan(mealRows, plan));

        // The shared builder falls back to the last published week once the window is over; a finished
        // plan has no current position.
        if (plan.Status == NutritionPlanStatus.Completed
            || !PlanWindowResolver.HasNotEnded(plan.StartDate, plan.Weeks.Count, DateOnly.FromDateTime(todayLocalUtc)))
        {
            response.CurrentWeek = null;
            response.CurrentDayOfWeek = null;
        }

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
