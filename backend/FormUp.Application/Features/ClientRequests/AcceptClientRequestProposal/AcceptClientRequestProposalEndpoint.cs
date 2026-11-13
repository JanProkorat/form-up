using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.ClientRequests.AcceptClientRequestProposal;

/// <summary>
/// A client accepts the package a coach proposed on their request. The link starts with the coach's role scope,
/// the proposed form is assigned and the proposed terms apply.
/// </summary>
public class AcceptClientRequestProposalEndpoint(
    IApplicationDbContext db,
    UserManager<ApplicationUser> userManager,
    INotificationService notificationService,
    IRealtimeNotifier notifier,
    IConversationSeedService conversationSeedService,
    TimeProvider timeProvider,
    InvitePackageTermsApplier termsApplier,
    ILogger<AcceptClientRequestProposalEndpoint> logger)
    : Endpoint<AcceptClientRequestProposalRequest>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/client/requests/{PublicId}/accept-proposal");
        Roles(AppRoles.Client);
        Summary(s =>
        {
            s.Summary = "Accept a package proposal";
            s.Description = "Accepts the coach's package proposal: the client-professional link starts, the proposed form is assigned " +
                "and the proposed terms apply. A package or discount that is no longer usable means the link starts without terms.";
            s.Response(StatusCodes.Status204NoContent, "Proposal accepted");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest,
                "The client already has an active professional in this profession (PROFESSION_ALREADY_OCCUPIED)");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or unreadable caller claim");
            s.Response(StatusCodes.Status403Forbidden, "The coach role was removed (COACH_ROLE_REMOVED), or the coach is at the active-client limit (CLIENT_LIMIT_REACHED)");
            s.Response(StatusCodes.Status404NotFound, "The request is unknown or not the caller's (CLIENT_REQUEST_NOT_FOUND)");
            s.Response(StatusCodes.Status409Conflict, "The request has no open proposal (CLIENT_REQUEST_INVALID_STATE)");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(AcceptClientRequestProposalRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);

        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var userGuid = Guid.Parse(userId);

        var clientRequest = await db.ClientRequests
            .Include(r => r.ClientProfile)
                .ThenInclude(cp => cp.User)
            .Include(r => r.ProfessionalProfile)
            .FirstOrDefaultAsync(r => r.PublicId == req.PublicId && r.ClientProfile.UserId == userGuid, ct);

        if (clientRequest is null)
        {
            await this.SendProblemAsync(StatusCodes.Status404NotFound, ErrorCodes.ClientRequestNotFound, "Client request not found.", ct);
            return;
        }

        if (clientRequest.Status != ClientRequestStatus.PackageProposed)
        {
            await SendInvalidStateAsync(ct);
            return;
        }

        var professional = clientRequest.ProfessionalProfile;

        // The same row lock a withdrawal takes: serializes against a concurrent link creation for this client
        // (#1009) and against the coach withdrawing the proposal at the same moment.
        await using var transaction = await db.BeginTransactionAsync(ct);
        await db.LockClientProfileAsync(clientRequest.ClientProfileId, ct);

        var currentStatus = await db.ClientRequests
            .AsNoTracking()
            .Where(r => r.Id == clientRequest.Id)
            .Select(r => r.Status)
            .FirstAsync(ct);

        if (currentStatus != ClientRequestStatus.PackageProposed)
        {
            await SendInvalidStateAsync(ct);
            return;
        }

        if (await this.RespondIfClientLimitReachedAsync(db, professional.Id, clientRequest.ClientProfileId, ct))
        {
            return;
        }

        var activation = await ClientLinkActivator.ActivateAsync(
            db, userManager, clientRequest.ClientProfileId, professional,
            requestedScope: null, clientRequest.QuestionnaireId,
            clientRequest.SkipOnboardingForm, ct);

        if (activation.Outcome == ClientLinkActivationOutcome.CoachRoleRemoved)
        {
            await this.SendErrorWithCodeAsync(
                ErrorCodes.CoachRoleRemoved,
                "This coach role was removed; the account is read-only for it.",
                StatusCodes.Status403Forbidden,
                ct);
            return;
        }

        if (activation.Outcome == ClientLinkActivationOutcome.SlotTaken)
        {
            this.ThrowErrorWithCode(
                ErrorCodes.ProfessionAlreadyOccupied,
                "The client already has an active professional occupying this profession slot.");
            return;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        clientRequest.Status = ClientRequestStatus.Accepted;
        clientRequest.RespondedAt = now;

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        // The proposed form is assigned to the link that was started or widened, for the added scope only.
        if ((activation.Started || activation.Extended) && clientRequest.QuestionnaireId is { } questionnaireId)
        {
            db.QuestionnaireResponses.Add(new QuestionnaireResponse
            {
                QuestionnaireId = questionnaireId,
                ClientId = userGuid,
                ProfessionalId = professional.UserId,
                LinkId = activation.Link!.Id,
                Status = QuestionnaireResponseStatus.Pending,
                Scope = activation.GrantedScope,
            });
            await db.SaveChangesAsync(ct);
        }

        // Only a newly started link takes the proposed terms. A package or discount that no longer qualifies is
        // skipped and audited against the request, never an error.
        if (activation.Started && clientRequest.ProposedPackageId is { } packageId)
        {
            await termsApplier.ApplyAsync(
                clientRequest.PublicId,
                clientRequest.ProfessionalProfileId,
                packageId,
                clientRequest.ProposedDiscountId,
                clientRequest.ClientProfileId,
                userGuid,
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                now,
                ct,
                nameof(ClientRequest));
        }

        var clientName = $"{clientRequest.ClientProfile.User.FirstName} {clientRequest.ClientProfile.User.LastName}";

        await notificationService.CreateAsync(
            professional.UserId,
            NotificationType.ClientRequestProposalAccepted,
            new Dictionary<string, string>
            {
                ["clientName"] = clientName,
                ["requestId"] = clientRequest.PublicId.ToString(),
            },
            ct: ct);

        await notifier.NotifyAsync(professional.UserId, "clientrequestproposalaccepted", new
        {
            RequestPublicId = clientRequest.PublicId,
            ClientPublicId = clientRequest.ClientProfile.PublicId,
            ClientName = clientName,
        }, ct);

        // The client's other open requests to professionals of the same role end with this one.
        var linkRole = activation.Link?.ProfessionalRole
            ?? await db.ClientProfessionalLinks
                .AsNoTracking()
                .Where(l => l.ClientProfileId == clientRequest.ClientProfileId && l.ProfessionalProfileId == professional.Id)
                .Select(l => l.ProfessionalRole)
                .FirstAsync(ct);
        var roleName = linkRole == UserRole.Nutritionist ? AppRoles.Nutritionist : AppRoles.Trainer;

        await ClientRequestActivator.CancelOtherRequestsOfSameRoleAsync(
            db, userManager, notifier, notificationService, conversationSeedService, clientRequest, roleName, ct);

        await conversationSeedService.AppendCooperationEventAsync(
            professional.UserId,
            userGuid,
            userGuid,
            ChatEventType.Accepted,
            clientRequest.PublicId,
            messageText: null,
            createConversationIfMissing: true,
            ct);

        logger.LogInformation(
            "Package proposal on client request {RequestId} accepted by client {ClientId}",
            clientRequest.PublicId, clientRequest.ClientProfile.PublicId);

        await Send.NoContentAsync(ct);
    }

    private Task SendInvalidStateAsync(CancellationToken ct) =>
        this.SendProblemAsync(
            StatusCodes.Status409Conflict,
            ErrorCodes.ClientRequestInvalidState,
            "The request has no open package proposal.",
            ct);
}
