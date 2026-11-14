using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Features.NutritionPlanTemplates.Shared;
using FormUp.Application.Infrastructure.Data.MongoDb;

namespace FormUp.Application.Features.NutritionPlanTemplates.GetTemplate;

/// <summary>
/// Fetches a single nutrition plan template's full detail — the caller's own entry at any
/// visibility, or any nutritionist's <c>Public</c> entry.
/// </summary>
/// <param name="mongo">MongoDB context.</param>
public class GetTemplateEndpoint(IMongoContext mongo)
    : Endpoint<GetNutritionPlanTemplateRequest, NutritionPlanTemplateDetailDto>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/nutrition/plan-templates/{TemplateId}");
        Roles(AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Get nutrition plan template detail";
            s.Description = "Returns the full week tree for a template the caller owns, or any Public template.";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(GetNutritionPlanTemplateRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);

        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var callerId = Guid.Parse(userId);

        if (await this.RespondIfCoachFeatureNotEntitledAsync(
                callerId, entitlements => entitlements.CanUsePlanTemplates,
                "The current subscription does not include nutrition plan templates.", ct))
        {
            return;
        }

        var template = await this.LoadLibraryEntryForReadOrRespondAsync(
            mongo.NutritionPlanTemplates, req.TemplateId, callerId, NutritionPlanTemplateLibrary.Denial, ct);

        if (template is null)
        {
            return;
        }

        var usage = await NutritionPlanTemplateUsage.LoadCallerUsageAsync(mongo, callerId, template.ExternalId, ct);

        await Send.OkAsync(
            NutritionPlanTemplateDetailDto.FromDocument(template, callerId, usage.GetValueOrDefault(template.ExternalId)), ct);
    }
}
