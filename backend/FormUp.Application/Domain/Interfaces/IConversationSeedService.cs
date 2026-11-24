using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Domain.Interfaces;

/// <summary>
/// Gets or creates the professional-client <see cref="Conversation"/> and seeds it
/// with an initial message authored by one of the two participants. Used by flows
/// that need a professional's own free-text (an invite's personal message, an
/// accept-time statement) to surface as a real chat message on both participants'
/// Messages screens, instead of being silently dropped.
/// </summary>
/// <remarks>
/// Extracted (rule of three, per
/// <c>rules/code-quality.md#no-re-layered-services</c>) once the same
/// get-or-create-conversation + append-first-message + broadcast "newmessage" shape
/// was needed by <c>CreatePendingInviteEndpoint</c>, <c>AcceptClientInviteEndpoint</c>,
/// and <c>AcceptInvitationEndpoint</c> (#768).
/// </remarks>
public interface IConversationSeedService
{
    /// <summary>
    /// Gets or creates the conversation between <paramref name="professionalUserId"/>
    /// and <paramref name="clientUserId"/>. If <paramref name="messageText"/> is
    /// non-empty, appends it as a message (authored by <paramref name="senderUserId"/>,
    /// which must be one of the two participants) and broadcasts the existing
    /// "newmessage" SignalR event to the other participant — but only when the
    /// conversation is brand-new, OR when <paramref name="seedIntoExisting"/> is true.
    /// </summary>
    /// <param name="professionalUserId">The professional participant's user id.</param>
    /// <param name="clientUserId">The client participant's user id.</param>
    /// <param name="senderUserId">
    /// The author of the seeded message — must equal either
    /// <paramref name="professionalUserId"/> or <paramref name="clientUserId"/>.
    /// </param>
    /// <param name="senderName">Display name used in the "newmessage" broadcast payload.</param>
    /// <param name="messageText">
    /// The message to seed. A null/whitespace value is a no-op for the message step
    /// (the conversation is still created if it didn't exist).
    /// </param>
    /// <param name="seedIntoExisting">
    /// <c>true</c> (invite-CREATION callers, e.g. <c>CreatePendingInviteEndpoint</c>):
    /// append the message even if the two participants already have a conversation —
    /// matches the pre-#768 behavior where re-inviting an already-conversing contact
    /// with a personal message still delivered it.
    /// <c>false</c> (invite-ACCEPT callers, e.g. <c>AcceptClientInviteEndpoint</c>,
    /// <c>AcceptInvitationEndpoint</c>): seed only into a brand-new conversation, so
    /// re-accepting/re-processing the same invite can never duplicate the message —
    /// the invitee either already got it seeded at invite-creation time (conversation
    /// already exists) or this is the first time it's ever delivered (conversation is
    /// new).
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// Concurrency: a concurrent double-accept/double-invite race can make two
    /// requests both observe "no conversation yet" and both try to insert one. The
    /// unique index on (ProfessionalUserId, ClientUserId) lets only one insert win;
    /// the loser's <c>DbUpdateException</c> is caught internally, the now-existing
    /// conversation is re-queried, and the call is treated as the "already existed"
    /// branch (no seed, no duplicate, no 500).
    /// </remarks>
    Task<Conversation> GetOrSeedConversationAsync(
        Guid professionalUserId,
        Guid clientUserId,
        Guid senderUserId,
        string senderName,
        string? messageText,
        bool seedIntoExisting,
        CancellationToken ct);

    /// <summary>
    /// Appends a system-generated cooperation event (invite sent, request accepted, etc.) to the
    /// professional-client conversation, writing a single <see cref="ChatMessage"/> row with
    /// <see cref="ChatMessageKind.Event"/> and, when <paramref name="messageText"/> is non-blank,
    /// a second plain <see cref="ChatMessageKind.Text"/> row beneath it — both authored by
    /// <paramref name="actorUserId"/> and both committed in a single <c>SaveChangesAsync</c> call
    /// (the FIX 1 pattern from <see cref="GetOrSeedConversationAsync"/>) via the
    /// <see cref="Conversation"/> navigation property.
    /// </summary>
    /// <param name="professionalUserId">The professional participant's user id.</param>
    /// <param name="clientUserId">The client participant's user id.</param>
    /// <param name="actorUserId">
    /// The user who raised the event — must equal either <paramref name="professionalUserId"/>
    /// or <paramref name="clientUserId"/>. Becomes the written row(s)' <c>SenderUserId</c>; no
    /// separate actor-name payload is stored.
    /// </param>
    /// <param name="eventType">The cooperation event type.</param>
    /// <param name="sourceId">
    /// The public id of the domain row that raised the event (a <c>PendingInvite.PublicId</c> or
    /// a <c>ClientRequest.PublicId</c>), or null for a source with no stable id (e.g. a
    /// token-only invite). Paired with <paramref name="eventType"/> and the conversation to
    /// deduplicate a re-processed event.
    /// </param>
    /// <param name="messageText">
    /// An optional free-text message (an invite's personal note, an accept-time statement) to
    /// seed beneath the event row. A null/whitespace value seeds the event only.
    /// </param>
    /// <param name="createConversationIfMissing">
    /// <c>true</c>: get-or-create the conversation, matching <see cref="GetOrSeedConversationAsync"/>'s
    /// behavior. <c>false</c>: only append when a conversation already exists — a neutral
    /// "withdrawn" event must never create a thread just to announce there is nothing to see.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="pushCompanionText">
    /// <c>true</c> sends the companion text (not the event row) as a push to an offline recipient.
    /// Optional after <paramref name="ct"/> by design; only the accept/reject-request flows set it.
    /// </param>
    /// <remarks>
    /// Idempotency: a re-processed event for the same <paramref name="eventType"/> +
    /// <paramref name="sourceId"/> is a no-op — a pre-check catches the common case, and the
    /// partial unique index on (ConversationId, EventType, EventSourceId) backstops a genuine race.
    /// </remarks>
    Task AppendCooperationEventAsync(
        Guid professionalUserId,
        Guid clientUserId,
        Guid actorUserId,
        ChatEventType eventType,
        Guid? sourceId,
        string? messageText,
        bool createConversationIfMissing,
        CancellationToken ct,
        bool pushCompanionText = false);
}
