using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Enums;
using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Features.NutritionPlanTemplates.Shared;
using FormUp.Application.Infrastructure.Data.MongoDb;
using MongoDB.Driver;

namespace FormUp.Application.Features.NutritionPlanTemplates.DeleteTemplate;

/// <summary>
/// Hard-deletes a nutrition plan template. Owner-only — readable-but-not-owned (another owner's
/// Public entry) returns 403; unreadable (another owner's Private entry) returns 404,
/// indistinguishable from a genuinely missing template.
/// </summary>
/// <param name="mongo">MongoDB context.</param>
[RemovedCoachRole(AppRoles.Nutritionist, RemovedCoachRoleMode.Refuse)]
public class DeleteTemplateEndpoint(IMongoContext mongo) : EndpointWithoutRequest
{
    /// <inheritdoc />
    public override void Configure()
    {
        Delete("/nutrition/plan-templates/{templateId}");
        Roles(AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Delete a nutrition plan template";
            s.Description = "Hard-deletes the template. Owner-only.";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(CancellationToken ct)
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
        var templateId = Route<Guid>("templateId");

        var template = await this.LoadLibraryEntryForWriteOrRespondAsync(
            mongo.NutritionPlanTemplates, templateId, ownerId, NutritionPlanTemplateLibrary.Denial, ct);

        if (template is null)
        {
            return;
        }

        await mongo.NutritionPlanTemplates.DeleteOneAsync(
            Builders<NutritionPlanTemplate>.Filter.Eq(t => t.ExternalId, template.ExternalId), ct);

        await Send.NoContentAsync(ct);
    }
}
