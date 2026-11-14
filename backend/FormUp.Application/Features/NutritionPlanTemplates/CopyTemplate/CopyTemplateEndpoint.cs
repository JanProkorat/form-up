using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.NutritionPlanTemplates.Shared;
using FormUp.Application.Infrastructure.Data.MongoDb;

namespace FormUp.Application.Features.NutritionPlanTemplates.CopyTemplate;

/// <summary>
/// Clones any readable nutrition plan template (the caller's own, or any nutritionist's
/// <c>Public</c> entry) into a new <c>Private</c> template owned by the caller, with a fresh
/// <c>ExternalId</c>. Read-guarded, not write-guarded — another owner's <c>Public</c> template
/// must stay copyable even though this endpoint writes a new document.
/// </summary>
/// <param name="mongo">MongoDB context.</param>
/// <param name="timeProvider">Injected time source for audit timestamps.</param>
/// <param name="macroCalculator">Computes meal kcal for the denormalized list stats.</param>
[RemovedCoachRole(AppRoles.Nutritionist, RemovedCoachRoleMode.Refuse)]
public class CopyTemplateEndpoint(IMongoContext mongo, TimeProvider timeProvider, IMacroCalculatorService macroCalculator)
    : EndpointWithoutRequest
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/nutrition/plan-templates/{templateId}/copy");
        Roles(AppRoles.Nutritionist);
        Description(b => b.ClearDefaultProduces(StatusCodes.Status200OK)
            .Produces<NutritionPlanTemplateSummaryDto>(StatusCodes.Status201Created));
        Summary(s =>
        {
            s.Summary = "Copy a nutrition plan template";
            s.Description = "Clones any readable template into a new Private template owned by the caller.";
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

        var callerId = Guid.Parse(userId);

        if (await this.RespondIfCoachFeatureNotEntitledAsync(
                callerId, entitlements => entitlements.CanUsePlanTemplates,
                "The current subscription does not include nutrition plan templates.", ct))
        {
            return;
        }
        var templateId = Route<Guid>("templateId");

        var source = await this.LoadLibraryEntryForReadOrRespondAsync(
            mongo.NutritionPlanTemplates, templateId, callerId, NutritionPlanTemplateLibrary.Denial, ct);

        if (source is null)
        {
            return;
        }

        var clonedWeeks = NutritionPlanContentCloner.CloneWeeksAsTemplate(source.Weeks);

        var copy = new NutritionPlanTemplate
        {
            ExternalId = Guid.NewGuid(),
            OwnerId = callerId,
            Name = source.Name,
            Description = source.Description,
            Goal = source.Goal,
            DietaryStyle = source.DietaryStyle,
            GlobalSettings = source.GlobalSettings,
            Supplements = NutritionPlanContentCloner.CloneSupplements(source.Supplements, mintFreshExternalIds: false),
            Weeks = clonedWeeks,
            WeekCount = clonedWeeks.Count,
            Visibility = LibraryVisibility.Private,
            Version = 1,
            DateCreated = timeProvider.GetUtcNow().UtcDateTime
        };

        TemplateStatsCalculator.Apply(copy, macroCalculator);

        await mongo.NutritionPlanTemplates.InsertOneAsync(copy, cancellationToken: ct);

        await HttpContext.Response.SendAsync(
            NutritionPlanTemplateSummaryDto.FromDocument(copy, callerId), 201, cancellation: ct);
    }
}
