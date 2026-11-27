using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FormUp.Application.Infrastructure.Services;

/// <inheritdoc cref="IConversationSeedService"/>
public class ConversationSeedService(IApplicationDbContext db, IChatMessageNotifier chatMessageNotifier)
    : IConversationSeedService
{
    /// <inheritdoc />
    public async Task<Conversation> GetOrSeedConversationAsync(
        Guid professionalUserId,
        Guid clientUserId,
        Guid senderUserId,
        string senderName,
        string? messageText,
        bool seedIntoExisting,
        CancellationToken ct)
    {
        var conversation = await db.Conversations
            .FirstOrDefaultAsync(c =>
                c.ProfessionalUserId == professionalUserId &&
                c.ClientUserId == clientUserId, ct);

        var isNewConversation = conversation is null;

        if (conversation is null)
        {
            conversation = new Conversation
            {
                ProfessionalUserId = professionalUserId,
                ClientUserId = clientUserId,
            };
            db.Conversations.Add(conversation);
        }

        // FIX 3 (#768 review): invite-creation callers (seedIntoExisting: true) must
        // still deliver the message into an already-existing conversation — matches
        // the pre-extraction inline behavior. Invite-accept callers (seedIntoExisting:
        // false) only ever seed a brand-new conversation, so re-accepting/re-processing
        // the same invite can't duplicate a message that was already delivered (either
        // at invite-creation time, or by an earlier accept).
        var shouldSeedMessage = !string.IsNullOrWhiteSpace(messageText) && (isNewConversation || seedIntoExisting);

        ChatMessage? message = null;

        if (shouldSeedMessage)
        {
            var text = messageText!.Trim();

            // FIX 1 (#768 review): build the message via the Conversation navigation
            // property — NOT a post-save ConversationId assignment — so the new
            // conversation and its first message are inserted in a SINGLE
            // SaveChangesAsync below. The previous two-save form could persist a
            // conversation shell with no message if the second save failed, and
            // because the next attempt would then see isNewConversation == false,
            // the message would be permanently lost.
            message = new ChatMessage
            {
                Conversation = conversation,
                SenderUserId = senderUserId,
                Text = text,
                IsRead = false,
            };
            db.ChatMessages.Add(message);

            conversation.LastMessageText = text.Length > 300 ? text[..300] : text;
            conversation.LastMessageAt = DateTime.UtcNow;
            conversation.LastMessageSenderId = senderUserId;
        }

        if (isNewConversation || shouldSeedMessage)
        {
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (isNewConversation && IsUniqueViolation(ex))
            {
                // FIX 2 (#768 review): a concurrent request (double-tap accept, client
                // retry) won the race and already inserted the (ProfessionalUserId,
                // ClientUserId) conversation first. Detach our losing Added entities
                // (Remove() on an entity still in the Added state just untracks it —
                // no DELETE is issued) and re-query the winner's row. Treat this as
                // the "already existed" no-op branch: no seed, no duplicate, no 500.
                if (message is not null) db.ChatMessages.Remove(message);
                db.Conversations.Remove(conversation);

                conversation = await db.Conversations
                    .FirstAsync(c =>
                        c.ProfessionalUserId == professionalUserId &&
                        c.ClientUserId == clientUserId, ct);

                return conversation;
            }
        }

        if (shouldSeedMessage)
        {
            var recipientUserId = senderUserId == professionalUserId ? clientUserId : professionalUserId;

            await chatMessageNotifier.NotifyNewMessageAsync(
                message!, conversation.PublicId, recipientUserId, senderName, imageUrl: null, sendPush: true, ct);
        }

        return conversation;
    }

    /// <inheritdoc />
    public async Task AppendCooperationEventAsync(
        Guid professionalUserId,
        Guid clientUserId,
        Guid actorUserId,
        ChatEventType eventType,
        Guid? sourceId,
        string? messageText,
        bool createConversationIfMissing,
        CancellationToken ct,
        bool pushCompanionText = false)
    {
        var conversation = await db.Conversations
            .FirstOrDefaultAsync(c =>
                c.ProfessionalUserId == professionalUserId &&
                c.ClientUserId == clientUserId, ct);

        var isNewConversation = conversation is null;

        if (conversation is null)
        {
            if (!createConversationIfMissing)
            {
                return;
            }

            conversation = new Conversation
            {
                ProfessionalUserId = professionalUserId,
                ClientUserId = clientUserId,
            };
            db.Conversations.Add(conversation);
        }

        // Cheap pre-check for the common no-op case (VerifyEmail's seed, then
        // CreatePendingInvite's or AcceptInvitation's own "ensure Invited" call, all
        // targeting the same sourceId) — avoids the 23505-driven EF error log the
        // catch block below produces on every one of those. A brand-new conversation
        // can't already carry this row, so skip the query there (#1108 review).
        if (sourceId.HasValue && !isNewConversation && await db.ChatMessages
                .AsNoTracking()
                .AnyAsync(m => m.ConversationId == conversation.Id && m.EventType == eventType && m.EventSourceId == sourceId, ct))
        {
            return;
        }

        // Captured before any LastMessage* mutation below, so a duplicate-event
        // rollback (see the catch block) can restore exactly what was persisted —
        // IApplicationDbContext does not expose EF's Entry()/OriginalValues, and
        // doesn't need to: reassigning these captured values makes the tracked
        // entity's current values equal its original snapshot again, which is all
        // EF's default snapshot change tracking needs to see it as Unchanged on the
        // next SaveChangesAsync.
        var originalLastMessageText = isNewConversation ? null : conversation.LastMessageText;
        var originalLastMessageAt = isNewConversation ? null : conversation.LastMessageAt;
        var originalLastMessageSenderId = isNewConversation ? null : conversation.LastMessageSenderId;
        var originalLastMessageHasImage = !isNewConversation && conversation.LastMessageHasImage;
        var originalLastMessageEventType = isNewConversation ? null : conversation.LastMessageEventType;

        var recipientUserId = actorUserId == professionalUserId ? clientUserId : professionalUserId;

        var actorUser = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == actorUserId, ct);
        var actorName = actorUser is not null ? $"{actorUser.FirstName} {actorUser.LastName}" : string.Empty;

        // The fallback line is rendered in the CLIENT's language regardless of which
        // participant is the actor — this is the same row both participants read.
        var clientUser = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == clientUserId, ct);
        var eventText = ChatEventTemplates.Resolve(eventType, clientUser?.Language, actorName);

        // Added to the change tracker BEFORE the optional message below, so a single
        // SaveChangesAsync batch inserts the event row first — the event must
        // deterministically sort before the message in GetMessages'
        // (DateCreated, Id) order.
        var eventMessage = new ChatMessage
        {
            Conversation = conversation,
            SenderUserId = actorUserId,
            Kind = ChatMessageKind.Event,
            EventType = eventType,
            EventSourceId = sourceId,
            Text = eventText,
            IsRead = false,
        };
        db.ChatMessages.Add(eventMessage);

        ChatMessage? textMessage = null;

        if (!string.IsNullOrWhiteSpace(messageText))
        {
            textMessage = new ChatMessage
            {
                Conversation = conversation,
                SenderUserId = actorUserId,
                Kind = ChatMessageKind.Text,
                Text = messageText.Trim(),
                IsRead = false,
            };
            db.ChatMessages.Add(textMessage);
        }

        var lastMessage = textMessage ?? eventMessage;
        conversation.LastMessageText = lastMessage.Text.Length > 300 ? lastMessage.Text[..300] : lastMessage.Text;
        conversation.LastMessageAt = DateTime.UtcNow;
        conversation.LastMessageSenderId = actorUserId;
        conversation.LastMessageHasImage = false;
        conversation.LastMessageEventType = textMessage is null ? eventType : null;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Untrack our losing Added rows (Remove() on an Added entity just untracks
            // it, no DELETE) — otherwise they'd survive to re-INSERT and re-throw 23505
            // on the caller's next unrelated SaveChangesAsync.
            if (textMessage is not null)
            {
                db.ChatMessages.Remove(textMessage);
            }

            db.ChatMessages.Remove(eventMessage);

            var constraintName = (ex.InnerException as PostgresException)?.ConstraintName;

            if (isNewConversation && constraintName == ConversationIdentityIndexName)
            {
                // A concurrent request won the conversation-identity race, not a duplicate
                // event — untrack our losing shell and retry once against the now-existing row.
                db.Conversations.Remove(conversation);

                await AppendCooperationEventAsync(
                    professionalUserId, clientUserId, actorUserId, eventType, sourceId,
                    messageText, createConversationIfMissing: false, ct, pushCompanionText);
                return;
            }

            // A genuinely re-processed event hit the partial unique index — swallow it as
            // a no-op (no broadcast) and restore the conversation's pre-mutation
            // LastMessage* values so a later save can't commit a phantom preview.
            if (!isNewConversation)
            {
                conversation.LastMessageText = originalLastMessageText;
                conversation.LastMessageAt = originalLastMessageAt;
                conversation.LastMessageSenderId = originalLastMessageSenderId;
                conversation.LastMessageHasImage = originalLastMessageHasImage;
                conversation.LastMessageEventType = originalLastMessageEventType;
            }
            else
            {
                db.Conversations.Remove(conversation);
            }

            return;
        }

        // One event for the last row; the Event row itself never pushes (the seam checks the kind).
        await chatMessageNotifier.NotifyNewMessageAsync(
            lastMessage, conversation.PublicId, recipientUserId, actorName, imageUrl: null, pushCompanionText, ct);
    }

    /// <summary>
    /// The unique index name (snake_case, per the Npgsql naming convention) backing
    /// <c>Conversation</c>'s (ProfessionalUserId, ClientUserId) identity constraint —
    /// see <see cref="ApplicationDbContext"/>'s <c>OnModelCreating</c>. Used to tell a
    /// concurrent first-contact race apart from a duplicate cooperation event, which
    /// hits the chat_messages partial unique index instead.
    /// </summary>
    private const string ConversationIdentityIndexName = "ix_conversations_professional_user_id_client_user_id";

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException pgEx && pgEx.SqlState == "23505";
}
