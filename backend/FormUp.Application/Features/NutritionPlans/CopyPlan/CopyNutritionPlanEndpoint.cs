using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using Microsoft.EntityFrameworkCore;
using MongoDB.Driver;

namespace FormUp.Application.Features.NutritionPlans.CopyPlan;

/// <summary>
/// Copies a nutrition plan the caller authored to the same or another linked client as a new
/// Draft plan. The source is gated like every other plan route (author plus a live nutrition
/// capability); the target is gated like plan creation. Every denial is a bodiless 404 so a
/// client's existence never leaks to an unlinked coach.
/// </summary>
/// <param name="mongo">MongoDB context.</param>
/// <param name="linkAuthorizationService">Resolves the nutritionist-client link's CanViewNutritionPlans permission.</param>
/// <param name="db">PostgreSQL context for the target client lookup.</param>
/// <param name="timeProvider">Injected time source for audit timestamps.</param>
[RemovedCoachRole(AppRoles.Nutritionist, RemovedCoachRoleMode.Refuse)]
public class CopyNutritionPlanEndpoint(
    IMongoContext mongo,
    IClientLinkAuthorizationService linkAuthorizationService,
    IApplicationDbContext db,
    TimeProvider timeProvider)
    : Endpoint<CopyNutritionPlanRequest, CopyNutritionPlanResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/nutrition/plans/{PlanId}/copy");
        Roles(AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Copy a nutrition plan to a client";
            s.Description = "Creates a new Draft plan for the same or another linked client from a plan the caller authored. Every week starts as Draft.";
            s.Response<CopyNutritionPlanResponse>(StatusCodes.Status201Created, "The new Draft plan");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest, "Invalid request");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or unreadable caller claim");
            s.Response(StatusCodes.Status403Forbidden,
                "The subscription does not include plans (SUBSCRIPTION_REQUIRED), or the target client is over the Free plan's client limit (CLIENT_OVER_PLAN_LIMIT)");
            s.Response(StatusCodes.Status404NotFound, "Source plan or target client not found, or not accessible to the caller");
            s.Response(StatusCodes.Status409Conflict, "The start date window overlaps another plan of the target client (PLAN_OVERLAP)");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(CopyNutritionPlanRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);

        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var nutritionistId = Guid.Parse(userId);

        var source = await this.LoadOwnedNutritionPlanIfAllowedAsync(
            mongo, linkAuthorizationService, req.PlanId, nutritionistId, ct);

        if (source is null)
        {
            return;
        }

        var capabilities = await linkAuthorizationService.GetCapabilitiesByClientPublicIdAsync(
            nutritionistId, req.ClientId, ct);

        if (capabilities is not { CanViewNutritionPlans: true })
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // After the source and target lookups, so a missing or foreign plan or client still answers 404.
        if (await this.RespondIfFeatureNotEntitledAsync(
                db, nutritionistId, entitlements => entitlements.CanCreatePlans,
                "The current subscription does not include creating plans.", ct))
        {
            return;
        }

        var targetClientUserId = await db.ClientProfiles
            .AsNoTracking()
            .Where(cp => cp.PublicId == req.ClientId)
            .Select(cp => (Guid?)cp.UserId)
            .FirstOrDefaultAsync(ct);

        if (targetClientUserId is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (await this.RespondIfClientOverPlanLimitAsync(db, nutritionistId, targetClientUserId.Value, ct))
        {
            return;
        }

        if (req.StartDate.HasValue && await HasOverlappingPlanAsync(
                targetClientUserId.Value, req.StartDate.Value, source.Weeks.Count, ct))
        {
            await this.SendProblemAsync(409, ErrorCodes.PlanOverlap,
                "The selected date range overlaps an existing plan for this client.", ct);
            return;
        }

        var plan = new NutritionPlan
        {
            ExternalId = Guid.NewGuid(),
            ClientId = targetClientUserId.Value,
            NutritionistId = nutritionistId,
            Name = req.Name ?? source.Name,
            SourceTemplateId = source.SourceTemplateId,
            Status = NutritionPlanStatus.Draft,
            GlobalSettings = source.GlobalSettings,
            Goal = source.Goal,
            TargetWeightKg = targetClientUserId.Value == source.ClientId ? source.TargetWeightKg : null,
            Supplements = NutritionPlanContentCloner.CloneSupplements(source.Supplements, mintFreshExternalIds: true),
            Weeks = NutritionPlanContentCloner.CloneWeeksPlanToPlan(source.Weeks),
            Version = 1,
            DateCreated = timeProvider.GetUtcNow().UtcDateTime,
            StartDate = req.StartDate.HasValue ? DateTime.SpecifyKind(req.StartDate.Value.Date, DateTimeKind.Utc) : null
        };

        await mongo.NutritionPlans.InsertOneAsync(plan, cancellationToken: ct);

        await HttpContext.Response.SendAsync(new CopyNutritionPlanResponse
        {
            PlanId = plan.ExternalId,
            ClientId = req.ClientId,
            Name = plan.Name,
            Status = plan.Status.ToString(),
            DateCreated = plan.DateCreated
        }, 201, cancellation: ct);
    }

    private async Task<bool> HasOverlappingPlanAsync(
        Guid clientUserId, DateTime startDate, int weekCount, CancellationToken ct)
    {
        var candidateStart = DateTime.SpecifyKind(startDate.Date, DateTimeKind.Utc);

        var existingFilter = Builders<NutritionPlan>.Filter.Eq(p => p.ClientId, clientUserId)
                            & Builders<NutritionPlan>.Filter.Ne(p => p.Status, NutritionPlanStatus.Archived)
                            & Builders<NutritionPlan>.Filter.Ne(p => p.Status, NutritionPlanStatus.Completed)
                            & Builders<NutritionPlan>.Filter.Ne(p => p.StartDate, null);

        using var existingCursor = await mongo.NutritionPlans.FindAsync(existingFilter, cancellationToken: ct);
        var existingPlans = await existingCursor.ToListAsync(ct);

        return existingPlans.Any(p =>
            PlanWindowResolver.WindowsOverlap(candidateStart, weekCount, p.StartDate!.Value, p.Weeks.Count));
    }
}
