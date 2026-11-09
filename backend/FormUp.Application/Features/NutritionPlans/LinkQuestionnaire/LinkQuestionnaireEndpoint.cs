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
using Microsoft.EntityFrameworkCore;
using MongoDB.Driver;

namespace FormUp.Application.Features.NutritionPlans.LinkQuestionnaire;

/// <summary>
/// Links or unlinks a questionnaire response to/from a nutrition plan.
/// Validates that the response belongs to the same professional and client.
/// </summary>
[RemovedCoachRole(AppRoles.Nutritionist, RemovedCoachRoleMode.WhilePlanInProgress)]
public class LinkQuestionnaireEndpoint(
    IMongoContext mongo,
    IApplicationDbContext db,
    PlanConcurrencyGuard guard,
    IClientLinkAuthorizationService linkAuthorizationService)
    : Endpoint<LinkNutritionQuestionnaireRequest, GetPlanResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Put("/nutrition/plans/{PlanId}/link-questionnaire");
        Roles(AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Link questionnaire to nutrition plan";
            s.Description = "Links a submitted questionnaire response to a plan, or unlinks by passing null.";
            s.Response<GetPlanResponse>(StatusCodes.Status200OK, "The updated plan");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest,
                "The plan is not Draft or Active, or the questionnaire response is not found or not submitted");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or unreadable caller claim");
            s.Response(StatusCodes.Status403Forbidden,
                "The client is over the Free plan's client limit and the plan's last published week has ended (CLIENT_OVER_PLAN_LIMIT)");
            s.Response(StatusCodes.Status404NotFound, "Plan not found, or not editable by the caller");
            s.Response(StatusCodes.Status409Conflict, "The plan was changed concurrently (PLAN_VERSION_CONFLICT)");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(LinkNutritionQuestionnaireRequest req, CancellationToken ct)
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
        var replaceFilter = Builders<NutritionPlan>.Filter.Eq(p => p.ExternalId, req.PlanId)
                            & Builders<NutritionPlan>.Filter.Eq(p => p.Version, req.Version);

        var guardResult = await guard.ReplaceWithVersionGuardAsync(
            mongo.NutritionPlans,
            lookupFilter,
            replaceFilter,
            req.Version,
            p => p.Version,
            (plan, authorizeCt) => AuthorizeAsync(plan, nutritionistId, authorizeCt),
            async (plan, mutateCt) =>
            {
                // Only draft or active plans can have their questionnaire link changed
                if (plan.Status is NutritionPlanStatus.Completed or NutritionPlanStatus.Archived)
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
                                       && r.ProfessionalId == nutritionistId
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
        await Send.OkAsync(GetPlanResponse.FromDocument(plan, clientPublicId), ct);
    }

    /// <summary>
    /// The lookup filter proved authorship, which is permanent. Access is not — require the
    /// caller's link to the plan's client to still grant nutrition access. Runs before the
    /// guard's version comparison so a denial is indistinguishable from a missing plan.
    /// </summary>
    private async Task<bool> AuthorizeAsync(NutritionPlan plan, Guid nutritionistId, CancellationToken ct)
    {
        // plan.ClientId is ApplicationUser.Id (#840) — the UserId-addressed overload.
        var capabilities = await linkAuthorizationService.GetCapabilitiesByClientUserIdAsync(
            nutritionistId, plan.ClientId, ct);

        if (capabilities is not { CanViewNutritionPlans: true })
        {
            await Send.NotFoundAsync(ct);
            return false;
        }

        // A client over the Free plan's cap stays editable only until the last published week ends.
        var overCapAccess = await this.RespondIfClientOverPlanLimitAfterRunsUntilAsync(
            db,
            nutritionistId,
            plan.ClientId,
            PlanRunsUntilResolver.Resolve(
                plan.StartDate, plan.Weeks.Where(w => w.Status == WeekStatus.Published).Select(w => w.WeekNumber)),
            ct);

        return overCapAccess != OverCapEditAccess.Refused;
    }
}
