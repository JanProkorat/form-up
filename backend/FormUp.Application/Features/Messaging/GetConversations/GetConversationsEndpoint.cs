using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.Messaging.Shared;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using FormUp.Application.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.Messaging.GetConversations;

/// <summary>
/// Returns conversations for the authenticated user (professional or client).
/// </summary>
/// <remarks>
/// A Client caller always gets today's plain, link-state-agnostic conversation query (including
/// any <c>IsFormer</c> row) and may never request a chip other than
/// <see cref="ClientListFilter.All"/>.
/// <para/>
/// A professional caller always goes through the live-roster path
/// (<see cref="ConversationRosterLoader"/>, live links only): every linked client that has a
/// conversation is classified against <see cref="GetConversationsRequest.Filter"/> — the same
/// six-way chip set <c>GetClientsEndpoint</c> exposes. A linked client with no conversation yet is
/// not listed. <c>archived</c> is applied after the chip.
/// <para/>
/// <see cref="ClientListFilter.All"/> (or an omitted filter) additionally unions in every
/// conversation whose client is NOT on the live roster (a deactivated/former link, including any
/// <c>IsFormer</c> row) — the population the plain query always returned — so a professional's
/// All view never loses a conversation just because the underlying link ended. Every other chip
/// stays roster-only, except UnreadMessages, which also lists pre-link conversations (no link yet,
/// not former, <see cref="PreLinkConversations"/>). A former client's unread conversation never
/// surfaces under UnreadMessages.
/// </remarks>
/// <param name="db">Database context.</param>
/// <param name="mongo">MongoDB context — plan-window lookups for the EndingSoon chip.</param>
/// <param name="presence">Resolves each participant's live online status.</param>
/// <param name="timeProvider">Clock abstraction — lets tests pin "now" deterministically.</param>
public class GetConversationsEndpoint(
    IApplicationDbContext db,
    IMongoContext mongo,
    PresenceTracker presence,
    TimeProvider timeProvider) : Endpoint<GetConversationsRequest, List<ConversationDto>>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/conversations");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist, AppRoles.Client);
        Summary(s =>
        {
            s.Summary = "Get conversations";
            s.Description = "Returns all existing conversations for the authenticated user, optionally " +
                             "narrowed by a clients-list-style filter chip (professional callers only). " +
                             "Linked clients with no conversation yet are not listed. The UnreadMessages chip also " +
                             "lists conversations of clients with no link yet.";
            s.Responses[StatusCodes.Status200OK] = "Conversation list, newest activity first.";
            s.Responses[StatusCodes.Status400BadRequest] = "filter is not a member of ClientListFilter, " +
                                                             "or a non-All filter was requested by a Client caller.";
            s.Responses[StatusCodes.Status401Unauthorized] = "No caller claim on the request.";
            s.Responses[StatusCodes.Status404NotFound] = "Caller is a professional with no ProfessionalProfile.";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(GetConversationsRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);
        if (userId is null) { await Send.UnauthorizedAsync(ct); return; }

        var userGuid = Guid.Parse(userId);
        var isProfessional = User.IsInRole(AppRoles.Trainer) || User.IsInRole(AppRoles.Nutritionist);

        if (!isProfessional)
        {
            if (req.Filter is not null && req.Filter != ClientListFilter.All)
            {
                this.ThrowErrorWithCode(
                    ErrorCodes.FilterRequiresProfessionalCaller,
                    "Filter chips are only available to a professional caller.");
                return;
            }

            var clientConversations = await LoadPlainConversationsAsync(isProfessional: false, userGuid, req.Archived, ct);
            await EnrichAsync(clientConversations, userGuid, timeProvider.GetUtcNow().UtcDateTime, ct);
            await Send.OkAsync(clientConversations, ct);
            return;
        }

        var professionalProfile = await db.ProfessionalProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(pp => pp.UserId == userGuid, ct);

        if (professionalProfile is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var roster = await ConversationRosterLoader.LoadAsync(db, mongo, professionalProfile.Id, userGuid, now, ct);

        var matched = roster.Where(r => ClientRosterFilterClassifier.Matches(
            req.Filter,
            r.UnreadMessageCount > 0,
            r.ConversationMessageCount == 0,
            r.HasNewCheckIn,
            r.HasMissingCheckIn,
            r.IsEndingSoon));

        var displayed = matched
            .Where(r => r.IsArchivedByProfessional == req.Archived)
            .Select(r => BuildFromRosterRow(r, userGuid))
            .ToList();

        if (req.Filter is null || req.Filter == ClientListFilter.All)
        {
            var rosterClientIds = roster.Select(r => r.ClientUserId).ToHashSet();
            var offRosterConversations = await LoadPlainConversationsAsync(
                isProfessional: true, userGuid, req.Archived, ct, excludedClientUserIds: rosterClientIds);
            displayed.AddRange(offRosterConversations);
        }

        if (req.Filter == ClientListFilter.UnreadMessages)
        {
            var preLinkClientIds = await PreLinkConversations
                .WithUnreadMessages(db, userGuid)
                .AsNoTracking()
                .Select(c => c.ClientUserId)
                .ToListAsync(ct);

            if (preLinkClientIds.Count > 0)
            {
                displayed.AddRange(await LoadPlainConversationsAsync(
                    isProfessional: true, userGuid, req.Archived, ct, includedClientUserIds: preLinkClientIds));
            }
        }

        displayed = displayed.OrderByDescending(c => c.LastMessageAt).ToList();

        await EnrichAsync(displayed, userGuid, now, ct);
        SetPresence(displayed);
        await Send.OkAsync(displayed, ct);
    }

    /// <summary>
    /// Today's pre-existing query — unchanged for a Client caller. A professional caller also
    /// reaches this from the All chip, restricted via <paramref name="excludedClientUserIds"/> to
    /// the clients NOT already represented by a live-roster row, so a former/deactivated-link
    /// conversation (including any <c>IsFormer</c> one) still surfaces once, never duplicated with
    /// its roster row.
    /// </summary>
    private async Task<List<ConversationDto>> LoadPlainConversationsAsync(
        bool isProfessional, Guid userGuid, bool archived, CancellationToken ct,
        IReadOnlySet<Guid>? excludedClientUserIds = null,
        IReadOnlyCollection<Guid>? includedClientUserIds = null)
    {
        var conversations = await db.Conversations
            .AsNoTracking()
            .Where(c => isProfessional ? c.ProfessionalUserId == userGuid : c.ClientUserId == userGuid)
            .Where(c => excludedClientUserIds == null || !excludedClientUserIds.Contains(c.ClientUserId))
            .Where(c => includedClientUserIds == null || includedClientUserIds.Contains(c.ClientUserId))
            .Where(c => isProfessional
                ? (archived ? c.ArchivedByProfessionalAt != null : c.ArchivedByProfessionalAt == null)
                : (archived ? c.ArchivedByClientAt != null : c.ArchivedByClientAt == null))
            .OrderByDescending(c => c.LastMessageAt ?? c.DateCreated)
            .Select(c => new ConversationDto
            {
                Id = c.PublicId,
                Participant = isProfessional
                    ? new ParticipantDto
                    {
                        Id = c.Client.Id,
                        Name = c.Client.FirstName + " " + c.Client.LastName,
                        Initials = (c.Client.FirstName.Substring(0, 1) + c.Client.LastName.Substring(0, 1)).ToUpper(),
                        Online = false, // populated below
                        // ClientProfile has no dedicated AvatarBlobUrl; use the user-level avatar.
                        AvatarBlobUrl = c.Client.AvatarBlobUrl,
                        ClientPublicId = c.Client.ClientProfile!.PublicId,
                    }
                    : new ParticipantDto
                    {
                        Id = c.Professional.Id,
                        Name = c.Professional.FirstName + " " + c.Professional.LastName,
                        Initials = (c.Professional.FirstName.Substring(0, 1) + c.Professional.LastName.Substring(0, 1)).ToUpper(),
                        Online = false, // populated below
                        // Prefer the professional-profile avatar; fall back to the user-level avatar.
                        AvatarBlobUrl = c.Professional.ProfessionalProfile != null
                            ? c.Professional.ProfessionalProfile.AvatarBlobUrl ?? c.Professional.AvatarBlobUrl
                            : c.Professional.AvatarBlobUrl,
                        ClientPublicId = null,
                    },
                LastMessage = c.LastMessageText ?? "",
                LastMessageAt = c.LastMessageAt ?? c.DateCreated,
                LastMessageIsOwn = c.LastMessageSenderId == userGuid,
                UnreadCount = c.Messages.Count(m =>
                    !m.IsRead && m.SenderUserId != userGuid && m.Kind != ChatMessageKind.Event),
                IsFormer = c.IsFormer,
                LastMessageHasImage = c.LastMessageHasImage,
                LastMessageEventType = c.LastMessageEventType,
            })
            .ToListAsync(ct);

        SetPresence(conversations);

        if (isProfessional)
        {
            var access = await ProfessionalSendGuard.EvaluateManyAsync(
                db, userGuid, conversations.Select(c => c.Participant.Id).ToList(), ct);

            foreach (var conversation in conversations)
            {
                conversation.IsSendLocked = access[conversation.Participant.Id] == ProfessionalSendAccess.Locked;

                conversation.Participant.Online = conversation.Participant.Online && !conversation.IsSendLocked;
            }
        }

        return conversations;
    }

    /// <summary>
    /// Adds cooperation, participant coach roles and the former-collaboration state to the final
    /// page in one batched pass (a fixed number of queries, independent of the page size). Every
    /// lookup is keyed by the page's own conversation ids, so nothing outside the caller's
    /// conversations can be read.
    /// </summary>
    private async Task EnrichAsync(
        List<ConversationDto> conversations, Guid userGuid, DateTime now, CancellationToken ct)
    {
        var conversationIds = conversations
            .Where(c => c.Id.HasValue)
            .Select(c => c.Id!.Value)
            .ToList();

        if (conversationIds.Count == 0)
        {
            return;
        }

        var pairs = (await db.Conversations
            .AsNoTracking()
            .Where(c => conversationIds.Contains(c.PublicId) &&
                        (c.ProfessionalUserId == userGuid || c.ClientUserId == userGuid))
            .Select(c => new { c.PublicId, c.ProfessionalUserId, c.ClientUserId, c.IsFormer })
            .ToListAsync(ct))
            .ToDictionary(c => c.PublicId);

        var newestProposalByConversation = (await db.ChatMessages
            .AsNoTracking()
            .Where(m => m.Kind == ChatMessageKind.Event &&
                        (m.EventType == ChatEventType.Invited || m.EventType == ChatEventType.Requested) &&
                        conversationIds.Contains(m.Conversation.PublicId))
            .Select(m => new
            {
                ConversationPublicId = m.Conversation.PublicId,
                m.EventType,
                m.SenderUserId,
                m.EventSourceId,
                m.DateCreated,
                m.Id,
            })
            .ToListAsync(ct))
            .GroupBy(m => m.ConversationPublicId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(m => m.DateCreated).ThenByDescending(m => m.Id).First());

        var sourceStates = await CooperationStatusResolver.ResolveAsync(
            db,
            newestProposalByConversation.Values
                .Where(m => m.EventSourceId.HasValue)
                .Select(m => m.EventSourceId!.Value)
                .Distinct()
                .ToList(),
            now,
            ct);

        var rolesByUser = await LoadCoachRolesAsync(
            conversations.Select(c => c.Participant.Id).Distinct().ToList(), ct);

        var clientUserIds = pairs.Values.Select(p => p.ClientUserId).Distinct().ToList();
        var professionalUserIds = pairs.Values.Select(p => p.ProfessionalUserId).Distinct().ToList();

        var links = await db.ClientProfessionalLinks
            .AsNoTracking()
            .Where(l => clientUserIds.Contains(l.ClientProfile.UserId) &&
                        professionalUserIds.Contains(l.ProfessionalProfile.UserId))
            .Select(l => new
            {
                ClientUserId = l.ClientProfile.UserId,
                ProfessionalUserId = l.ProfessionalProfile.UserId,
                IsActive = l.ConnectionState != ConnectionState.Ended,
                ChangedAt = l.DateUpdated ?? l.DateCreated,
            })
            .ToListAsync(ct);

        foreach (var conversation in conversations)
        {
            if (conversation.Id is not { } conversationId || !pairs.TryGetValue(conversationId, out var pair))
            {
                continue;
            }

            if (rolesByUser.TryGetValue(conversation.Participant.Id, out var roles))
            {
                conversation.Participant.Roles = roles;
            }

            if (newestProposalByConversation.TryGetValue(conversationId, out var proposal) &&
                proposal.EventSourceId is { } sourceId &&
                sourceStates.TryGetValue(sourceId, out var state))
            {
                conversation.Cooperation = new ConversationCooperationDto
                {
                    Type = proposal.EventType == ChatEventType.Invited
                        ? CooperationType.Invite
                        : CooperationType.Request,
                    Direction = proposal.SenderUserId == userGuid
                        ? CooperationDirection.Sent
                        : CooperationDirection.Received,
                    Status = state.Status,
                    SourceId = sourceId,
                    RequestedScope = state.RequestedScope,
                };
            }

            var pairLinks = links
                .Where(l => l.ClientUserId == pair.ClientUserId && l.ProfessionalUserId == pair.ProfessionalUserId)
                .ToList();
            var inactiveLinks = pairLinks.Where(l => !l.IsActive).ToList();

            conversation.IsFormer = pair.IsFormer || (!pairLinks.Any(l => l.IsActive) && inactiveLinks.Count > 0);
            conversation.EndedAt = conversation.IsFormer && inactiveLinks.Count > 0
                ? inactiveLinks.Max(l => l.ChangedAt)
                : null;
        }
    }

    /// <summary>
    /// Loads the active coach roles of the given users with one query for the role assignments
    /// and one for the professional profiles that carry removal markers.
    /// </summary>
    private async Task<Dictionary<Guid, List<string>>> LoadCoachRolesAsync(
        List<Guid> userIds, CancellationToken ct)
    {
        var held = await (
            from ur in db.UserRoles.AsNoTracking()
            join r in db.Roles.AsNoTracking() on ur.RoleId equals r.Id
            where userIds.Contains(ur.UserId) && (r.Name == AppRoles.Trainer || r.Name == AppRoles.Nutritionist)
            select new { ur.UserId, RoleName = r.Name! })
            .ToListAsync(ct);

        if (held.Count == 0)
        {
            return [];
        }

        var heldUserIds = held.Select(h => h.UserId).Distinct().ToList();
        var profiles = (await db.ProfessionalProfiles
            .AsNoTracking()
            .Where(p => heldUserIds.Contains(p.UserId))
            .ToListAsync(ct))
            .ToDictionary(p => p.UserId);

        return held
            .GroupBy(h => h.UserId)
            .ToDictionary(
                g => g.Key,
                g => CoachRoleStatus.ActiveRoles(
                        g.Select(h => h.RoleName),
                        profiles.GetValueOrDefault(g.Key))
                    .ToList());
    }

    /// <summary>
    /// Maps a roster row (always backed by an existing conversation) to a <see cref="ConversationDto"/>.
    /// </summary>
    private static ConversationDto BuildFromRosterRow(ConversationRosterRow row, Guid userGuid) =>
        new()
        {
            Id = row.ConversationPublicId,
            Participant = new ParticipantDto
            {
                Id = row.ClientUserId,
                Name = row.ClientFirstName + " " + row.ClientLastName,
                Initials = ParticipantInitials.Compute(row.ClientFirstName, row.ClientLastName, row.ClientEmail),
                Online = false, // populated below
                AvatarBlobUrl = row.ClientAvatarBlobUrl,
                ClientPublicId = row.ClientPublicId,
            },
            LastMessage = row.LastMessage,
            LastMessageAt = row.LastMessageAt,
            LastMessageIsOwn = row.LastMessageSenderId == userGuid,
            UnreadCount = row.UnreadMessageCount,
            IsFormer = false, // the live roster never includes a former collaboration.
            IsSendLocked = false, // a live link always allows sending.
            LastMessageHasImage = row.LastMessageHasImage,
            LastMessageEventType = row.LastMessageEventType,
        };

    private void SetPresence(List<ConversationDto> conversations)
    {
        foreach (var c in conversations)
        {
            // An invitee who has not accepted is never shown online to the inviter.
            c.Participant.Online = !c.IsSendLocked && presence.IsOnline(c.Participant.Id);
        }
    }
}
