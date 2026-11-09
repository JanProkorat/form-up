using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.NutritionPlans.Shared;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using Microsoft.EntityFrameworkCore;
using MongoDB.Driver;

namespace FormUp.Application.Features.NutritionPlans.CreatePlan;

/// <summary>
/// Creates a new nutrition plan for a client in Draft status.
/// </summary>
/// <remarks>
/// Intentionally does not go through <see cref="Domain.Services.PlanConcurrencyGuard"/> —
/// this is an <c>InsertOneAsync</c> of a brand-new document with <c>Version = 1</c>, so
/// there is no existing row to fetch, no version to compare, and no 409 path to extract.
/// See the guard's class doc-comment for the full Create/Delete exclusion rationale (#659 / #695).
/// </remarks>
/// <param name="mongo">MongoDB context.</param>
/// <param name="linkAuthorizationService">Resolves the nutritionist-client link's CanViewNutritionPlans permission.</param>
/// <param name="db">PostgreSQL context for cross-DB validation.</param>
[RemovedCoachRole(AppRoles.Nutritionist, RemovedCoachRoleMode.Refuse)]
public class CreatePlanEndpoint(
    IMongoContext mongo, IClientLinkAuthorizationService linkAuthorizationService, IApplicationDbContext db)
    : Endpoint<CreatePlanRequest, PlanSummaryDto>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/nutrition/plans");
        Roles(AppRoles.Nutritionist);
        Description(b => b.ClearDefaultProduces(StatusCodes.Status200OK)
            .Produces<PlanSummaryDto>(StatusCodes.Status201Created));
        Summary(s =>
        {
            s.Summary = "Create a nutrition plan";
            s.Description = "Creates a new nutrition plan in Draft status for a client.";
            s.Responses[StatusCodes.Status201Created] = "The new Draft plan";
            s.Responses[StatusCodes.Status400BadRequest] = "Invalid request, or the questionnaire response is not found or not submitted";
            s.Responses[StatusCodes.Status401Unauthorized] = "Missing or unreadable caller claim";
            s.Responses[StatusCodes.Status403Forbidden] =
                "The caller's Nutritionist coach role was removed, the subscription does not include plans (SUBSCRIPTION_REQUIRED), " +
                "or the client is over the Free plan's client limit (CLIENT_OVER_PLAN_LIMIT)";
            s.Responses[StatusCodes.Status404NotFound] = "Client not found, or not accessible to the caller";
            s.Responses[StatusCodes.Status409Conflict] = "The start date window overlaps another plan of the client (PLAN_OVERLAP)";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(CreatePlanRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);

        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var nutritionistId = Guid.Parse(userId);

        // req.ClientId is the nutritionist-facing ClientProfile.PublicId — the PublicId-addressed
        // overload. Nutrition plans require CanViewNutritionPlans specifically.
        var capabilities = await linkAuthorizationService.GetCapabilitiesByClientPublicIdAsync(
            nutritionistId, req.ClientId, ct);

        if (capabilities is not { CanViewNutritionPlans: true })
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (await this.RespondIfFeatureNotEntitledAsync(
                db, nutritionistId, entitlements => entitlements.CanCreatePlans,
                "The current subscription does not include creating plans.", ct))
        {
            return;
        }

        // req.ClientId is the trainer-facing ClientProfile.PublicId — resolve to
        // ApplicationUser.Id, the canonical clientId key for Mongo documents (#840).
        var clientProfile = await db.ClientProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(cp => cp.PublicId == req.ClientId, ct);

        if (clientProfile is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var clientUserId = clientProfile.UserId;

        if (await this.RespondIfClientOverPlanLimitAsync(db, nutritionistId, clientUserId, ct))
        {
            return;
        }

        // Validate questionnaire response link if provided
        if (req.QuestionnaireResponseId.HasValue)
        {
            // QuestionnaireResponse.ClientId is ApplicationUser.Id (set from the auth user
            // id), so compare against the already-resolved clientUserId, not req.ClientId
            // (which is the trainer-facing ClientProfile.PublicId) — see #840.
            var responseExists = await db.QuestionnaireResponses
                .AsNoTracking()
                .AnyAsync(r => r.PublicId == req.QuestionnaireResponseId.Value
                               && r.ProfessionalId == nutritionistId
                               && r.ClientId == clientUserId
                               && r.Status == QuestionnaireResponseStatus.Submitted, ct);

            if (!responseExists)
            {
                ThrowError("QuestionnaireResponseId", "Questionnaire response not found or not submitted.");
                return;
            }
        }

        // Overlap check: a client may hold several sequential, non-overlapping plans of the
        // same type (#780), but their date windows [StartDate, StartDate + WeekCount * 7) must
        // not overlap. Only applies when a StartDate is supplied — Draft plans left unscheduled
        // are unranged and cannot overlap anything. Archived/Completed plans are excluded: they
        // are no longer "in force" and a new plan may legitimately reuse their old window.
        if (req.StartDate.HasValue)
        {
            var candidateStart = DateTime.SpecifyKind(req.StartDate.Value.Date, DateTimeKind.Utc);

            var existingFilter = Builders<NutritionPlan>.Filter.Eq(p => p.ClientId, clientUserId)
                                & Builders<NutritionPlan>.Filter.Ne(p => p.Status, NutritionPlanStatus.Archived)
                                & Builders<NutritionPlan>.Filter.Ne(p => p.Status, NutritionPlanStatus.Completed)
                                & Builders<NutritionPlan>.Filter.Ne(p => p.StartDate, null);

            using var existingCursor = await mongo.NutritionPlans.FindAsync(existingFilter, cancellationToken: ct);
            var existingPlans = await existingCursor.ToListAsync(ct);

            var overlaps = existingPlans.Any(p =>
                PlanWindowResolver.WindowsOverlap(candidateStart, req.WeekCount, p.StartDate!.Value, p.Weeks.Count));

            if (overlaps)
            {
                await this.SendProblemAsync(409, ErrorCodes.PlanOverlap,
                    "The selected date range overlaps an existing plan for this client.", ct);
                return;
            }
        }

        var now = DateTime.UtcNow;

        var plan = new NutritionPlan
        {
            ExternalId = Guid.NewGuid(),
            ClientId = clientUserId,
            NutritionistId = nutritionistId,
            Name = req.Name,
            Status = NutritionPlanStatus.Draft,
            GlobalSettings = req.GlobalSettings,
            QuestionnaireResponseId = req.QuestionnaireResponseId,
            Goal = req.Goal,
            TargetWeightKg = req.TargetWeightKg,
            Weeks = Enumerable.Range(1, req.WeekCount).Select(w => new PlanWeek
            {
                WeekNumber = w,
                Status = WeekStatus.Draft,
                Days = Enumerable.Range(1, 7).Select(d => new PlanDay
                {
                    DayOfWeek = d,
                    Meals = [],
                    DayTotals = null
                }).ToList()
            }).ToList(),
            Version = 1,
            DateCreated = now,
            StartDate = req.StartDate.HasValue ? DateTime.SpecifyKind(req.StartDate.Value.Date, DateTimeKind.Utc) : null
        };

        await mongo.NutritionPlans.InsertOneAsync(plan, cancellationToken: ct);

        // req.ClientId is already the client-facing ClientProfile.PublicId (resolved above to
        // clientUserId for storage) — reuse it directly for the response, no extra lookup needed.
        var response = PlanSummaryDto.FromDocument(plan, req.ClientId);
        await HttpContext.Response.SendAsync(response, 201, cancellation: ct);
    }
}
