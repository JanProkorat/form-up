using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.TrainingPlans.Shared;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using Microsoft.EntityFrameworkCore;
using MongoDB.Driver;

namespace FormUp.Application.Features.TrainingPlans.CreateTrainingPlan;

/// <summary>
/// Creates a new training plan for a client in Draft status.
/// </summary>
/// <remarks>
/// Intentionally does not go through <see cref="FormUp.Application.Domain.Services.PlanConcurrencyGuard"/> —
/// this is an <c>InsertOneAsync</c> of a brand-new document with <c>Version = 1</c>, so
/// there is no existing row to fetch, no version to compare, and no 409 path to extract.
/// See the guard's class doc-comment for the full Create/Delete exclusion rationale (#659 / #695).
/// </remarks>
/// <param name="mongo">MongoDB context.</param>
/// <param name="linkAuthorizationService">Resolves the trainer-client link's CanViewTrainingPlans permission.</param>
/// <param name="db">PostgreSQL context for cross-DB validation.</param>
[RemovedCoachRole(AppRoles.Trainer, RemovedCoachRoleMode.Refuse)]
public class CreateTrainingPlanEndpoint(
    IMongoContext mongo, IClientLinkAuthorizationService linkAuthorizationService, IApplicationDbContext db)
    : Endpoint<CreateTrainingPlanRequest, TrainingPlanSummaryDto>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/training/plans");
        Roles(AppRoles.Trainer);
        Summary(s =>
        {
            s.Summary = "Create a training plan";
            s.Description = "Creates a new training plan in Draft status for a client.";
            s.Response<TrainingPlanSummaryDto>(StatusCodes.Status201Created, "The new Draft plan");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest, "Invalid request, or the questionnaire response is not found or not submitted");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or unreadable caller claim");
            s.Response(StatusCodes.Status403Forbidden,
                "The subscription does not include plans (SUBSCRIPTION_REQUIRED), or the client is over the Free plan's client limit (CLIENT_OVER_PLAN_LIMIT)");
            s.Response(StatusCodes.Status404NotFound, "Client not found, or not accessible to the caller");
            s.Response(StatusCodes.Status409Conflict, "The start date window overlaps another plan of the client (PLAN_OVERLAP)");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(CreateTrainingPlanRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);

        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var trainerId = Guid.Parse(userId);

        // req.ClientId is the trainer-facing ClientProfile.PublicId — the PublicId-addressed
        // overload. Training plans require CanViewTrainingPlans specifically.
        var capabilities = await linkAuthorizationService.GetCapabilitiesByClientPublicIdAsync(
            trainerId, req.ClientId, ct);

        if (capabilities is not { CanViewTrainingPlans: true })
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (await this.RespondIfFeatureNotEntitledAsync(
                db, trainerId, entitlements => entitlements.CanCreatePlans,
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

        if (await this.RespondIfClientOverPlanLimitAsync(db, trainerId, clientUserId, ct))
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
                               && r.ProfessionalId == trainerId
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

            var existingFilter = Builders<TrainingPlan>.Filter.Eq(p => p.ClientId, clientUserId)
                                & Builders<TrainingPlan>.Filter.Ne(p => p.Status, TrainingPlanStatus.Archived)
                                & Builders<TrainingPlan>.Filter.Ne(p => p.Status, TrainingPlanStatus.Completed)
                                & Builders<TrainingPlan>.Filter.Ne(p => p.StartDate, null);

            using var existingCursor = await mongo.TrainingPlans.FindAsync(existingFilter, cancellationToken: ct);
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

        var plan = new TrainingPlan
        {
            ExternalId = Guid.NewGuid(),
            ClientId = clientUserId,
            TrainerId = trainerId,
            Name = req.Name,
            Description = req.Description?.Trim(),
            Status = TrainingPlanStatus.Draft,
            QuestionnaireResponseId = req.QuestionnaireResponseId,
            Goal = req.Goal,
            TargetWeightKg = req.TargetWeightKg,
            Weeks = Enumerable.Range(1, req.WeekCount).Select(w => new TrainingWeek
            {
                WeekNumber = w,
                Status = WeekStatus.Draft,
                Days = Enumerable.Range(1, 7).Select(d => new TrainingDay
                {
                    DayOfWeek = d,
                    Sessions = []
                }).ToList()
            }).ToList(),
            Version = 1,
            DateCreated = now,
            StartDate = req.StartDate.HasValue ? DateTime.SpecifyKind(req.StartDate.Value.Date, DateTimeKind.Utc) : null
        };

        await mongo.TrainingPlans.InsertOneAsync(plan, cancellationToken: ct);

        // req.ClientId is already the client-facing ClientProfile.PublicId (resolved above to
        // clientUserId for storage) — reuse it directly for the response, no extra lookup needed.
        var response = TrainingPlanSummaryDto.FromDocument(plan, req.ClientId);
        await HttpContext.Response.SendAsync(response, 201, cancellation: ct);
    }
}
