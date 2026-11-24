using FormUp.Application.Domain.Entities;

namespace FormUp.Application.Domain.Interfaces;

/// <summary>
/// The one seam through which a stored <see cref="ChatMessage"/> reaches its recipient:
/// the "newmessage" SignalR event, plus a push when the recipient is offline.
/// </summary>
public interface IChatMessageNotifier
{
    /// <summary>
    /// Raises "newmessage" for the recipient and, when <paramref name="sendPush"/> is set, the
    /// message is a text message and the recipient has no live SignalR connection, sends a
    /// push-only notification. A push failure is logged and never thrown.
    /// </summary>
    /// <param name="message">The saved message (event rows never push).</param>
    /// <param name="conversationPublicId">Public id of the conversation.</param>
    /// <param name="recipientUserId">The participant who did not author the message.</param>
    /// <param name="senderName">Display name of the author.</param>
    /// <param name="imageUrl">Signed read URL of the attachment, or null.</param>
    /// <param name="sendPush">False for callers that must stay SignalR-only.</param>
    /// <param name="ct">Cancellation token.</param>
    Task NotifyNewMessageAsync(
        ChatMessage message,
        Guid conversationPublicId,
        Guid recipientUserId,
        string senderName,
        string? imageUrl,
        bool sendPush,
        CancellationToken ct);
}
