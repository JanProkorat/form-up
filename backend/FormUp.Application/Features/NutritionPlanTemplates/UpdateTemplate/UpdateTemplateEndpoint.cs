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

namespace FormUp.Application.Features.NutritionPlanTemplates.UpdateTemplate;

/// <summary>
/// Full-state update of a nutrition plan template: replaces name, description, goal/dietary
/// style, settings, week tree, and supplements. Owner-only, version-gated.
/// </summary>
/// <param name="mongo">MongoDB context.</param>
/// <param name="guard">Shared version-gated fetch-check-replace-409 skeleton.</param>
/// <param name="timeProvider">Injected time source for audit timestamps.</param>
/// <param name="macroCalculator">Computes meal kcal for the denormalized list stats.</param>
[RemovedCoachRole(AppRoles.Nutritionist, RemovedCoachRoleMode.Refuse)]
public class UpdateTemplateEndpoint(
    IMongoContext mongo,
    PlanConcurrencyGuard guard,
    TimeProvider timeProvider,
    IMacroCalculatorService macroCalculator)
    : Endpoint<UpdateNutritionPlanTemplateRequest, NutritionPlanTemplateDetailDto>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Put("/nutrition/plan-templates/{TemplateId}");
        Roles(AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Full-state update of a nutrition plan template";
            s.Description = "Replaces the template's name, settings, and week tree. Owner-only, version-gated.";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(UpdateNutritionPlanTemplateRequest req, CancellationToken ct)
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

        var updated = await this.LoadAndReplaceLibraryEntryWithVersionGuardAsync(
            mongo.NutritionPlanTemplates,
            req.TemplateId,
            ownerId,
            NutritionPlanTemplateLibrary.Denial,
            req.Version,
            guard,
            (template, _) => MutateAsync(template, req),
            ct);

        if (updated is null)
        {
            return;
        }

        var usage = await NutritionPlanTemplateUsage.LoadCallerUsageAsync(mongo, ownerId, updated.ExternalId, ct);

        await Send.OkAsync(
            NutritionPlanTemplateDetailDto.FromDocument(updated, ownerId, usage.GetValueOrDefault(updated.ExternalId)), ct);
    }

    /// <summary>
    /// Endpoint-specific mutation applied to the fetched template before the version-gated
    /// replace. Always returns <c>true</c> — every validation rule already ran in
    /// <see cref="UpdateNutritionPlanTemplateValidator"/> before <c>HandleAsync</c> was reached.
    /// </summary>
    private Task<bool> MutateAsync(NutritionPlanTemplate template, UpdateNutritionPlanTemplateRequest req)
    {
        var weeks = TemplateRequestMapper.ToWeeks(req.Weeks, template.Weeks);

        template.Name = req.Name;
        template.Description = req.Description;
        template.Goal = req.Goal;
        template.DietaryStyle = req.DietaryStyle;
        template.GlobalSettings = req.GlobalSettings;
        template.Supplements = TemplateRequestMapper.ToSupplements(req.Supplements);
        template.Weeks = weeks;
        template.WeekCount = weeks.Count;
        TemplateStatsCalculator.Apply(template, macroCalculator);
        template.DateUpdated = timeProvider.GetUtcNow().UtcDateTime;
        template.Version += 1;

        return Task.FromResult(true);
    }
}
