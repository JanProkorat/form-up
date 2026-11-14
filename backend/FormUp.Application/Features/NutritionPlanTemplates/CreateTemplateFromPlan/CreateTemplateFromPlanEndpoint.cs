using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Enums;
using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.NutritionPlanTemplates.Shared;
using FormUp.Application.Infrastructure.Data.MongoDb;
using MongoDB.Driver;

namespace FormUp.Application.Features.NutritionPlanTemplates.CreateTemplateFromPlan;

/// <summary>
/// Saves an existing nutrition plan as a new template owned by the caller, stripping every
/// client-only field (<c>ClientId</c>, <c>Status</c>, <c>StartDate</c>, publish/complete dates,
/// <c>QuestionnaireResponseId</c>, <c>TargetWeightKg</c>).
/// </summary>
/// <param name="mongo">MongoDB context.</param>
/// <param name="timeProvider">Injected time source for audit timestamps.</param>
/// <param name="linkAuthorizationService">Resolves link capabilities — authorship identifies the
/// source plan, the caller's live link to its client decides access.</param>
/// <param name="macroCalculator">Computes meal kcal for the denormalized list stats; the plan's stored
/// day totals can be stale and are never read.</param>
[RemovedCoachRole(AppRoles.Nutritionist, RemovedCoachRoleMode.Refuse)]
public class CreateTemplateFromPlanEndpoint(
    IMongoContext mongo,
    TimeProvider timeProvider,
    IClientLinkAuthorizationService linkAuthorizationService,
    IMacroCalculatorService macroCalculator)
    : Endpoint<CreateNutritionPlanTemplateFromPlanRequest, NutritionPlanTemplateSummaryDto>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/nutrition/plan-templates/from-plan");
        Roles(AppRoles.Nutritionist);
        Description(b => b.ClearDefaultProduces(StatusCodes.Status200OK)
            .Produces<NutritionPlanTemplateSummaryDto>(StatusCodes.Status201Created));
        Summary(s =>
        {
            s.Summary = "Save a nutrition plan as a template";
            s.Description = "Copies an existing plan's content into a new template, dropping client-only fields.";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(CreateNutritionPlanTemplateFromPlanRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);

        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var ownerId = Guid.Parse(userId);

        if (await this.RespondIfCoachFeatureNotEntitledAsync(
                ownerId, entitlements => entitlements.CanUsePlanTemplates,
                "The current subscription does not include nutrition plan templates.", ct))
        {
            return;
        }

        // NutritionPlan is not an ILibraryDocument, so ownership is checked directly against
        // NutritionistId in the fetch filter — a missing plan and an unowned plan are
        // indistinguishable, both routed through the same shaped 404 this library uses.
        var filter = Builders<NutritionPlan>.Filter.Eq(p => p.ExternalId, req.PlanId)
                     & Builders<NutritionPlan>.Filter.Eq(p => p.NutritionistId, ownerId);

        using var cursor = await mongo.NutritionPlans.FindAsync(filter, cancellationToken: ct);
        var plan = await cursor.FirstOrDefaultAsync(ct);

        if (plan is null)
        {
            await this.SendLibraryNotFoundAsync(NutritionPlanTemplateLibrary.Denial, ct);
            return;
        }

        // Authorship is permanent; the collaboration is not. Require the caller's link to the
        // plan's client to still grant nutrition access, routed through the same shaped 404.
        // plan.ClientId is ApplicationUser.Id (#840) — the UserId-addressed overload.
        var capabilities = await linkAuthorizationService.GetCapabilitiesByClientUserIdAsync(
            ownerId, plan.ClientId, ct);

        if (capabilities is not { CanViewNutritionPlans: true })
        {
            await this.SendLibraryNotFoundAsync(NutritionPlanTemplateLibrary.Denial, ct);
            return;
        }

        var template = new NutritionPlanTemplate
        {
            ExternalId = Guid.NewGuid(),
            OwnerId = ownerId,
            Name = req.Name,
            Description = req.Description,
            Goal = plan.Goal,
            GlobalSettings = plan.GlobalSettings,
            Supplements = NutritionPlanContentCloner.CloneSupplements(plan.Supplements, mintFreshExternalIds: true),
            Weeks = NutritionPlanContentCloner.CloneWeeksFromPlan(plan.Weeks),
            WeekCount = plan.Weeks.Count,
            Visibility = req.Visibility,
            Version = 1,
            DateCreated = timeProvider.GetUtcNow().UtcDateTime
        };

        TemplateStatsCalculator.Apply(template, macroCalculator);

        await mongo.NutritionPlanTemplates.InsertOneAsync(template, cancellationToken: ct);

        await HttpContext.Response.SendAsync(
            NutritionPlanTemplateSummaryDto.FromDocument(template, ownerId), 201, cancellation: ct);
    }
}
