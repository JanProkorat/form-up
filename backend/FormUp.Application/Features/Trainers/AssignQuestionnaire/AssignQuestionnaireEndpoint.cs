using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.Trainers.AssignQuestionnaire;

/// <summary>
/// Endpoint for a professional to assign a questionnaire to an existing linked client.
/// </summary>
[RemovedCoachRole(RemovedCoachRoleMode.Refuse)]
public class AssignQuestionnaireEndpoint(
    IApplicationDbContext db,
    IRealtimeNotifier notifier,
    INotificationService notificationService,
    ILogger<AssignQuestionnaireEndpoint> logger)
    : Endpoint<AssignQuestionnaireRequest>
{
    public override void Configure()
    {
        Post("/trainer/clients/{ClientPublicId}/assign-questionnaire");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist, AppRoles.Admin);
        Summary(s =>
        {
            s.Summary = "Assign a questionnaire to a client";
            s.Description = "Assigns a questionnaire to an existing linked client, creating a pending response.";
            s.Response(StatusCodes.Status204NoContent, "Questionnaire assigned");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest,
                "The profile, client, link or questionnaire is unknown, or a check-in form was sent as a questionnaire (CHECK_IN_FORM_NOT_SENDABLE)");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or unreadable caller claim");
            s.Response(StatusCodes.Status403Forbidden,
                "The subscription does not include questionnaires (SUBSCRIPTION_REQUIRED), or a removed coach role");
        });
    }

    public override async Task HandleAsync(AssignQuestionnaireRequest req, CancellationToken ct)
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

        if (await this.RespondIfFeatureNotEntitledAsync(
                db, professionalProfile.Id, entitlements => entitlements.CanSendQuestionnaires,
                "The current subscription does not include sending questionnaires.", ct))
        {
            return;
        }

        var clientProfile = await db.ClientProfiles
            .FirstOrDefaultAsync(cp => cp.PublicId == req.ClientPublicId, ct);

        if (clientProfile is null)
        {
            this.ThrowErrorWithCode(ErrorCodes.ClientNotFound, "Client not found.");
            return;
        }

        // Verify active link exists
        var link = await db.ClientProfessionalLinks
            .FirstOrDefaultAsync(l => l.ClientProfileId == clientProfile.Id
                                   && l.ProfessionalProfileId == professionalProfile.Id
                                   && l.ConnectionState != ConnectionState.Ended, ct);

        if (link is null)
        {
            this.ThrowErrorWithCode(ErrorCodes.NoClientRelationship, "No active relationship with this client.");
            return;
        }

        var questionnaire = await db.Questionnaires
            .FirstOrDefaultAsync(q => q.PublicId == req.QuestionnairePublicId
                && q.ProfessionalId == userGuid, ct);

        if (questionnaire is null)
        {
            ThrowError("Questionnaire not found.");
            return;
        }

        if (questionnaire.Type == QuestionnaireType.CheckIn)
        {
            this.ThrowErrorWithCode(ErrorCodes.CheckInFormNotSendable, "Check-in forms cannot be sent as a questionnaire.");
            return;
        }

        // Update link with questionnaire
        link.QuestionnaireId = questionnaire.Id;

        // Create questionnaire response
        var questionnaireResponse = new QuestionnaireResponse
        {
            QuestionnaireId = questionnaire.Id,
            ClientId = clientProfile.UserId,
            ProfessionalId = userGuid,
            LinkId = link.Id,
            Status = QuestionnaireResponseStatus.Pending,
            Kind = QuestionnaireResponseKind.Questionnaire,
        };

        db.QuestionnaireResponses.Add(questionnaireResponse);

        // Save entity changes (link update + questionnaire response) before creating notification
        await db.SaveChangesAsync(ct);

        await notificationService.CreateAsync(
            clientProfile.UserId,
            NotificationType.QuestionnaireAssigned,
            new Dictionary<string, string> { ["questionnaireTitle"] = questionnaire.Title },
            ct: ct);

        await notifier.NotifyAsync(clientProfile.UserId, "questionnaireassigned", new
        {
            QuestionnairePublicId = questionnaire.PublicId,
            questionnaire.Title
        }, ct);

        logger.LogInformation(
            "Questionnaire {QuestionnaireId} assigned to client {ClientId} by professional {ProfessionalId}",
            questionnaire.PublicId, clientProfile.PublicId, professionalProfile.PublicId);

        await Send.NoContentAsync(ct);
    }
}
