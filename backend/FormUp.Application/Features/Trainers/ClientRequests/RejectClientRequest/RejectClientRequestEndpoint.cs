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

namespace FormUp.Application.Features.Trainers.ClientRequests.RejectClientRequest;

/// <summary>
/// Endpoint for a professional to reject a pending client request.
/// </summary>
[RemovedCoachRole(RemovedCoachRoleMode.Refuse)]
public class RejectClientRequestEndpoint(
    IApplicationDbContext db,
    IRealtimeNotifier notifier,
    INotificationService notificationService,
    IConversationSeedService conversationSeedService,
    ILogger<RejectClientRequestEndpoint> logger)
    : Endpoint<RejectClientRequestRequest>
{
    public override void Configure()
    {
        Post("/trainer/client-requests/{PublicId}/reject");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist, AppRoles.Admin);
        Summary(s =>
        {
            s.Summary = "Reject a client request";
            s.Description = "Rejects a pending client request.";
        });
    }

    public override async Task HandleAsync(RejectClientRequestRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);

        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var userGuid = Guid.Parse(userId);
        var publicId = req.PublicId;

        var professionalProfile = await db.ProfessionalProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(pp => pp.UserId == userGuid, ct);

        if (professionalProfile is null)
        {
            this.ThrowErrorWithCode(ErrorCodes.TrainerProfileMissing, "Professional profile not found.");
            return;
        }

        var clientRequest = await db.ClientRequests
            .Include(r => r.ClientProfile)
            .FirstOrDefaultAsync(r => r.PublicId == publicId, ct);

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

        clientRequest.Status = ClientRequestStatus.Rejected;
        clientRequest.RespondedAt = DateTime.UtcNow;
        clientRequest.Statement = req.Statement;

        var profUser = await db.Users.FirstAsync(u => u.Id == userGuid, ct);
        var profName = $"{profUser.FirstName} {profUser.LastName}";

        // Save entity changes (status update) before creating notification
        await db.SaveChangesAsync(ct);

        await notificationService.CreateAsync(
            clientRequest.ClientProfile.UserId,
            NotificationType.InvitationDeclined,
            new Dictionary<string, string> { ["profName"] = profName },
            ct: ct);

        await notifier.NotifyAsync(clientRequest.ClientProfile.UserId, "clientrequestrejected", new
        {
            RequestPublicId = clientRequest.PublicId
        }, ct);

        // Append the Declined cooperation event — always written, even with a blank statement
        // (RULING 7). A non-blank statement becomes a plain Text row beneath it, authored by
        // the coach, via the same seam call.
        await conversationSeedService.AppendCooperationEventAsync(
            userGuid,
            clientRequest.ClientProfile.UserId,
            userGuid,
            ChatEventType.Declined,
            clientRequest.PublicId,
            req.Statement,
            createConversationIfMissing: true,
            ct,
            pushCompanionText: true);

        logger.LogInformation(
            "Client request {RequestId} rejected by professional {ProfessionalId}",
            clientRequest.PublicId, professionalProfile.PublicId);

        await Send.NoContentAsync(ct);
    }
}
