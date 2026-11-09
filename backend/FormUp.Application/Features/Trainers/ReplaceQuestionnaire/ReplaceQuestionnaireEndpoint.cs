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

namespace FormUp.Application.Features.Trainers.ReplaceQuestionnaire;

public class ReplaceQuestionnaireRequest
{
    public Guid ClientPublicId { get; set; }
    public Guid QuestionnairePublicId { get; set; }
}

[RemovedCoachRole(RemovedCoachRoleMode.Refuse)]
public class ReplaceQuestionnaireEndpoint(
    IApplicationDbContext db,
    IRealtimeNotifier notifier,
    INotificationService notificationService,
    ILogger<ReplaceQuestionnaireEndpoint> logger)
    : Endpoint<ReplaceQuestionnaireRequest>
{
    public override void Configure()
    {
        Post("/trainer/clients/{ClientPublicId}/replace-questionnaire");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Replace a pending questionnaire";
            s.Description = "Cancels the current pending questionnaire and assigns a new one.";
            s.Response(StatusCodes.Status204NoContent, "Questionnaire replaced");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest,
                "No pending questionnaire for the client, the questionnaire is unknown, or a check-in form was sent as a questionnaire (CHECK_IN_FORM_NOT_SENDABLE)");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or unreadable caller claim");
            s.Response(StatusCodes.Status403Forbidden,
                "The subscription does not include questionnaires (SUBSCRIPTION_REQUIRED), or a removed coach role");
            s.Response(StatusCodes.Status404NotFound, "Professional profile or client not found, or the caller has no live link to the client");
        });
    }

    public override async Task HandleAsync(ReplaceQuestionnaireRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);
        if (userId is null) { await Send.UnauthorizedAsync(ct); return; }

        var userGuid = Guid.Parse(userId);

        var professionalProfile = await db.ProfessionalProfiles
            .FirstOrDefaultAsync(pp => pp.UserId == userGuid, ct);

        if (professionalProfile is null)
        {
            await Send.NotFoundAsync(ct);
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
            await Send.NotFoundAsync(ct);
            return;
        }

        var link = await db.ClientProfessionalLinks
            .FirstOrDefaultAsync(l =>
                l.ClientProfileId == clientProfile.Id &&
                l.ProfessionalProfileId == professionalProfile.Id &&
                l.ConnectionState != ConnectionState.Ended, ct);

        if (link is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        // Cancel old response
        var oldResponse = await db.QuestionnaireResponses
            .Where(r => r.ClientId == clientProfile.UserId
                     && r.ProfessionalId == userGuid
                     && (r.Status == QuestionnaireResponseStatus.Pending || r.Status == QuestionnaireResponseStatus.InProgress))
            .OrderByDescending(r => r.DateCreated)
            .FirstOrDefaultAsync(ct);

        if (oldResponse is null)
        {
            ThrowError("No pending questionnaire found for this client.");
            return;
        }

        // Find new questionnaire
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

        oldResponse.Status = QuestionnaireResponseStatus.Cancelled;

        // Update link with new questionnaire
        link.QuestionnaireId = questionnaire.Id;

        // Create new response
        var newResponse = new QuestionnaireResponse
        {
            QuestionnaireId = questionnaire.Id,
            ClientId = clientProfile.UserId,
            ProfessionalId = userGuid,
            LinkId = link.Id,
            Status = QuestionnaireResponseStatus.Pending,
            Kind = QuestionnaireResponseKind.Questionnaire,
        };
        db.QuestionnaireResponses.Add(newResponse);

        await db.SaveChangesAsync(ct);

        var profUser = await db.Users.FirstAsync(u => u.Id == userGuid, ct);
        var profName = $"{profUser.FirstName} {profUser.LastName}";

        // Notify client: old cancelled, new assigned
        await notifier.NotifyAsync(clientProfile.UserId, "questionnairecancelled", new
        {
            QuestionnaireTitle = oldResponse.Questionnaire?.Title ?? "",
        }, ct);

        await notificationService.CreateAsync(
            clientProfile.UserId,
            NotificationType.QuestionnaireAssigned,
            new Dictionary<string, string> { ["questionnaireTitle"] = questionnaire.Title },
            ct: ct);

        await notifier.NotifyAsync(clientProfile.UserId, "questionnaireassigned", new
        {
            QuestionnairePublicId = questionnaire.PublicId,
            questionnaire.Title,
        }, ct);

        logger.LogInformation(
            "Questionnaire replaced for client {ClientId}: old {OldId} cancelled, new {NewId} assigned by {ProfessionalId}",
            clientProfile.PublicId, oldResponse.PublicId, newResponse.PublicId, professionalProfile.PublicId);

        await Send.NoContentAsync(ct);
    }
}
