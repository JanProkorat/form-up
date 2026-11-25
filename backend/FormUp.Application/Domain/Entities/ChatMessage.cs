using System.ComponentModel.DataAnnotations;
using FormUp.Application.Domain.Common;
using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Domain.Entities;

/// <summary>
/// A single message within a conversation.
/// </summary>
public class ChatMessage : PublicTimestampableEntity
{
    /// <summary>
    /// The maximum length of <see cref="Text"/>. Single source of truth for the column's storage
    /// limit — referenced by <c>BroadcastMessageValidator</c> and its endpoint.
    /// </summary>
    public const int MaxTextLength = 4000;

    /// <summary>
    /// Database name of the unique index behind <see cref="ClientMessageId"/>; the send race
    /// handler matches the Postgres constraint name against it.
    /// </summary>
    public const string ClientMessageIdIndexName = "ix_chat_messages_client_message_id";

    /// <summary>
    /// The conversation this message belongs to.
    /// </summary>
    public long ConversationId { get; set; }

    /// <summary>
    /// The user who sent this message. For an <see cref="Kind"/> of
    /// <see cref="ChatMessageKind.Event"/>, this is the event's actor.
    /// </summary>
    public Guid SenderUserId { get; set; }

    /// <summary>
    /// Discriminates a plain message from a system-generated cooperation event.
    /// </summary>
    public ChatMessageKind Kind { get; set; } = ChatMessageKind.Text;

    /// <summary>
    /// The cooperation event type, when <see cref="Kind"/> is
    /// <see cref="ChatMessageKind.Event"/>. Null for a plain text message.
    /// </summary>
    public ChatEventType? EventType { get; set; }

    /// <summary>
    /// The public id of the domain row that raised this event (a
    /// <c>PendingInvite.PublicId</c> or a <c>ClientRequest.PublicId</c>), used to
    /// deduplicate a re-processed event via the partial unique index on
    /// (ConversationId, EventType, EventSourceId). Null for a plain text message.
    /// </summary>
    public Guid? EventSourceId { get; set; }

    /// <summary>
    /// The message text content. For an event row, this is a fallback line
    /// rendered in the client's language at write time
    /// (<c>Infrastructure.Services.ChatEventTemplates</c>).
    /// </summary>
    [MaxLength(MaxTextLength)]
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Whether the recipient has read this message.
    /// </summary>
    public bool IsRead { get; set; }

    /// <summary>
    /// The permanent blob storage key of this message's image attachment
    /// (<c>chat/{conversationPublicId}/{messagePublicId}.{ext}</c>), or null when the message
    /// carries no image. Never rendered directly — always resolved through
    /// <c>IBlobStorageService.GenerateReadUrlAsync</c> before reaching a response.
    /// </summary>
    [MaxLength(1000)]
    public string? ImageBlobUrl { get; set; }

    /// <summary>
    /// The sniffed content type of the image attachment (<c>image/jpeg</c>, <c>image/png</c>, or
    /// <c>image/webp</c>) — the type actually detected from the file's magic bytes, not the
    /// client-declared one. Null when the message carries no image.
    /// </summary>
    [MaxLength(32)]
    public string? ImageContentType { get; set; }

    /// <summary>
    /// The image attachment's byte size, as measured on the staged object. Null when the message
    /// carries no image.
    /// </summary>
    public long? ImageSizeBytes { get; set; }

    /// <summary>
    /// The image attachment's width in pixels, as reported by the client. A layout hint only —
    /// never trusted for security. Null when the message carries no image.
    /// </summary>
    public int? ImageWidth { get; set; }

    /// <summary>
    /// The image attachment's height in pixels, as reported by the client. A layout hint only —
    /// never trusted for security. Null when the message carries no image.
    /// </summary>
    public int? ImageHeight { get; set; }

    /// <summary>
    /// Client-generated id that makes a send retry-safe. Unique per
    /// (ConversationId, SenderUserId) while set; cleared once the replay window has passed.
    /// Null for event rows and for sends that carried no id.
    /// </summary>
    public Guid? ClientMessageId { get; set; }

    /// <summary>
    /// Navigation property to the conversation.
    /// </summary>
    public Conversation Conversation { get; set; } = null!;

    /// <summary>
    /// Navigation property to the sender user.
    /// </summary>
    public ApplicationUser Sender { get; set; } = null!;
}
