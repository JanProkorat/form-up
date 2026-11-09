using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.TrainingPlans.GetTrainingPlan;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using Microsoft.EntityFrameworkCore;
using MongoDB.Driver;

namespace FormUp.Application.Features.TrainingPlans.LinkQuestionnaire;

/// <summary>
/// Links or unlinks a questionnaire response to/from a training plan.
/// Validates that the response belongs to the same professional and client.
/// </summary>
[RemovedCoachRole(AppRoles.Trainer, RemovedCoachRoleMode.WhilePlanInProgress)]
public class LinkTrainingQuestionnaireEndpoint(
    IMongoContext mongo,
    IApplicationDbContext db,
    PlanConcurrencyGuard guard,
    IClientLinkAuthorizationService linkAuthorizationService)
    : Endpoint<LinkTrainingQuestionnaireRequest, GetTrainingPlanResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Put("/training/plans/{PlanId}/link-questionnaire");
        Roles(AppRoles.Trainer);
        Summary(s =>
        {
            s.Summary = "Link questionnaire to training plan";
            s.Description = "Links a submitted questionnaire response to a plan, or unlinks by passing null.";
            s.Responses[StatusCodes.Status403Forbidden] =
                "The client is over the Free plan's client limit and the plan's last published week has ended (CLIENT_OVER_PLAN_LIMIT)";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(LinkTrainingQuestionnaireRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);
        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var trainerId = Guid.Parse(userId);

        var lookupFilter = Builders<TrainingPlan>.Filter.Eq(p => p.ExternalId, req.PlanId)
                     & Builders<TrainingPlan>.Filter.Eq(p => p.TrainerId, trainerId);
        var replaceFilter = Builders<TrainingPlan>.Filter.Eq(p => p.ExternalId, req.PlanId)
                            & Builders<TrainingPlan>.Filter.Eq(p => p.Version, req.Version);

        var guardResult = await guard.ReplaceWithVersionGuardAsync(
            mongo.TrainingPlans,
            lookupFilter,
            replaceFilter,
            req.Version,
            p => p.Version,
            (plan, authorizeCt) => AuthorizeAsync(plan, trainerId, authorizeCt),
            async (plan, mutateCt) =>
            {
                // Only draft or active plans can have their questionnaire link changed
                if (plan.Status is TrainingPlanStatus.Completed or TrainingPlanStatus.Archived)
                {
                    ThrowError("Only draft or active plans can have their questionnaire link changed.");
                    return false;
                }

                // Validate the questionnaire response if linking (not unlinking)
                if (req.QuestionnaireResponseId.HasValue)
                {
                    var responseExists = await db.QuestionnaireResponses
                        .AsNoTracking()
                        .AnyAsync(r => r.PublicId == req.QuestionnaireResponseId.Value
                                       && r.ProfessionalId == trainerId
                                       && r.ClientId == plan.ClientId
                                       && r.Status == QuestionnaireResponseStatus.Submitted, mutateCt);

                    if (!responseExists)
                    {
                        ThrowError("QuestionnaireResponseId", "Questionnaire response not found or not submitted.");
                        return false;
                    }
                }

                // Update the link
                plan.QuestionnaireResponseId = req.QuestionnaireResponseId;
                plan.DateUpdated = DateTime.UtcNow;
                plan.Version += 1;

                return true;
            },
            ct);

        switch (guardResult.Outcome)
        {
            case PlanConcurrencyOutcome.NotFound:
                await Send.NotFoundAsync(ct);
                return;
            case PlanConcurrencyOutcome.VersionConflict:
                await this.SendProblemAsync(409, ErrorCodes.PlanVersionConflict,
                    "Version conflict. The plan was modified by another request.", ct);
                return;
            case PlanConcurrencyOutcome.ReplaceConflict:
                await this.SendProblemAsync(409, ErrorCodes.PlanVersionConflict,
                    "Version conflict. The plan was modified concurrently.", ct);
                return;
            case PlanConcurrencyOutcome.HandledByMutator:
                // The authorize delegate already wrote its 404.
                return;
        }

        var plan = guardResult.Document!;

        // Response ClientId must stay the client-facing ClientProfile.PublicId (pre-#840
        // contract) — plan.ClientId is the internal ApplicationUser.Id storage key.
        var clientPublicId = await db.ResolveClientPublicIdAsync(plan.ClientId, ct);
        await Send.OkAsync(GetTrainingPlanResponse.FromDocument(plan, clientPublicId), ct);
    }

    /// <summary>
    /// The lookup filter proved authorship, which is permanent. Access is not — require the
    /// caller's link to the plan's client to still grant training access. Runs before the
    /// guard's version comparison so a denial is indistinguishable from a missing plan.
    /// </summary>
    private async Task<bool> AuthorizeAsync(TrainingPlan plan, Guid trainerId, CancellationToken ct)
    {
        // plan.ClientId is ApplicationUser.Id (#840) — the UserId-addressed overload.
        var capabilities = await linkAuthorizationService.GetCapabilitiesByClientUserIdAsync(
            trainerId, plan.ClientId, ct);

        if (capabilities is not { CanViewTrainingPlans: true })
        {
            await Send.NotFoundAsync(ct);
            return false;
        }

        // A client over the Free plan's cap stays editable only until the last published week ends.
        var overCapAccess = await this.RespondIfClientOverPlanLimitAfterRunsUntilAsync(
            db,
            trainerId,
            plan.ClientId,
            PlanRunsUntilResolver.Resolve(
                plan.StartDate, plan.Weeks.Where(w => w.Status == WeekStatus.Published).Select(w => w.WeekNumber)),
            ct);

        return overCapAccess != OverCapEditAccess.Refused;
    }
}
