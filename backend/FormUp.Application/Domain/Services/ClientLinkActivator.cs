using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Domain.Services;

/// <summary>
/// Creates the client-professional link for an accepted invite, or switches an ended one back on.
/// Call it inside the transaction, after the client profile row is locked.
/// </summary>
public static class ClientLinkActivator
{
    /// <summary>
    /// Loads the pair's link and creates it, reactivates it, or leaves it alone when already active.
    /// The invite's scope and questionnaire replace whatever an ended link carried.
    /// </summary>
    /// <param name="db">Database context; the new or changed link is tracked, not saved.</param>
    /// <param name="userManager">Resolves the professional's identity roles.</param>
    /// <param name="clientProfileId">The accepting client's profile id.</param>
    /// <param name="professional">The inviting professional's profile.</param>
    /// <param name="requestedScope">Scope stored on the invite, or null for every active role.</param>
    /// <param name="questionnaireId">Questionnaire from the invite; overwrites the old value even when null.</param>
    /// <param name="skipOnboardingForm">The invite's "no onboarding form" choice; set on create and reactivate, left alone on an active link.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<ClientLinkActivationResult> ActivateAsync(
        IApplicationDbContext db,
        UserManager<ApplicationUser> userManager,
        long clientProfileId,
        ProfessionalProfile professional,
        LinkCapabilityScope? requestedScope,
        long? questionnaireId,
        bool skipOnboardingForm,
        CancellationToken ct)
    {
        var link = await db.ClientProfessionalLinks
            .FirstOrDefaultAsync(l => l.ClientProfileId == clientProfileId
                                      && l.ProfessionalProfileId == professional.Id, ct);

        if (link is { ConnectionState: not ConnectionState.Ended })
        {
            return await WidenActiveLinkAsync(db, userManager, link, professional, requestedScope, ct);
        }

        var professionalUser = await userManager.FindByIdAsync(professional.UserId.ToString());
        var professionalRoles = professionalUser is not null
            ? await userManager.GetRolesAsync(professionalUser)
            : [];

        // Grant view access per role actually held — independent booleans, so a professional
        // holding both Trainer and Nutritionist keeps access to both plan types (#776).
        var activeRoles = CoachRoleStatus.ActiveRoles(professionalRoles, professional);
        var professionalRole = activeRoles.Contains(AppRoles.Nutritionist) ? UserRole.Nutritionist : UserRole.Trainer;

        // A coach role the professional has since removed is read-only: it may not seed a link.
        if (!CoachRoleStatus.TryResolveLinkGrant(
                requestedScope, professionalRoles, professional,
                out var canViewNutritionPlans, out var canViewTrainingPlans))
        {
            return new ClientLinkActivationResult(ClientLinkActivationOutcome.CoachRoleRemoved, null);
        }

        // A client may hold at most one active coach per profession (#980); this covers reactivation too.
        if (await ProfessionSlotGuard.IsSlotTakenByAnotherProfessionalAsync(
                db.ClientProfessionalLinks, clientProfileId, professional.Id,
                canViewNutritionPlans, canViewTrainingPlans, ct))
        {
            return new ClientLinkActivationResult(ClientLinkActivationOutcome.SlotTaken, null);
        }

        if (link is not null)
        {
            link.ConnectionState = ConnectionState.Connected;
            link.ActivatedAtUtc = DateTime.UtcNow;
            link.ProfessionalRole = professionalRole;
            link.CanViewNutritionPlans = canViewNutritionPlans;
            link.CanViewTrainingPlans = canViewTrainingPlans;
            link.QuestionnaireId = questionnaireId;
            link.SkipOnboardingForm = skipOnboardingForm;

            return new ClientLinkActivationResult(
                ClientLinkActivationOutcome.Reactivated, link,
                ClientLinkActivationResult.ScopeOf(canViewNutritionPlans, canViewTrainingPlans));
        }

        link = new ClientProfessionalLink
        {
            ClientProfileId = clientProfileId,
            ProfessionalProfileId = professional.Id,
            ProfessionalRole = professionalRole,
            ConnectionState = ConnectionState.Connected,
            ActivatedAtUtc = DateTime.UtcNow,
            CanViewNutritionPlans = canViewNutritionPlans,
            CanViewTrainingPlans = canViewTrainingPlans,
            QuestionnaireId = questionnaireId,
            SkipOnboardingForm = skipOnboardingForm,
        };
        db.ClientProfessionalLinks.Add(link);

        return new ClientLinkActivationResult(
            ClientLinkActivationOutcome.Created, link,
            ClientLinkActivationResult.ScopeOf(canViewNutritionPlans, canViewTrainingPlans));
    }

    /// <summary>
    /// Adds the plan types the invite covers beyond what the active link already grants. Never narrows,
    /// and leaves the role, questionnaire, skip-onboarding flag and activation time alone. The widened link and the added scope
    /// are returned so the caller can attach the invite's questionnaire to that part only.
    /// </summary>
    private static async Task<ClientLinkActivationResult> WidenActiveLinkAsync(
        IApplicationDbContext db,
        UserManager<ApplicationUser> userManager,
        ClientProfessionalLink link,
        ProfessionalProfile professional,
        LinkCapabilityScope? requestedScope,
        CancellationToken ct)
    {
        var professionalUser = await userManager.FindByIdAsync(professional.UserId.ToString());
        var professionalRoles = professionalUser is not null
            ? await userManager.GetRolesAsync(professionalUser)
            : [];

        var grantResolved = CoachRoleStatus.TryResolveLinkGrant(
            requestedScope, professionalRoles, professional,
            out var canViewNutritionPlans, out var canViewTrainingPlans);

        // Only the part the link does not cover yet counts as new.
        var addsNutrition = canViewNutritionPlans && !link.CanViewNutritionPlans;
        var addsTraining = canViewTrainingPlans && !link.CanViewTrainingPlans;

        if (!addsNutrition && !addsTraining)
        {
            return new ClientLinkActivationResult(ClientLinkActivationOutcome.AlreadyActive, null);
        }

        // A removed coach role may not add anything to the link.
        if (!grantResolved)
        {
            return new ClientLinkActivationResult(ClientLinkActivationOutcome.CoachRoleRemoved, null);
        }

        if (await ProfessionSlotGuard.IsSlotTakenByAnotherProfessionalAsync(
                db.ClientProfessionalLinks, link.ClientProfileId, professional.Id,
                addsNutrition, addsTraining, ct))
        {
            return new ClientLinkActivationResult(ClientLinkActivationOutcome.SlotTaken, null);
        }

        link.CanViewNutritionPlans |= addsNutrition;
        link.CanViewTrainingPlans |= addsTraining;

        return new ClientLinkActivationResult(
            ClientLinkActivationOutcome.Extended, link,
            ClientLinkActivationResult.ScopeOf(addsNutrition, addsTraining));
    }
}
