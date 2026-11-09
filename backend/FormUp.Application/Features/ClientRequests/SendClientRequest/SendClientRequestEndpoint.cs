using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.ClientRequests.SendClientRequest;

/// <summary>
/// Endpoint for a client to send a request to work with a professional.
/// </summary>
public class SendClientRequestEndpoint(
    IApplicationDbContext db,
    IRealtimeNotifier notifier,
    INotificationService notificationService,
    INotificationEmailSender notificationEmails,
    IConversationSeedService conversationSeedService,
    EntitlementService entitlementService,
    ILogger<SendClientRequestEndpoint> logger)
    : Endpoint<SendClientRequestRequest, SendClientRequestResponse>
{
    public override void Configure()
    {
        Post("/client/requests");
        Roles(AppRoles.Client);
        Summary(s =>
        {
            s.Summary = "Send a client request";
            s.Description = "Sends a request from a client to a professional to establish a working relationship.";
            s.Response<SendClientRequestResponse>(StatusCodes.Status201Created, "Request sent");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest,
                "Invalid request, or the package is not available (REQUEST_PACKAGE_UNAVAILABLE)");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or unreadable caller claim");
        });
    }

    public override async Task HandleAsync(SendClientRequestRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);

        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var userGuid = Guid.Parse(userId);

        var clientProfile = await db.ClientProfiles
            .FirstOrDefaultAsync(cp => cp.UserId == userGuid, ct);

        if (clientProfile is null)
        {
            this.ThrowErrorWithCode(ErrorCodes.ClientNotFound, "Client profile not found.");
            return;
        }

        var professionalProfile = await db.ProfessionalProfiles
            .Include(pp => pp.User)
            .FirstOrDefaultAsync(pp => pp.PublicId == req.ProfessionalPublicId, ct);

        if (professionalProfile is null)
        {
            this.ThrowErrorWithCode(ErrorCodes.ProfessionalNotFound, "Professional not found.");
            return;
        }

        if (!professionalProfile.AcceptNewClients)
        {
            this.ThrowErrorWithCode(ErrorCodes.ProfessionalNotAccepting, "Professional is not accepting new clients.");
            return;
        }

        // Unknown, foreign, archived, hidden and not-payable packages all answer alike, so a client
        // cannot probe another coach's packages.
        CoachPackage? package = null;

        if (req.PackagePublicId is { } packagePublicId)
        {
            package = await CoachPackagePricing.FindProfileVisiblePackageAsync(
                db, entitlementService, professionalProfile.Id, packagePublicId, ct);

            if (package is null)
            {
                this.ThrowErrorWithCode(ErrorCodes.RequestPackageUnavailable, "The package is not available.");
                return;
            }
        }

        // Check for existing active link
        var existingLink = await db.ClientProfessionalLinks
            .AnyAsync(l => l.ClientProfileId == clientProfile.Id
                        && l.ProfessionalProfileId == professionalProfile.Id
                        && l.ConnectionState != ConnectionState.Ended, ct);

        if (existingLink)
        {
            this.ThrowErrorWithCode(ErrorCodes.AlreadyLinked, "You are already linked with this professional.");
            return;
        }

        // Check for existing pending request
        var existingRequest = await db.ClientRequests
            .AnyAsync(r => r.ClientProfileId == clientProfile.Id
                        && r.ProfessionalProfileId == professionalProfile.Id
                        && (r.Status == ClientRequestStatus.Pending || r.Status == ClientRequestStatus.PackageProposed), ct);

        if (existingRequest)
        {
            this.ThrowErrorWithCode(ErrorCodes.RequestAlreadyPending, "A pending request already exists for this professional.");
            return;
        }

        var clientRequest = new ClientRequest
        {
            ClientProfileId = clientProfile.Id,
            ProfessionalProfileId = professionalProfile.Id,
            Message = req.Message,
            ShareHistory = req.ShareHistory,
            CoachPackageId = package?.Id,
            Status = ClientRequestStatus.Pending,
            SentAt = DateTime.UtcNow
        };

        db.ClientRequests.Add(clientRequest);

        // Persist explicitly rather than relying on NotificationService.CreateAsync's
        // downstream SaveChangesAsync call on this same scoped context — the request
        // must still be durably saved if the notification call is ever removed,
        // reordered, or moved to a different context/scope (#663).
        await db.SaveChangesAsync(ct);

        // Get-or-create the conversation and write the Requested event (plus the request
        // message beneath, when non-blank) server-side — atomically, not as a best-effort
        // client-side follow-up (R2=A).
        await conversationSeedService.AppendCooperationEventAsync(
            professionalProfile.UserId,
            userGuid,
            userGuid,
            ChatEventType.Requested,
            clientRequest.PublicId,
            req.Message,
            createConversationIfMissing: true,
            ct);

        // Get client user for notification
        var clientUser = await db.Users.FirstAsync(u => u.Id == userGuid, ct);

        var clientName = $"{clientUser.FirstName} {clientUser.LastName}";

        await notificationService.CreateAsync(
            professionalProfile.UserId,
            NotificationType.ClientRequestReceived,
            new Dictionary<string, string> { ["clientName"] = clientName },
            ct: ct);

        await notifier.NotifyAsync(professionalProfile.UserId, "clientrequestreceived", new
        {
            RequestPublicId = clientRequest.PublicId,
            ClientFirstName = clientUser.FirstName,
            ClientLastName = clientUser.LastName,
            clientRequest.Message
        }, ct);

        var emailValues = new Dictionary<string, string>
        {
            [NotificationEmailWorkItem.ClientNameKey] = clientName.Trim(),
        };

        if (!string.IsNullOrWhiteSpace(req.Message))
        {
            emailValues[NotificationEmailWorkItem.MessageKey] = req.Message;
        }

        if (package is not null)
        {
            emailValues[NotificationEmailWorkItem.PackageNameKey] = package.Name;
        }

        await notificationEmails.SendAsync(professionalProfile.UserId, NotificationEvent.JoinRequest, emailValues, ct);

        logger.LogInformation(
            "Client request sent from {ClientId} to professional {ProfessionalId}",
            clientProfile.PublicId, professionalProfile.PublicId);

        await Send.CreatedAtAsync<SendClientRequestEndpoint>(null, new SendClientRequestResponse
        {
            PublicId = clientRequest.PublicId,
            ProfessionalPublicId = professionalProfile.PublicId,
            ProfessionalName = $"{professionalProfile.User.FirstName} {professionalProfile.User.LastName}",
            Message = clientRequest.Message,
            Status = clientRequest.Status,
            SentAt = clientRequest.SentAt
        }, cancellation: ct);
    }
}
