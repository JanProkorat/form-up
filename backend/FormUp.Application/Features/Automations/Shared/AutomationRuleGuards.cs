using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.Automations.Shared;

/// <summary>
/// Load-and-authorize sequences shared by the automation endpoints. Each returns null (or a failed
/// result) after writing the response, so the caller only has to return.
/// </summary>
public static class AutomationRuleGuards
{
    /// <summary>
    /// Loads the caller's professional profile, or answers 404; answers 403
    /// (<see cref="ErrorCodes.SubscriptionRequired"/>) when the subscription does not include automations.
    /// Every coach automation endpoint starts here, reads included.
    /// </summary>
    public static async Task<ProfessionalProfile?> LoadCallerProfileOrRespondAsync(
        this IEndpoint endpoint, IApplicationDbContext db, Guid userId, CancellationToken ct)
    {
        var profile = await db.ProfessionalProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);

        if (profile is null)
        {
            await endpoint.HttpContext.Response.SendNotFoundAsync(ct);
            return null;
        }

        if (await endpoint.RespondIfFeatureNotEntitledAsync(
                db, profile.Id, entitlements => entitlements.CanUseAutomations,
                "The current subscription does not include automations.", ct))
        {
            return null;
        }

        return profile;
    }

    /// <summary>
    /// For a SendForm rule, resolves the questionnaire the caller owns; answers a coded 404 when it is
    /// not the caller's. Other actions resolve to a null id. A check-in form must come with the check-in
    /// fields and any other rule must not carry them; a mismatch answers a coded 400.
    /// </summary>
    public static async Task<(bool Ok, long? QuestionnaireId, bool IsCheckIn)> ResolveQuestionnaireOrRespondAsync(
        this IEndpoint endpoint, IApplicationDbContext db, Guid userId, AutomationRuleBody body, CancellationToken ct)
    {
        if (body.ActionType != AutomationActionType.SendForm)
        {
            return (!await endpoint.RespondIfCheckInBodyInvalidAsync(body, false, ct), null, false);
        }

        var questionnaire = await db.Questionnaires
            .AsNoTracking()
            .Where(q => q.PublicId == body.QuestionnaireId && q.ProfessionalId == userId)
            .Select(q => new { q.Id, q.IsActive, q.Type })
            .FirstOrDefaultAsync(ct);

        if (questionnaire is null)
        {
            await endpoint.SendProblemAsync(
                StatusCodes.Status404NotFound,
                ErrorCodes.AutomationQuestionnaireNotFound,
                "Questionnaire not found.",
                ct);
            return (false, null, false);
        }

        if (!AutomationQuestionnaireUsability.IsUsable(questionnaire.IsActive))
        {
            await endpoint.SendProblemAsync(
                StatusCodes.Status400BadRequest,
                ErrorCodes.AutomationQuestionnaireNotUsable,
                "The questionnaire is inactive and cannot be sent by a rule.",
                ct);
            return (false, null, false);
        }

        var isCheckIn = AutomationQuestionnaireUsability.IsCheckInForm(questionnaire.Type);

        if (await endpoint.RespondIfCheckInBodyInvalidAsync(body, isCheckIn, ct))
        {
            return (false, null, isCheckIn);
        }

        return (true, questionnaire.Id, isCheckIn);
    }

    /// <summary>
    /// A check-in rule needs a weekday, a local time and an answer window on a weekly schedule; any other
    /// rule carries none of the check-in fields. Answers a coded 400 and returns true when that is not so.
    /// </summary>
    public static async Task<bool> RespondIfCheckInBodyInvalidAsync(
        this IEndpoint endpoint, AutomationTriggerBody body, bool isCheckIn, CancellationToken ct)
    {
        if (!isCheckIn)
        {
            if (body.ScheduleWeekday is null && body.AnswerWindowHours is null
                && body.ReminderAfterHours is null && body.NotifyCoachOnMissedWeek is null)
            {
                return false;
            }

            await endpoint.SendProblemAsync(
                StatusCodes.Status400BadRequest,
                ErrorCodes.AutomationCheckInFieldsNotAllowed,
                "Check-in fields belong to a rule that sends a check-in form.",
                ct);
            return true;
        }

        var isWeekly = body.TriggerKind == AutomationTriggerKind.Schedule
                       && body.ScheduleInterval == 1
                       && body.ScheduleUnit == AutomationScheduleUnit.Weeks;

        if (body.ScheduleWeekday is not null && body.LocalTime is not null && body.AnswerWindowHours is not null && isWeekly)
        {
            return false;
        }

        await endpoint.SendProblemAsync(
            StatusCodes.Status400BadRequest,
            ErrorCodes.AutomationCheckInInvalid,
            "A check-in rule needs a weekday, a time, an answer window and a weekly schedule every week.",
            ct);
        return true;
    }

    /// <summary>
    /// Checks the coach's subscription for a check-in rule write: the weekly check-in feature, or, with
    /// <paramref name="perClient"/>, per-client check-in configuration (an override or a pause). Answers
    /// a coded 403 (<see cref="ErrorCodes.SubscriptionRequired"/>) and returns true when the plan does not include it.
    /// </summary>
    /// <param name="endpoint">The endpoint instance.</param>
    /// <param name="entitlementService">Resolves subscription entitlements.</param>
    /// <param name="profileId">The coach's professional profile id.</param>
    /// <param name="perClient">True to check the per-client configuration flag instead of the check-in flag.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<bool> RespondIfCheckInNotEntitledAsync(
        this IEndpoint endpoint, EntitlementService entitlementService, long profileId, bool perClient, CancellationToken ct)
    {
        var entitlements = await entitlementService.GetEntitlementsAsync(profileId, ct);

        if (perClient ? entitlements.CanUsePerClientCheckInConfig : entitlements.CanUseWeeklyCheckIns)
        {
            return false;
        }

        await endpoint.SendProblemAsync(
            StatusCodes.Status403Forbidden,
            ErrorCodes.SubscriptionRequired,
            perClient
                ? "The current subscription does not include per-client check-in configuration."
                : "The current subscription does not include weekly check-ins.",
            ct);
        return true;
    }

    /// <summary>
    /// A coach can have one enabled check-in rule. Answers 409 and returns true when another enabled
    /// check-in rule exists. Call it inside the write's transaction, after locking the coach's profile.
    /// </summary>
    public static async Task<bool> RespondIfCheckInRuleExistsAsync(
        this IEndpoint endpoint, IApplicationDbContext db, long profileId, long? exceptRuleId, CancellationToken ct)
    {
        var exists = await db.AutomationRules
            .AsNoTracking()
            .AnyAsync(r => r.OwnerProfessionalProfileId == profileId
                           && r.Enabled
                           && r.Id != exceptRuleId
                           && r.ActionType == AutomationActionType.SendForm
                           && r.Questionnaire!.Type == QuestionnaireType.CheckIn, ct);

        if (!exists)
        {
            return false;
        }

        await endpoint.SendProblemAsync(
            StatusCodes.Status409Conflict,
            ErrorCodes.CheckInRuleExists,
            "You already have an enabled check-in rule.",
            ct);
        return true;
    }

    /// <summary>
    /// Resolves a request audience: a Tag audience needs a tag owned by the caller's profile, a Chosen
    /// audience needs every client to have a live link to the caller. Any miss rejects the whole request.
    /// </summary>
    public static async Task<ResolvedAutomationAudience?> ResolveAudienceOrRespondAsync(
        this IEndpoint endpoint,
        IApplicationDbContext db,
        ProfessionalProfile profile,
        AutomationAudienceBody body,
        AutomationAudience audience,
        CancellationToken ct)
    {
        if (audience == AutomationAudience.Tag)
        {
            var tagId = await db.ClientTags
                .AsNoTracking()
                .Where(t => t.PublicId == body.AudienceTagId && t.OwnerProfessionalProfileId == profile.Id)
                .Select(t => (long?)t.Id)
                .FirstOrDefaultAsync(ct);

            if (tagId is null)
            {
                await endpoint.SendProblemAsync(
                    StatusCodes.Status404NotFound,
                    ErrorCodes.AutomationTagNotFound,
                    "Tag not found.",
                    ct);
                return null;
            }

            return new ResolvedAutomationAudience(audience, tagId, []);
        }

        if (audience == AutomationAudience.Chosen)
        {
            var publicIds = body.AudienceClientIds.Distinct().ToList();

            var clientProfileIds = await db.ClientProfessionalLinks
                .AsNoTracking()
                .Where(l => l.ProfessionalProfileId == profile.Id && l.ConnectionState != ConnectionState.Ended && publicIds.Contains(l.ClientProfile.PublicId))
                .Select(l => l.ClientProfileId)
                .Distinct()
                .ToListAsync(ct);

            if (clientProfileIds.Count != publicIds.Count)
            {
                await endpoint.SendProblemAsync(
                    StatusCodes.Status404NotFound,
                    ErrorCodes.AutomationAudienceClientNotFound,
                    "One or more clients were not found.",
                    ct);
                return null;
            }

            return new ResolvedAutomationAudience(audience, null, clientProfileIds);
        }

        return new ResolvedAutomationAudience(audience, null, []);
    }

    /// <summary>
    /// Loads (tracked, with detail navigations) a rule owned by the caller's profile, or answers a coded 404.
    /// </summary>
    public static async Task<AutomationRule?> LoadOwnedRuleOrRespondAsync(
        this IEndpoint endpoint, IApplicationDbContext db, long profileId, Guid ruleId, bool track, CancellationToken ct)
    {
        var query = db.AutomationRules.Where(r => r.PublicId == ruleId && r.OwnerProfessionalProfileId == profileId).WithDetails();

        if (!track)
        {
            query = query.AsNoTracking();
        }

        var rule = await query.FirstOrDefaultAsync(ct);

        if (rule is null)
        {
            await endpoint.SendProblemAsync(
                StatusCodes.Status404NotFound,
                ErrorCodes.AutomationRuleNotFound,
                "Automation rule not found.",
                ct);
        }

        return rule;
    }
}
