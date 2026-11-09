using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.Trainers.ClientRequests.AcceptClientRequest;

/// <summary>
/// Endpoint for a professional to accept a client request, creating a link and optionally assigning a questionnaire.
/// </summary>
[RemovedCoachRole(RemovedCoachRoleMode.Refuse)]
public class AcceptClientRequestEndpoint(
    IApplicationDbContext db,
    IRealtimeNotifier notifier,
    INotificationService notificationService,
    IConversationSeedService conversationSeedService,
    UserManager<ApplicationUser> userManager,
    InvitePackageTermsApplier termsApplier,
    ILogger<AcceptClientRequestEndpoint> logger)
    : Endpoint<AcceptClientRequestRequest>
{
    public override void Configure()
    {
        Post("/trainer/client-requests/{PublicId}/accept");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist, AppRoles.Admin);
        Summary(s =>
        {
            s.Summary = "Accept a client request";
            s.Description = "Accepts a pending client request, creates a client-professional link, and optionally assigns a questionnaire. " +
                "When the request carries a package, its terms (with the optional discount) are applied; an unusable package or discount means accepting without terms.";
            s.Responses[StatusCodes.Status403Forbidden] =
                "The coach role was removed, or the coach is at the active-client limit (CLIENT_LIMIT_REACHED)";
        });
    }

    public override async Task HandleAsync(AcceptClientRequestRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);

        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var userGuid = Guid.Parse(userId);

        var professionalProfile = await db.ProfessionalProfiles
            .FirstOrDefaultAsync(pp => pp.UserId == userGuid, ct);

        if (professionalProfile is null)
        {
            this.ThrowErrorWithCode(ErrorCodes.TrainerProfileMissing, "Professional profile not found.");
            return;
        }

        var clientRequest = await db.ClientRequests
            .Include(r => r.ClientProfile)
                .ThenInclude(cp => cp.User)
            .FirstOrDefaultAsync(r => r.PublicId == req.PublicId, ct);

        if (clientRequest is null)
        {
            this.ThrowErrorWithCode(ErrorCodes.ClientRequestNotFound, "Client request not found.");
            return;
        }

        if (clientRequest.ProfessionalProfileId != professionalProfile.Id)
        {
            this.ThrowErrorWithCode(ErrorCodes.ClientRequestNotFound, "Client request not found.");
            return;
        }

        if (clientRequest.Status != ClientRequestStatus.Pending)
        {
            this.ThrowErrorWithCode(ErrorCodes.ClientRequestNotFound, "This request has already been processed.");
            return;
        }

        // Resolve the optional questionnaire up front so a Check-in form is refused before any write.
        // An unknown or foreign questionnaire id is ignored, as before.
        Questionnaire? questionnaire = null;

        if (req.QuestionnairePublicId.HasValue)
        {
            questionnaire = await db.Questionnaires
                .FirstOrDefaultAsync(q => q.PublicId == req.QuestionnairePublicId.Value
                    && q.ProfessionalId == userGuid, ct);

            if (questionnaire?.Type == QuestionnaireType.CheckIn)
            {
                this.ThrowErrorWithCode(ErrorCodes.CheckInFormNotSendable, "Check-in forms cannot be sent as a questionnaire.");
                return;
            }
        }

        // Determine plan-view capabilities from ALL of the caller's identity roles —
        // NOT a single tie-broken role. A professional can legitimately hold both
        // Trainer and Nutritionist roles simultaneously (#776); resolving to a single
        // mutually-exclusive role here meant CanViewTrainingPlans was force-reset to
        // false (and vice versa) every time such a dual-role professional accepted
        // ANY client request, even one that already had the other flag granted.
        var isTrainer = User.IsInRole(AppRoles.Trainer);
        var isNutritionist = User.IsInRole(AppRoles.Nutritionist);

        // ProfessionalRole remains a single display label (used elsewhere for UI text) —
        // prefer Nutritionist when both are held, matching prior behavior; the two
        // CanView* flags below are what actually gate plan access and are independent.
        var professionalRole = isNutritionist && !CoachRoleStatus.IsRemoved(professionalProfile, AppRoles.Nutritionist)
            ? UserRole.Nutritionist
            : UserRole.Trainer;

        // A caller-requested scope narrows the CanView* flags below the full set implied
        // by the caller's held roles — it must never widen them. Reject (400), don't
        // clamp: silently downgrading a request that asks for more than the caller holds
        // would mask a client bug and give no signal that the requested scope was denied.
        if (req.RequestedScope == LinkCapabilityScope.NutritionOnly && !isNutritionist)
        {
            this.ThrowErrorWithCode(
                ErrorCodes.RequestedScopeExceedsHeldRoles,
                "Requested scope exceeds the caller's held roles.");
            return;
        }

        if (req.RequestedScope == LinkCapabilityScope.TrainingOnly && !isTrainer)
        {
            this.ThrowErrorWithCode(
                ErrorCodes.RequestedScopeExceedsHeldRoles,
                "Requested scope exceeds the caller's held roles.");
            return;
        }

        // A removed coach role is read-only: it may not seed a link. Only active roles grant flags.
        var heldCoachRoles = new[] { AppRoles.Trainer, AppRoles.Nutritionist }.Where(User.IsInRole).ToList();

        if (!CoachRoleStatus.TryResolveLinkGrant(
                req.RequestedScope, heldCoachRoles, professionalProfile,
                out var canViewNutritionPlans, out var canViewTrainingPlans))
        {
            await this.SendErrorWithCodeAsync(
                ErrorCodes.CoachRoleRemoved,
                "This coach role was removed; the account is read-only for it.",
                StatusCodes.Status403Forbidden,
                ct);
            return;
        }

        // Serialize the slot check below against a concurrent link creation for the SAME client
        // (#1009). Under READ COMMITTED neither racer can see the other's uncommitted link, so
        // there is nothing on the link side to lock — the client's own already-committed profile
        // row is the one row both racers must touch, so that is what gets locked.
        // The client profile already exists (a request implies one), so no find-or-create
        // ordering concern here. Only the link-insert save below is inside this transaction —
        // the questionnaire-response, sibling-auto-cancel and chat-message saves that follow the
        // commit are unrelated writes and must stay outside it.
        await using var transaction = await db.BeginTransactionAsync(ct);
        await db.LockClientProfileAsync(clientRequest.ClientProfileId, ct);

        if (await this.RespondIfClientLimitReachedAsync(
                db, professionalProfile.Id, clientRequest.ClientProfileId, ct))
        {
            return;
        }

        // A client may hold at most one active coach per profession (#980). This
        // covers BOTH the reactivate branch and the insert branch below — a
        // reactivation re-occupying a slot another active professional holds is
        // rejected too, not just a brand-new link.
        if (await ProfessionSlotGuard.IsSlotTakenByAnotherProfessionalAsync(
                db.ClientProfessionalLinks, clientRequest.ClientProfileId, professionalProfile.Id,
                canViewNutritionPlans, canViewTrainingPlans, ct))
        {
            this.ThrowErrorWithCode(
                ErrorCodes.ProfessionAlreadyOccupied,
                "The client already has an active professional occupying this profession slot.");
            return;
        }

        // Update request status
        clientRequest.Status = ClientRequestStatus.Accepted;
        clientRequest.RespondedAt = DateTime.UtcNow;
        clientRequest.Statement = req.Statement;

        // Create or reactivate the client-professional link
        var link = await db.ClientProfessionalLinks
            .FirstOrDefaultAsync(l =>
                l.ClientProfileId == clientRequest.ClientProfileId &&
                l.ProfessionalProfileId == professionalProfile.Id, ct);

        if (link is not null)
        {
            link.ConnectionState = ConnectionState.Connected;
            link.ActivatedAtUtc = DateTime.UtcNow;
            link.ProfessionalRole = professionalRole;
            link.CanViewNutritionPlans = canViewNutritionPlans;
            link.CanViewTrainingPlans = canViewTrainingPlans;
            link.SkipOnboardingForm = req.SkipOnboardingForm;
        }
        else
        {
            link = new ClientProfessionalLink
            {
                ClientProfileId = clientRequest.ClientProfileId,
                ProfessionalProfileId = professionalProfile.Id,
                ProfessionalRole = professionalRole,
                ConnectionState = ConnectionState.Connected,
                ActivatedAtUtc = DateTime.UtcNow,
                CanViewNutritionPlans = canViewNutritionPlans,
                CanViewTrainingPlans = canViewTrainingPlans,
                SkipOnboardingForm = req.SkipOnboardingForm
            };
            db.ClientProfessionalLinks.Add(link);
        }

        clientRequest.SkipOnboardingForm = req.SkipOnboardingForm;

        // Handle optional questionnaire assignment
        if (questionnaire is not null)
        {
            link.QuestionnaireId = questionnaire.Id;
            clientRequest.QuestionnaireId = questionnaire.Id;
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        // The link is committed, so an unusable package or discount only means no terms (same rule as
        // an accepted invite). A discount on a request without a package is ignored.
        if (clientRequest.CoachPackageId is { } packageId)
        {
            // An unknown discount id maps to 0, which matches no row, so the applier skips and audits it.
            var discountId = req.DiscountPublicId is { } discountPublicId
                ? await db.CoachDiscounts
                    .AsNoTracking()
                    .Where(d => d.PublicId == discountPublicId)
                    .Select(d => d.Id)
                    .FirstOrDefaultAsync(ct)
                : (long?)null;

            var applied = await termsApplier.ApplyAsync(
                clientRequest.PublicId,
                professionalProfile.Id,
                packageId,
                discountId,
                clientRequest.ClientProfileId,
                userGuid,
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                DateTime.UtcNow,
                ct,
                nameof(ClientRequest));

            if (applied && discountId is not null)
            {
                clientRequest.CoachDiscountId = discountId;
                await db.SaveChangesAsync(ct);
            }
        }

        // Create QuestionnaireResponse after save so link.Id is generated
        if (questionnaire is not null)
        {
            var questionnaireResponse = new QuestionnaireResponse
            {
                QuestionnaireId = questionnaire.Id,
                ClientId = clientRequest.ClientProfile.UserId,
                ProfessionalId = userGuid,
                LinkId = link.Id,
                Status = QuestionnaireResponseStatus.Pending,
                Kind = QuestionnaireResponseKind.Onboarding,
                Scope = ClientLinkActivationResult.ScopeOf(canViewNutritionPlans, canViewTrainingPlans),
            };

            db.QuestionnaireResponses.Add(questionnaireResponse);
            await db.SaveChangesAsync(ct);
        }

        // Notify client of acceptance
        var profUser = await db.Users.FirstAsync(u => u.Id == userGuid, ct);
        var profName = $"{profUser.FirstName} {profUser.LastName}";

        await notificationService.CreateAsync(
            clientRequest.ClientProfile.UserId,
            NotificationType.InvitationAccepted,
            new Dictionary<string, string> { ["profName"] = profName },
            ct: ct);

        await notifier.NotifyAsync(clientRequest.ClientProfile.UserId, "clientrequestaccepted", new
        {
            RequestPublicId = clientRequest.PublicId,
            ProfessionalProfilePublicId = professionalProfile.PublicId
        }, ct);

        // Auto-cancel the client's other open requests to professionals of the same role
        var roleString = professionalRole == UserRole.Nutritionist ? AppRoles.Nutritionist : AppRoles.Trainer;

        await ClientRequestActivator.CancelOtherRequestsOfSameRoleAsync(
            db, userManager, notifier, notificationService, conversationSeedService, clientRequest, roleString, ct);

        // Questionnaire notification
        if (questionnaire is not null)
        {
            await notificationService.CreateAsync(
                clientRequest.ClientProfile.UserId,
                NotificationType.QuestionnaireAssigned,
                new Dictionary<string, string> { ["questionnaireTitle"] = questionnaire.Title },
                ct: ct);

            await notifier.NotifyAsync(clientRequest.ClientProfile.UserId, "questionnaireassigned", new
            {
                QuestionnairePublicId = questionnaire.PublicId,
                questionnaire.Title
            }, ct);
        }

        // Append the Accepted cooperation event — always written, even with a blank statement
        // (RULING 7). A non-blank statement becomes a plain Text row beneath it, authored by
        // the coach, via the same seam call.
        await conversationSeedService.AppendCooperationEventAsync(
            userGuid,
            clientRequest.ClientProfile.UserId,
            userGuid,
            ChatEventType.Accepted,
            clientRequest.PublicId,
            req.Statement,
            createConversationIfMissing: true,
            ct);

        logger.LogInformation(
            "Client request {RequestId} accepted by professional {ProfessionalId}",
            clientRequest.PublicId, professionalProfile.PublicId);

        await Send.NoContentAsync(ct);
    }
}
