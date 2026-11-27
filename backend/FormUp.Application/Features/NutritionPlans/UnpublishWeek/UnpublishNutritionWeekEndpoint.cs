using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.NutritionPlans.GetPlan;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Application.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using MongoDB.Bson;
using MongoDB.Driver;

namespace FormUp.Application.Features.NutritionPlans.UnpublishWeek;

/// <summary>
/// Unpublishes a not-yet-started week of a nutrition plan, hiding it from the client again.
/// Refused once the week's Monday has arrived or the client has logged any meal in it.
/// Plans archived by the first publish are not restored. Unpublishing the last published
/// week returns the plan to Draft.
/// </summary>
[RemovedCoachRole(AppRoles.Nutritionist, RemovedCoachRoleMode.WhilePlanInProgress)]
public class UnpublishNutritionWeekEndpoint(
    IMongoContext mongo,
    IApplicationDbContext db,
    INotificationService notificationService,
    IRealtimeNotifier notifier,
    PlanConcurrencyGuard guard,
    IClientLinkAuthorizationService linkAuthorizationService) : Endpoint<UnpublishNutritionWeekRequest, GetPlanResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/nutrition/plans/{PlanId}/weeks/{WeekNumber}/unpublish");
        Roles(AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Unpublish a week of a nutrition plan";
            s.Description = "Sets the week back to Draft and notifies the client. Allowed only before the week's Monday and while the client has no meal logs in the week.";
            s.Response<GetPlanResponse>(StatusCodes.Status200OK, "The plan with the week unpublished");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest,
                "The week is missing or not published, or the plan is not Active");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or unreadable caller claim");
            s.Response(StatusCodes.Status403Forbidden, "The caller's coach role was removed (COACH_ROLE_REMOVED)");
            s.Response(StatusCodes.Status404NotFound, "Plan not found, or not editable by the caller");
            s.Response(StatusCodes.Status409Conflict,
                "The week has already started (WEEK_ALREADY_STARTED), the client logged meals in it (WEEK_HAS_MEAL_LOGS), or the plan was changed concurrently (PLAN_VERSION_CONFLICT)");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(UnpublishNutritionWeekRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);
        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var nutritionistId = Guid.Parse(userId);

        var lookupFilter = Builders<NutritionPlan>.Filter.Eq(p => p.ExternalId, req.PlanId)
                           & Builders<NutritionPlan>.Filter.Eq(p => p.NutritionistId, nutritionistId);

        // The guard takes the write filter and update by value, so whether this is the last published
        // week is read up front; the write filter below re-checks it atomically. Authorization and
        // validation still run inside the guard on its own fresh read.
        var preReadPlan = await mongo.NutritionPlans.Find(lookupFilter).FirstOrDefaultAsync(ct);
        if (preReadPlan is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var isLastPublishedWeek = !preReadPlan.Weeks.Any(
            w => w.WeekNumber != req.WeekNumber && w.Status == WeekStatus.Published);

        async Task<bool> Authorize(NutritionPlan plan, CancellationToken authorizeCt)
        {
            // plan.ClientId is ApplicationUser.Id (#840).
            var capabilities = await linkAuthorizationService.GetCapabilitiesByClientUserIdAsync(
                nutritionistId, plan.ClientId, authorizeCt);

            if (capabilities is not { CanViewNutritionPlans: true })
            {
                await Send.NotFoundAsync(authorizeCt);
                return false;
            }

            return true;
        }

        async Task<bool> Validate(NutritionPlan plan, CancellationToken validateCt)
        {
            var week = plan.Weeks.FirstOrDefault(w => w.WeekNumber == req.WeekNumber);
            if (week is null)
            {
                ThrowError($"Week {req.WeekNumber} not found in plan.");
                return false;
            }

            if (week.Status != WeekStatus.Published)
            {
                ThrowError($"Week {req.WeekNumber} is not published.");
                return false;
            }

            if (plan.Status != NutritionPlanStatus.Active)
            {
                this.ThrowErrorWithCode(ErrorCodes.PlanNotActive, "Only weeks of an active plan can be unpublished.");
                return false;
            }

            // A published week always has a start date (publishing requires it).
            if (!plan.StartDate.HasValue)
            {
                return true;
            }

            var weekStart = plan.StartDate.Value.Date.AddDays((req.WeekNumber - 1) * 7);
            if (DateOnly.FromDateTime(weekStart) <= DateOnly.FromDateTime(DateTime.UtcNow))
            {
                await this.SendProblemAsync(409, ErrorCodes.WeekAlreadyStarted,
                    $"Week {req.WeekNumber} has already started and can no longer be unpublished.", validateCt);
                return false;
            }

            var weekStartUtc = DateTime.SpecifyKind(weekStart, DateTimeKind.Utc);
            var mealLogFilter = MealLogDayFilter.BuildRange(
                plan.ClientId, plan.ExternalId, weekStartUtc, weekStartUtc.AddDays(6), weekStartUtc, weekStartUtc.AddDays(7));

            // The check-then-write race (a meal logged between this count and the write) is accepted.
            if (await mongo.MealLogs.CountDocumentsAsync(mealLogFilter, new CountOptions { Limit = 1 }, validateCt) > 0)
            {
                await this.SendProblemAsync(409, ErrorCodes.WeekHasMealLogs,
                    $"The client has logged meals in week {req.WeekNumber}, so it can no longer be unpublished.", validateCt);
                return false;
            }

            return true;
        }

        var now = DateTime.UtcNow;

        // Gate on Active plan and the target week still Published, so a concurrent change matches zero documents.
        var writeFilter = Builders<NutritionPlan>.Filter.Eq(p => p.ExternalId, req.PlanId)
            & Builders<NutritionPlan>.Filter.Eq(p => p.NutritionistId, nutritionistId)
            & Builders<NutritionPlan>.Filter.Eq(p => p.Status, NutritionPlanStatus.Active)
            & Builders<NutritionPlan>.Filter.ElemMatch(p => p.Weeks,
                w => w.WeekNumber == req.WeekNumber && w.Status == WeekStatus.Published);

        var update = Builders<NutritionPlan>.Update
            .Set("weeks.$[w].status", WeekStatus.Draft.ToString())
            .Unset("weeks.$[w].datePublished")
            .Set(p => p.DateUpdated, now)
            .Inc(p => p.Version, 1);

        // Last published week: the plan goes back to Draft, and the write only matches while no other
        // week got published meanwhile (a concurrent publish then yields PLAN_VERSION_CONFLICT).
        if (isLastPublishedWeek)
        {
            writeFilter &= Builders<NutritionPlan>.Filter.Not(
                Builders<NutritionPlan>.Filter.ElemMatch(p => p.Weeks,
                    w => w.WeekNumber != req.WeekNumber && w.Status == WeekStatus.Published));
            update = update.Set(p => p.Status, NutritionPlanStatus.Draft);
        }

        var arrayFilters = new List<ArrayFilterDefinition>
        {
            new BsonDocumentArrayFilterDefinition<BsonDocument>(new BsonDocument
            {
                { "w.weekNumber", req.WeekNumber },
                { "w.status", WeekStatus.Published.ToString() }
            })
        };

        var guardResult = await guard.UpdateWithArrayFilterGuardAsync(
            mongo.NutritionPlans,
            lookupFilter,
            Authorize,
            Validate,
            writeFilter,
            update,
            arrayFilters,
            ct);

        switch (guardResult.Outcome)
        {
            case PlanConcurrencyOutcome.NotFound:
                await Send.NotFoundAsync(ct);
                return;
            case PlanConcurrencyOutcome.ReplaceConflict:
                await this.SendProblemAsync(409, ErrorCodes.PlanVersionConflict,
                    "Version conflict. The week was modified concurrently.", ct);
                return;
            case PlanConcurrencyOutcome.HandledByMutator:
                return;
        }

        var updatedPlan = guardResult.Document!;

        var clientProfile = await db.ClientProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(cp => cp.UserId == updatedPlan.ClientId, ct);

        if (clientProfile is not null)
        {
            await notificationService.CreateAsync(
                clientProfile.UserId,
                NotificationType.PlanPublished,
                new Dictionary<string, string>
                {
                    ["weekNumber"] = req.WeekNumber.ToString(),
                    [NotificationTemplates.PlanKindKey] = NotificationTemplates.PlanKindNutrition,
                    [NotificationTemplates.PlanIdKey] = updatedPlan.ExternalId.ToString(),
                },
                variant: NotificationTemplates.PlanPublishedNutritionUnpublished,
                ct: ct);

            await notifier.NotifyAsync(clientProfile.UserId, "nutritionplanupdated", new
            {
                PlanId = updatedPlan.ExternalId,
                req.WeekNumber,
            }, ct);

            await notifier.NotifyAsync(clientProfile.UserId, "newnotification", new { }, ct);
        }

        var clientPublicId = clientProfile?.PublicId ?? updatedPlan.ClientId;
        await Send.OkAsync(GetPlanResponse.FromDocument(updatedPlan, clientPublicId), ct);
    }
}
