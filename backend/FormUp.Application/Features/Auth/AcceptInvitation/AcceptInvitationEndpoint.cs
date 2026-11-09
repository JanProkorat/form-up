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

namespace FormUp.Application.Features.Auth.AcceptInvitation;

/// <summary>
/// Endpoint for a client to accept a professional's invitation using a one-time token.
/// Creates or reuses the client's profile and establishes a client-professional link.
/// </summary>
/// <param name="db">Database context.</param>
/// <param name="userManager">ASP.NET Identity user manager.</param>
/// <param name="audit">Audit logging service.</param>
/// <param name="notificationService">Creates the in-app notification for the professional (#770).</param>
/// <param name="notifier">Broadcasts the "inviteaccepted" SignalR event to the professional (#770).</param>
/// <param name="conversationSeedService">
/// Seeds the invite's personal message as the conversation's first message so it
/// surfaces on the client's Messages screen (#768).
/// </param>
/// <param name="timeProvider">Clock.</param>
/// <param name="termsApplier">Gives the client the package the invite carried, for a created or reactivated link.</param>
public class AcceptInvitationEndpoint(
    IApplicationDbContext db,
    UserManager<ApplicationUser> userManager,
    IAuditService audit,
    INotificationService notificationService,
    IRealtimeNotifier notifier,
    IConversationSeedService conversationSeedService,
    TimeProvider timeProvider,
    InvitePackageTermsApplier termsApplier)
    : Endpoint<AcceptInvitationRequest, AcceptInvitationResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/auth/invite/accept");
        Roles(AppRoles.Client);
        Summary(s =>
        {
            s.Summary = "Accept a trainer invitation";
            s.Description = "Accepts a one-time invitation token from a trainer, creating a client-professional relationship.";
            s.Response<AcceptInvitationResponse>(StatusCodes.Status200OK, "Invitation accepted");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest, "Invalid, used or expired token, or the profession slot is already taken (PROFESSION_ALREADY_OCCUPIED)");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or unreadable caller claim");
            s.Response(StatusCodes.Status403Forbidden,
                "The coach role was removed, or the coach is at the active-client limit (CLIENT_LIMIT_REACHED)");
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(AcceptInvitationRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);

        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var userGuid = Guid.Parse(userId);

        // Find the invitation token (must not be used and not expired)
        var invitation = await db.InvitationTokens
            .Include(i => i.ProfessionalProfile)
            .FirstOrDefaultAsync(i => i.Token == req.Token, ct);

        if (invitation is null)
        {
            ThrowError("Invalid invitation token.");
            return;
        }

        if (invitation.IsUsed)
        {
            ThrowError("This invitation has already been used.");
            return;
        }

        if (invitation.ExpiresAt < DateTime.UtcNow)
        {
            ThrowError("This invitation has expired.");
            return;
        }

        // Find or create the client profile
        var clientProfile = await db.ClientProfiles
            .FirstOrDefaultAsync(cp => cp.UserId == userGuid, ct);

        if (clientProfile is null)
        {
            clientProfile = new ClientProfile
            {
                UserId = userGuid
            };
            db.ClientProfiles.Add(clientProfile);
            await db.SaveChangesAsync(ct);
        }

        // Serialize the slot check below against a concurrent link creation for the SAME client
        // (#1009). Under READ COMMITTED neither racer can see the other's uncommitted link, so
        // there is nothing on the link side to lock — the client's own already-committed profile
        // row is the one row both racers must touch, so that is what gets locked.
        // Taken AFTER the find-or-create save above, or the row would not exist yet.
        await using var transaction = await db.BeginTransactionAsync(ct);
        await db.LockClientProfileAsync(clientProfile.Id, ct);

        if (await this.RespondIfClientLimitReachedAsync(db, invitation.ProfessionalProfileId, clientProfile.Id, ct))
        {
            return;
        }

        // Find the matching live pending invite to get questionnaire assignment. An expired
        // row may coexist with a re-invite (#1541), so ignore it and prefer the newest.
        var validSince = PendingInviteStatusResolver.ValidSince(timeProvider.GetUtcNow().UtcDateTime);
        var pendingInvite = await db.PendingInvites
            .Where(pi => pi.ProfessionalProfileId == invitation.ProfessionalProfileId
                && pi.Email == invitation.Email
                && pi.Status == PendingInviteStatus.Pending
                && pi.SentAt >= validSince)
            .OrderByDescending(pi => pi.SentAt)
            .FirstOrDefaultAsync(ct);

        // Honor the scope the professional selected at invite-creation time (already
        // validated as a subset of their held roles there) rather than re-deriving
        // from global roles here. Falls back to the full held-role set when the
        // invite carries no explicit scope — invites created before this field
        // existed, or where the professional didn't opt in.
        var activation = await ClientLinkActivator.ActivateAsync(
            db, userManager, clientProfile.Id, invitation.ProfessionalProfile,
            invitation.RequestedScope ?? pendingInvite?.RequestedScope,
            pendingInvite?.QuestionnaireId,
            pendingInvite?.SkipOnboardingForm ?? false, ct);

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

        // True for a newly created or a reactivated link; false when the client was already linked.
        var linkStarted = activation.Started;

        // A widened link is a real accept too: the invite is consumed and the chat events follow.
        var inviteAccepted = linkStarted || activation.Extended;

        if (inviteAccepted && pendingInvite is not null)
        {
            pendingInvite.Status = PendingInviteStatus.Accepted;
        }

        // Mark invitation as used
        invitation.IsUsed = true;

        // Auto-confirm email since user clicked the invitation link
        var currentUser = await userManager.FindByIdAsync(userGuid.ToString());
        if (currentUser is not null && !currentUser.EmailConfirmed)
        {
            currentUser.EmailConfirmed = true;
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        // Only for a link that was just created or reactivated (#1108 review) — an already-linked
        // client re-using this token would otherwise get a fresh, undeduplicated Accepted
        // banner on every re-use, since a null-source event has no index to dedupe on.
        // Show the "waiting" state in the portal when the invite carried a questionnaire. A widened link gets
        // its own response for the added scope only.
        if ((linkStarted || activation.Extended) && pendingInvite?.QuestionnaireId is { } questionnaireId)
        {
            db.QuestionnaireResponses.Add(new QuestionnaireResponse
            {
                QuestionnaireId = questionnaireId,
                ClientId = userGuid,
                ProfessionalId = invitation.ProfessionalProfile.UserId,
                LinkId = activation.Link!.Id,
                Status = QuestionnaireResponseStatus.Pending,
                Kind = QuestionnaireResponseKind.Onboarding,
                Scope = activation.GrantedScope,
            });
            await db.SaveChangesAsync(ct);
        }

        if (linkStarted)
        {
            // A client who was already linked keeps whatever the coach set on the Payments tab.
            if (pendingInvite?.CoachPackageId is { } packageId)
            {
                await termsApplier.ApplyAsync(
                    pendingInvite.PublicId,
                    pendingInvite.ProfessionalProfileId,
                    packageId,
                    pendingInvite.CoachDiscountId,
                    clientProfile.Id,
                    userGuid,
                    HttpContext.Connection.RemoteIpAddress?.ToString(),
                    timeProvider.GetUtcNow().UtcDateTime,
                    ct);
            }
        }

        // Chat events also follow a widened link; deduped by the invite's PublicId.
        if (inviteAccepted)
        {
            if (pendingInvite is not null)
            {
                await conversationSeedService.AppendCooperationEventAsync(
                    invitation.ProfessionalProfile.UserId, userGuid, invitation.ProfessionalProfile.UserId,
                    ChatEventType.Invited, pendingInvite.PublicId, pendingInvite.Message,
                    createConversationIfMissing: true, ct);
            }

            await conversationSeedService.AppendCooperationEventAsync(
                invitation.ProfessionalProfile.UserId, userGuid, userGuid,
                ChatEventType.Accepted, pendingInvite?.PublicId, messageText: null,
                createConversationIfMissing: true, ct);
        }

        // Audit: new data sharing relationship established
        await audit.LogAsync(
            userGuid,
            "AcceptInvitation",
            nameof(ClientProfessionalLink),
            invitation.ProfessionalProfile.PublicId,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            ct: ct);

        // Notify the professional that their (token-based) invitation was accepted (#770).
        // This flow previously created the ClientProfessionalLink with no notification or
        // SignalR emit at all, so the professional's notification bell and web client-list
        // never learned about the new link until an unrelated action refreshed the page.
        // Mirrors the pattern used by AcceptClientInviteEndpoint for the in-app invite flow.
        var clientName = currentUser is not null
            ? $"{currentUser.FirstName} {currentUser.LastName}"
            : "A client";

        await notificationService.CreateAsync(
            invitation.ProfessionalProfile.UserId,
            NotificationType.ClientRequestAccepted,
            new Dictionary<string, string> { ["clientName"] = clientName },
            ct: ct);

        await notifier.NotifyAsync(
            invitation.ProfessionalProfile.UserId,
            "inviteaccepted",
            new { clientName },
            ct);

        await Send.OkAsync(new AcceptInvitationResponse
        {
            Message = "Invitation accepted successfully.",
            TrainerPublicId = invitation.ProfessionalProfile.PublicId
        }, ct);
    }
}
