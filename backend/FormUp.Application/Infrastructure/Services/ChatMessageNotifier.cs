using System.Globalization;
using System.Text;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;

namespace FormUp.Application.Infrastructure.Services;

/// <inheritdoc cref="IChatMessageNotifier"/>
/// <param name="realtimeNotifier">Sends the SignalR event.</param>
/// <param name="notificationService">Sends the push-only notification.</param>
/// <param name="presence">Tells whether the recipient has a live connection.</param>
/// <param name="logger">Logs a failed push.</param>
public class ChatMessageNotifier(
    IRealtimeNotifier realtimeNotifier,
    INotificationService notificationService,
    PresenceTracker presence,
    ILogger<ChatMessageNotifier> logger) : IChatMessageNotifier
{
    /// <summary>Maximum length of the message text in a push body, ellipsis included.</summary>
    public const int MaxPushTextLength = 140;

    private const string Ellipsis = "…";

    /// <inheritdoc />
    public async Task NotifyNewMessageAsync(
        ChatMessage message,
        Guid conversationPublicId,
        Guid recipientUserId,
        string senderName,
        string? imageUrl,
        bool sendPush,
        CancellationToken ct)
    {
        // kind/eventType are stringified explicitly: the SignalR JSON protocol does not share the
        // REST pipeline's JsonStringEnumConverter, so an enum would serialize as an integer.
        await realtimeNotifier.NotifyAsync(recipientUserId, "newmessage", new
        {
            conversationId = conversationPublicId,
            messageId = message.PublicId,
            senderId = message.SenderUserId,
            senderName,
            text = message.Text,
            timestamp = message.DateCreated,
            imageUrl,
            imageWidth = message.ImageWidth,
            imageHeight = message.ImageHeight,
            kind = message.Kind.ToString(),
            eventType = message.EventType?.ToString(),
        }, ct);

        if (!sendPush
            || message.Kind != ChatMessageKind.Text
            || recipientUserId == message.SenderUserId
            || presence.IsOnline(recipientUserId))
        {
            return;
        }

        await TrySendPushAsync(message, conversationPublicId, recipientUserId, senderName, ct);
    }

    /// <summary>
    /// Shortens <paramref name="text"/> to at most <see cref="MaxPushTextLength"/> characters,
    /// ellipsis included, cutting only between text elements so no surrogate pair or ZWJ emoji
    /// sequence is split.
    /// </summary>
    public static string TruncateForPush(string text)
    {
        if (text.Length <= MaxPushTextLength)
        {
            return text;
        }

        var budget = MaxPushTextLength - Ellipsis.Length;
        var kept = new StringBuilder();
        var elements = StringInfo.GetTextElementEnumerator(text);

        while (elements.MoveNext())
        {
            var element = (string)elements.Current;

            if (kept.Length + element.Length > budget)
            {
                break;
            }

            kept.Append(element);
        }

        return kept.ToString().TrimEnd() + Ellipsis;
    }

    private async Task TrySendPushAsync(
        ChatMessage message,
        Guid conversationPublicId,
        Guid recipientUserId,
        string senderName,
        CancellationToken ct)
    {
        var parameters = new Dictionary<string, string>
        {
            ["senderName"] = senderName,
            ["conversationId"] = conversationPublicId.ToString(),
        };

        string? variant = null;

        if (string.IsNullOrWhiteSpace(message.Text))
        {
            variant = NotificationTemplates.NewMessageImageOnly;
        }
        else
        {
            // Only the truncated text is passed: every parameter is also copied into the push data.
            parameters[NotificationTemplates.MessageTextKey] = TruncateForPush(message.Text);
        }

        try
        {
            await notificationService.SendPushOnlyAsync(
                recipientUserId, NotificationType.NewMessage, parameters, variant, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "Failed to send the new-message push for user {UserId} in conversation {ConversationId}",
                recipientUserId, conversationPublicId);
        }
    }
}
