using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Enums;
using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Features.NutritionPlanTemplates.Shared;
using FormUp.Application.Infrastructure.Data.MongoDb;

namespace FormUp.Application.Features.NutritionPlanTemplates.CreateTemplate;

/// <summary>
/// Creates a new nutrition plan template for the authenticated nutritionist, either empty
/// (materialized from a week count) or with a full week tree supplied directly.
/// </summary>
/// <param name="mongo">MongoDB context.</param>
/// <param name="timeProvider">Injected time source for audit timestamps.</param>
/// <param name="macroCalculator">Computes meal kcal for the denormalized list stats.</param>
[RemovedCoachRole(AppRoles.Nutritionist, RemovedCoachRoleMode.Refuse)]
public class CreateTemplateEndpoint(IMongoContext mongo, TimeProvider timeProvider, IMacroCalculatorService macroCalculator)
    : Endpoint<CreateNutritionPlanTemplateRequest, NutritionPlanTemplateSummaryDto>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/nutrition/plan-templates");
        Roles(AppRoles.Nutritionist);
        Description(b => b.ClearDefaultProduces(StatusCodes.Status200OK)
            .Produces<NutritionPlanTemplateSummaryDto>(StatusCodes.Status201Created));
        Summary(s =>
        {
            s.Summary = "Create a nutrition plan template";
            s.Description = "Creates a template owned by the caller, either empty or with a supplied week tree.";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(CreateNutritionPlanTemplateRequest req, CancellationToken ct)
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

        var weeks = req.Weeks is { Count: > 0 }
            ? TemplateRequestMapper.ToWeeks(req.Weeks)
            : TemplateRequestMapper.ToEmptyWeeks(req.WeekCount!.Value);

        var template = new NutritionPlanTemplate
        {
            ExternalId = Guid.NewGuid(),
            OwnerId = ownerId,
            Name = req.Name,
            Description = req.Description,
            Goal = req.Goal,
            DietaryStyle = req.DietaryStyle,
            GlobalSettings = req.GlobalSettings,
            Supplements = TemplateRequestMapper.ToSupplements(req.Supplements),
            Weeks = weeks,
            WeekCount = weeks.Count,
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
