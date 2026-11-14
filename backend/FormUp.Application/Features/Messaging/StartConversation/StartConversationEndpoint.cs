using System.Security.Claims;
using FastEndpoints;
using FluentValidation;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.Messaging.Shared;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.Messaging.StartConversation;

/// <summary>
/// Gets or creates a conversation between the professional and a client.
/// Accepts a participant profile PublicId (ClientProfile or ProfessionalProfile)
/// and resolves it to the correct user.
/// </summary>
/// <remarks>
/// A first message requires a currently live link between the caller and the participant — an
/// unknown participant profile 404s, and a resolvable participant with no live link 404s with
/// <see cref="ErrorCodes.NotLinkedToClient"/>, unless the caller is a Client with a
/// <see cref="ClientRequestStatus.Pending"/> <see cref="ClientRequest"/> to that professional —
/// mobile sends the join request and the intro chat message back-to-back
/// (<c>useCollaboration.ts</c>), before the professional has accepted. The professional-caller
/// path is unchanged: a professional never gets this exception. A conversation that already
/// exists for the pair is always returned, regardless of link liveness — reopening a thread from
/// a former collaboration must not be blocked by the same check that gates starting a new one.
/// Capability flags (<c>CanViewNutritionPlans</c>/<c>CanViewTrainingPlans</c>) are deliberately
/// not checked — messaging ignores them, same as <c>BroadcastMessageEndpoint</c>.
/// </remarks>
/// <param name="db">Relational database context.</param>
/// <param name="linkAuthorizationService">Resolves whether the caller has a live link to the participant.</param>
public class StartConversationEndpoint(
    IApplicationDbContext db,
    IClientLinkAuthorizationService linkAuthorizationService)
    : Endpoint<StartConversationRequest, ConversationDto>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/conversations");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist, AppRoles.Client);
        Summary(s =>
        {
            s.Summary = "Start or get a conversation";
            s.Description = "Gets an existing conversation with the specified participant. Creates " +
                             "one if it doesn't exist and the caller currently has a live link with " +
                             "the participant. Pass the participant's profile PublicId.";
            s.Responses[StatusCodes.Status200OK] = "The existing or newly created conversation.";
            s.Responses[StatusCodes.Status403Forbidden] = "A coach whose subscription does not include " +
                                                            "messaging starts a new conversation (SUBSCRIPTION_REQUIRED).";
            s.Responses[StatusCodes.Status404NotFound] = "The participant id does not resolve to a " +
                                                           "profile, or no conversation exists yet and " +
                                                           "the caller has no live link to the participant " +
                                                           "(a Client caller with a pending join request " +
                                                           "to that professional is exempt).";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(StartConversationRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);
        if (userId is null) { await Send.UnauthorizedAsync(ct); return; }

        var userGuid = Guid.Parse(userId);
        // Determine caller role: prefer professional role (Trainer/Nutritionist) over Client
        var isProfessional = User.IsInRole(AppRoles.Trainer) || User.IsInRole(AppRoles.Nutritionist);

        Guid professionalUserId;
        Guid clientUserId;
        ApplicationUser otherUser;
        // For professionals the avatar falls back from profile-level to user-level;
        // for clients only the user-level avatar exists (ClientProfile has no AvatarBlobUrl).
        string? participantAvatarBlobUrl;
        Guid? participantClientPublicId;

        if (isProfessional)
        {
            // Professional is starting conversation — participantId is a ClientProfile.PublicId
            var client = await db.ClientProfiles
                .AsNoTracking()
                .Include(c => c.User)
                .FirstOrDefaultAsync(c => c.PublicId == req.ParticipantId, ct);

            if (client is null) { await Send.NotFoundAsync(ct); return; }

            professionalUserId = userGuid;
            clientUserId = client.UserId;
            otherUser = client.User;
            // ClientProfile has no dedicated AvatarBlobUrl; use the user-level avatar.
            participantAvatarBlobUrl = client.User.AvatarBlobUrl;
            participantClientPublicId = client.PublicId;
        }
        else
        {
            // Client is starting conversation — participantId is a ProfessionalProfile.PublicId
            var prof = await db.ProfessionalProfiles
                .AsNoTracking()
                .Include(p => p.User)
                .FirstOrDefaultAsync(p => p.PublicId == req.ParticipantId, ct);

            if (prof is null) { await Send.NotFoundAsync(ct); return; }

            professionalUserId = prof.UserId;
            clientUserId = userGuid;
            otherUser = prof.User;
            // Prefer the professional-profile avatar; fall back to the user-level avatar.
            participantAvatarBlobUrl = prof.AvatarBlobUrl ?? prof.User.AvatarBlobUrl;
            participantClientPublicId = null;
        }

        // A conversation that already exists is always reopened, regardless of link liveness —
        // see the class remarks. Only starting a NEW conversation requires a live link.
        var existing = await db.Conversations
            .Include(c => c.Messages)
            .FirstOrDefaultAsync(c =>
                c.ProfessionalUserId == professionalUserId && c.ClientUserId == clientUserId, ct);

        if (existing is not null)
        {
            var isSendLocked = isProfessional &&
                await ProfessionalSendGuard.EvaluateAsync(db, professionalUserId, clientUserId, ct)
                    == ProfessionalSendAccess.Locked;

            await Send.OkAsync(
                BuildResponse(existing, otherUser, participantAvatarBlobUrl, participantClientPublicId, userGuid, isSendLocked), ct);
            return;
        }

        // No conversation yet — a first message requires a currently live link between the
        // caller and the participant. Capability flags are not checked (see class remarks).
        var capabilities = await linkAuthorizationService.GetCapabilitiesByClientUserIdAsync(
            professionalUserId, clientUserId, ct);

        if (capabilities is null)
        {
            // A Client caller with no live link may still start the conversation if they have a
            // pending join request to this professional (see class remarks). The professional
            // caller path never satisfies this — isProfessional short-circuits the query.
            var hasPendingJoinRequest = !isProfessional && await db.ClientRequests
                .AsNoTracking()
                .AnyAsync(r =>
                    r.ClientProfile.UserId == clientUserId &&
                    r.ProfessionalProfile.UserId == professionalUserId &&
                    (r.Status == ClientRequestStatus.Pending || r.Status == ClientRequestStatus.PackageProposed), ct);

            if (!hasPendingJoinRequest)
            {
                await this.SendProblemAsync(
                    404, ErrorCodes.NotLinkedToClient, "The caller has no active link with this participant.", ct);
                return;
            }
        }

        // Only a coach starting a new thread needs the messaging feature; a client always may.
        if (isProfessional
            && await this.RespondIfFeatureNotEntitledAsync(
                db, userGuid, entitlements => entitlements.CanMessage,
                "The current subscription does not include messaging.", ct))
        {
            return;
        }

        var conversation = new Conversation
        {
            ProfessionalUserId = professionalUserId,
            ClientUserId = clientUserId,
        };

        db.Conversations.Add(conversation);
        await db.SaveChangesAsync(ct);

        await Send.OkAsync(
            BuildResponse(conversation, otherUser, participantAvatarBlobUrl, participantClientPublicId, userGuid, isSendLocked: false), ct);
    }

    private static ConversationDto BuildResponse(
        Conversation conversation,
        ApplicationUser otherUser,
        string? participantAvatarBlobUrl,
        Guid? participantClientPublicId,
        Guid callerUserId,
        bool isSendLocked) =>
        new()
        {
            Id = conversation.PublicId,
            Participant = new ParticipantDto
            {
                Id = otherUser.Id,
                Name = otherUser.FirstName + " " + otherUser.LastName,
                Initials = ParticipantInitials.Compute(otherUser.FirstName, otherUser.LastName, otherUser.Email),
                Online = false,
                AvatarBlobUrl = participantAvatarBlobUrl,
                ClientPublicId = participantClientPublicId,
            },
            LastMessage = conversation.LastMessageText ?? "",
            LastMessageAt = conversation.LastMessageAt ?? conversation.DateCreated,
            LastMessageIsOwn = conversation.LastMessageSenderId == callerUserId,
            UnreadCount = conversation.Messages.Count(m => !m.IsRead && m.SenderUserId != callerUserId && m.Kind != ChatMessageKind.Event),
            LastMessageHasImage = conversation.LastMessageHasImage,
            LastMessageEventType = conversation.LastMessageEventType,
            IsSendLocked = isSendLocked,
        };
}

public class StartConversationRequest
{
    /// <summary>
    /// The PublicId of the participant's profile (ClientProfile.PublicId or ProfessionalProfile.PublicId).
    /// </summary>
    public Guid ParticipantId { get; set; }
}

public class StartConversationValidator : FastEndpoints.Validator<StartConversationRequest>
{
    public StartConversationValidator()
    {
        RuleFor(x => x.ParticipantId).NotEmpty();
    }
}
