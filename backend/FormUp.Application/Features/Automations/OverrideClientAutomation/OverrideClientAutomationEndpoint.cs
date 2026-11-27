using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.Automations.Shared;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.Automations.OverrideClientAutomation;

/// <summary>
/// Customises a global rule's message, trigger and options for one client. Any existing pause is kept.
/// </summary>
/// <param name="db">Application database context.</param>
/// <param name="linkAuthorizationService">Resolves link capabilities.</param>
/// <param name="entitlementService">Resolves the coach's subscription entitlements.</param>
[RemovedCoachRole(RemovedCoachRoleMode.Refuse)]
public class OverrideClientAutomationEndpoint(
    IApplicationDbContext db,
    IClientLinkAuthorizationService linkAuthorizationService,
    EntitlementService entitlementService)
    : Endpoint<OverrideClientAutomationRequest>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Put("/trainer/clients/{ClientId}/automations/{RuleId}/override");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Customise a rule for one client";
            s.Description = "Stores a per-client copy of the rule's message, trigger and options.";
            s.Responses[StatusCodes.Status204NoContent] = "Override saved";
            s.Responses[StatusCodes.Status400BadRequest] =
                "Invalid trigger or options, a progress photos message over 500 characters, check-in fields that do not match the rule's form, or a check-in form that is inactive, not a check-in form, or sent for a rule that is not a check-in";
            s.Responses[StatusCodes.Status401Unauthorized] = "Missing or invalid credentials";
            s.Responses[StatusCodes.Status403Forbidden] = "The live link grants no access, or the subscription does not include per-client check-in configuration";
            s.Responses[StatusCodes.Status404NotFound] = "No live link to the client, the rule does not apply to them, or the check-in form is not one of the caller's";
            s.Responses[StatusCodes.Status409Conflict] = "A client-only rule is changed through the rule itself";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(OverrideClientAutomationRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);

        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var context = await this.LoadClientContextOrRespondAsync(
            db, linkAuthorizationService, Guid.Parse(userId), req.ClientId, ct);

        if (context is null)
        {
            return;
        }

        var rule = await this.LoadCoveringRuleOrRespondAsync(db, context, req.RuleId, ct);

        if (rule is null || await this.RespondIfClientOnlyAsync(rule, ct))
        {
            return;
        }

        if (rule.ActionType == AutomationActionType.RequestProgressPhotos &&
            req.Message?.Length > AutomationTriggerBody.PhotoMessageMaxLength)
        {
            this.ThrowErrorWithCode(
                ErrorCodes.AutomationPhotoMessageTooLong,
                "A progress photos message can be at most 500 characters.");
            return;
        }

        var isCheckIn = rule is { ActionType: AutomationActionType.SendForm, Questionnaire: { } form }
                        && AutomationQuestionnaireUsability.IsCheckInForm(form.Type);

        if (await this.RespondIfCheckInBodyInvalidAsync(req, isCheckIn, ct))
        {
            return;
        }

        if (req.CheckInQuestionnaireId is not null && !isCheckIn)
        {
            await this.SendProblemAsync(
                StatusCodes.Status400BadRequest,
                ErrorCodes.AutomationCheckInFieldsNotAllowed,
                "A check-in form belongs to a rule that sends a check-in form.",
                ct);
            return;
        }

        if (isCheckIn && await this.RespondIfCheckInNotEntitledAsync(entitlementService, context.Profile.Id, true, ct))
        {
            return;
        }

        long? overrideFormId = null;

        if (req.CheckInQuestionnaireId is { } pickedFormId)
        {
            var (formOk, resolvedFormId) = await this.ResolveOverrideCheckInFormOrRespondAsync(
                db, Guid.Parse(userId), pickedFormId, rule.QuestionnaireId, ct);

            if (!formOk)
            {
                return;
            }

            overrideFormId = resolvedFormId;
        }

        var clientOverride = await db.AutomationClientOverrides
            .FirstOrDefaultAsync(o => o.AutomationRuleId == rule.Id && o.ClientProfileId == context.Client.Id, ct);

        if (clientOverride is null)
        {
            clientOverride = new AutomationClientOverride
            {
                AutomationRuleId = rule.Id,
                ClientProfileId = context.Client.Id,
            };
            db.AutomationClientOverrides.Add(clientOverride);
        }

        // The form the client gets today against the one they get after this save; a clear counts as a change.
        var currentFormId = (clientOverride.IsCustomized ? clientOverride.CheckInQuestionnaireId : null) ?? rule.QuestionnaireId;
        var formChanged = currentFormId != (overrideFormId ?? rule.QuestionnaireId);

        AutomationRuleWriter.Apply(clientOverride, req, isCheckIn, parentRule: rule);
        clientOverride.CheckInQuestionnaireId = overrideFormId;

        if (formChanged && clientOverride.Enabled)
        {
            clientOverride.EnabledAtUtc = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);

        await Send.NoContentAsync(ct);
    }
}
