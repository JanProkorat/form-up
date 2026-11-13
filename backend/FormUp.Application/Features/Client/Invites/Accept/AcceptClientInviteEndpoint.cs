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

namespace FormUp.Application.Features.Client.Invites.Accept;

/// <summary>
/// Client accepts a pending invite by its PublicId. Creates the client-professional link, or reactivates an ended one.
/// </summary>
public class AcceptClientInviteEndpoint(
    IApplicationDbContext db,
    UserManager<ApplicationUser> userManager,
    INotificationService notificationService,
    IRealtimeNotifier notifier,
    IAuditService audit,
    IConversationSeedService conversationSeedService,
    TimeProvider timeProvider,
    InvitePackageTermsApplier termsApplier)
    : Endpoint<AcceptClientInviteRequest>
{
    public override void Configure()
    {
        Post("/client/invites/{Id}/accept");
        Roles(AppRoles.Client);
        Summary(s =>
        {
            s.Summary = "Accept a pending invite";
            s.Description = "Accepts a pending invitation from a professional, creating a client-professional link.";
            s.Response(StatusCodes.Status204NoContent, "Invite accepted");
            s.Response<ProblemDetails>(StatusCodes.Status400BadRequest, "The profession slot is already taken (PROFESSION_ALREADY_OCCUPIED)");
            s.Response(StatusCodes.Status401Unauthorized, "Missing or unreadable caller claim");
            s.Response(StatusCodes.Status403Forbidden,
                "The coach role was removed, or the coach is at the active-client limit (CLIENT_LIMIT_REACHED)");
            s.Response(StatusCodes.Status404NotFound, "Invite not found, not addressed to the caller, or the caller is a coach");
            s.Response(StatusCodes.Status409Conflict, "The invite has expired (INVITE_EXPIRED)");
        });
    }

    public override async Task HandleAsync(AcceptClientInviteRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);
        if (userId is null) { await Send.UnauthorizedAsync(ct); return; }

        var userGuid = Guid.Parse(userId);

        // Invites addressed to a coaching account are silent rows — never actionable by it.
        if (User.IsInRole(AppRoles.Trainer) || User.IsInRole(AppRoles.Nutritionist))
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var caller = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userGuid, ct);
        if (caller is null) { await Send.UnauthorizedAsync(ct); return; }

        // Use NormalizedEmail (uppercase, set by Identity) for reliable matching.
        // PendingInvite.Email stores the original casing from the trainer, so compare
        // using UPPER() on both sides. Folding this into the lookup itself (rather than
        // checking after) means a GUID that belongs to someone else's invite falls
        // through to the same 404 as an unknown/consumed invite — never a distinct 403
        // that would confirm the GUID exists.
        var normalizedEmail = caller.NormalizedEmail ?? caller.Email?.ToUpper() ?? string.Empty;

        var invite = await db.PendingInvites
            .Include(pi => pi.ProfessionalProfile)
            .FirstOrDefaultAsync(pi => pi.PublicId == req.Id && pi.Status == PendingInviteStatus.Pending
                && pi.Email.ToUpper() == normalizedEmail, ct);

        if (invite is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (PendingInviteStatusResolver.Resolve(invite.Status, invite.SentAt, timeProvider.GetUtcNow().UtcDateTime)
            == PendingInviteStatus.Expired)
        {
            await this.SendProblemAsync(StatusCodes.Status409Conflict, ErrorCodes.InviteExpired,
                "This invitation has expired.", ct);
            return;
        }

        // Ensure or create client profile
        var clientProfile = await db.ClientProfiles
            .FirstOrDefaultAsync(cp => cp.UserId == userGuid, ct);

        if (clientProfile is null)
        {
            clientProfile = new ClientProfile { UserId = userGuid };
            db.ClientProfiles.Add(clientProfile);
            await db.SaveChangesAsync(ct);
        }

        // Serialize the slot check below against a concurrent link creation for the SAME client
        // (#1009). Under READ COMMITTED neither racer can see the other's uncommitted link, so
        // there is nothing on the link side to lock — the client's own already-committed profile
        // row is the one row both racers must touch, so that is what gets locked.
        // Reachable by a single actor: this endpoint is Roles(Client), so a client holding two
        // pending invites from two professionals can accept both inside one request duration.
        // The lock is taken AFTER the find-or-create save above, or the row would not exist yet.
        await using var transaction = await db.BeginTransactionAsync(ct);
        await db.LockClientProfileAsync(clientProfile.Id, ct);

        if (await this.RespondIfClientLimitReachedAsync(db, invite.ProfessionalProfileId, clientProfile.Id, ct))
        {
            return;
        }

        // Create the link, or switch an ended one back on (a re-invited former client).
        var activation = await ClientLinkActivator.ActivateAsync(
            db, userManager, clientProfile.Id, invite.ProfessionalProfile,
            invite.RequestedScope, invite.QuestionnaireId,
            invite.SkipOnboardingForm, ct);

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

        invite.Status = PendingInviteStatus.Accepted;

        // Also mark matching invitation tokens as used
        var matchingTokens = await db.InvitationTokens
            .Where(t => t.ProfessionalProfileId == invite.ProfessionalProfileId
                        && t.Email == invite.Email
                        && !t.IsUsed)
            .ToListAsync(ct);

        foreach (var token in matchingTokens)
            token.IsUsed = true;

        // Save link first so it gets a generated Id for the questionnaire response
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        // If the invite included a questionnaire, create a pending response
        // so the web portal immediately shows the "waiting" state. A widened link gets its own response
        // for the added scope only; the link's own questionnaire stays as it was.
        if ((activation.Started || activation.Extended) && invite.QuestionnaireId.HasValue)
        {
            var questionnaireResponse = new QuestionnaireResponse
            {
                QuestionnaireId = invite.QuestionnaireId.Value,
                ClientId = userGuid,
                ProfessionalId = invite.ProfessionalProfile.UserId,
                LinkId = activation.Link!.Id,
                Status = QuestionnaireResponseStatus.Pending,
                Kind = QuestionnaireResponseKind.Onboarding,
                Scope = activation.GrantedScope,
            };
            db.QuestionnaireResponses.Add(questionnaireResponse);
            await db.SaveChangesAsync(ct);
        }

        // Only a newly started (created or reactivated) link takes the invite's terms: a client who was already linked keeps whatever
        // the coach set on the Payments tab. A package that no longer qualifies is skipped, never an error.
        if (activation.Started && invite.CoachPackageId is { } packageId)
        {
            await termsApplier.ApplyAsync(
                invite.PublicId,
                invite.ProfessionalProfileId,
                packageId,
                invite.CoachDiscountId,
                clientProfile.Id,
                userGuid,
                HttpContext.Connection.RemoteIpAddress?.ToString(),
                timeProvider.GetUtcNow().UtcDateTime,
                ct);
        }

        // Ensure the invite's Invited event + message exist (idempotent no-op if the
        // immediate invite-time or VerifyEmail-time seed already wrote them — e.g. this
        // invite's target account was unverified at invite time and only became verified
        // later), then record this Accepted event. Both keyed on invite.PublicId via the
        // partial unique index inside AppendCooperationEventAsync, so a double-accept
        // writes at most one Accepted row and never duplicates the message.
        await conversationSeedService.AppendCooperationEventAsync(
            invite.ProfessionalProfile.UserId, userGuid, invite.ProfessionalProfile.UserId,
            ChatEventType.Invited, invite.PublicId, invite.Message,
            createConversationIfMissing: true, ct);

        await conversationSeedService.AppendCooperationEventAsync(
            invite.ProfessionalProfile.UserId, userGuid, userGuid,
            ChatEventType.Accepted, invite.PublicId, messageText: null,
            createConversationIfMissing: true, ct);

        // Notify the professional that their invite was accepted
        var clientName = $"{caller.FirstName} {caller.LastName}";

        await notificationService.CreateAsync(
            invite.ProfessionalProfile.UserId,
            NotificationType.ClientRequestAccepted,
            new Dictionary<string, string> { ["clientName"] = clientName },
            ct: ct);

        await notifier.NotifyAsync(
            invite.ProfessionalProfile.UserId,
            "inviteaccepted",
            new { clientName, inviteId = invite.PublicId },
            ct);

        await audit.LogAsync(userGuid, "AcceptInviteFromApp", nameof(ClientProfessionalLink),
            invite.ProfessionalProfile.PublicId,
            HttpContext.Connection.RemoteIpAddress?.ToString(), ct: ct);

        await Send.NoContentAsync(ct);
    }
}

public class AcceptClientInviteRequest
{
    public Guid Id { get; set; }
}
