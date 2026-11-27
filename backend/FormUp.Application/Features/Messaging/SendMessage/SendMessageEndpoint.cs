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
using FormUp.Application.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FormUp.Application.Features.Messaging.SendMessage;

/// <summary>
/// Sends a message in a conversation. Creates the conversation if it doesn't exist yet.
/// A message carries text, an image, or both — <see cref="SendMessageRequest.ImageUploadId"/>
/// references a staged upload from <c>GenerateChatImageUploadUrlEndpoint</c>; the server
/// rebuilds the staging path itself and never trusts a client-supplied blob URL.
/// </summary>
/// <param name="db">Relational database context.</param>
/// <param name="notifier">Realtime notifier — pushes the <c>conversationunarchived</c> event.</param>
/// <param name="blobStorage">
/// Blob storage — downloads and sniffs a staged image before promoting it to a permanent
/// object, and signs the final <c>ImageUrl</c> echoed back in the response.
/// </param>
/// <param name="chatMessageNotifier">Raises the new-message event and the offline push.</param>
/// <param name="logger">Logger for the best-effort grouped email and staging cleanup.</param>
/// <param name="timeProvider">Clock for the replay window.</param>
public class SendMessageEndpoint(
    IApplicationDbContext db,
    IRealtimeNotifier notifier,
    IBlobStorageService blobStorage,
    IChatMessageNotifier chatMessageNotifier,
    ILogger<SendMessageEndpoint> logger,
    TimeProvider timeProvider) : Endpoint<SendMessageRequest, SendMessageResponse>
{
    /// <summary>
    /// How long a <see cref="SendMessageRequest.ClientMessageId"/> is remembered. A repeat inside the
    /// window returns the original message; after it the old id is released and the send is a new one.
    /// </summary>
    private static readonly TimeSpan ReplayWindow = TimeSpan.FromHours(24);

    public override void Configure()
    {
        Post("/conversations/{ConversationId}/messages");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist, AppRoles.Client);
        Summary(s =>
        {
            s.Summary = "Send a message";
            s.Description = "Sends a text message, an image, or both, in a conversation. "
                + "An optional ClientMessageId makes the send retry-safe: repeating it from the same sender in the "
                + "same conversation within 24 hours returns the original message (200) without creating, "
                + "notifying or unarchiving again. A ClientMessageId of Guid.Empty is rejected with 400.";
            s.Responses[StatusCodes.Status400BadRequest] = "Invalid request, or the staged image upload was not found";
            s.Responses[StatusCodes.Status403Forbidden] = "CONVERSATION_LOCKED: professional caller in an invite-only thread; SUBSCRIPTION_REQUIRED: coach whose subscription does not include messaging";
        });
    }

    public override async Task HandleAsync(SendMessageRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);
        if (userId is null) { await Send.UnauthorizedAsync(ct); return; }

        var userGuid = Guid.Parse(userId);

        // Verify user is a participant
        var conversation = await db.Conversations
            .FirstOrDefaultAsync(c =>
                c.PublicId == req.ConversationId &&
                (c.ProfessionalUserId == userGuid || c.ClientUserId == userGuid), ct);

        if (conversation is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var senderIsProfessional = conversation.ProfessionalUserId == userGuid;
        var professionalAccess = ProfessionalSendAccess.SendAndUnarchive;

        if (senderIsProfessional)
        {
            if (await this.RespondIfFeatureNotEntitledAsync(
                    db, userGuid, entitlements => entitlements.CanMessage,
                    "The current subscription does not include messaging.", ct))
            {
                return;
            }

            var access = await this.RequireProfessionalAccessOrRespondAsync(
                db, userGuid, conversation.ClientUserId, ct);

            if (access is null)
            {
                return;
            }

            professionalAccess = access.Value;
        }

        // A retry of an already-accepted send returns the original before any image work, so a
        // repeat never promotes a second blob, re-notifies or un-archives again.
        if (req.ClientMessageId is { } clientMessageId)
        {
            var original = await FindReplayableMessageAsync(conversation.Id, userGuid, clientMessageId, ct);

            if (original is not null)
            {
                await DiscardStagedUploadAsync(req.ConversationId, userGuid, req.ImageUploadId, ct);
                await Send.OkAsync(ToResponse(original, await ResolveImageUrlAsync(original.ImageBlobUrl, ct)), ct);
                return;
            }
        }

        string? imageBlobUrl = null;
        string? promotedPath = null;
        string? imageContentType = null;
        long? imageSizeBytes = null;
        Guid? messageId = null;

        if (req.ImageUploadId.HasValue)
        {
            var stagingPath = ChatImagePolicy.BuildStagingContainerPath(
                req.ConversationId, userGuid, req.ImageUploadId.Value);

            var staged = await blobStorage.DownloadAsync(stagingPath, ImageUploadService.MaxImageSizeBytes, ct);

            if (staged is null)
            {
                await this.SendProblemAsync(400, ErrorCodes.ChatImageUploadNotFound,
                    "No staged image upload found for this uploadId.", ct);
                return;
            }

            if (staged.Data is null)
            {
                // Stat-only result: the actual object exceeds the cap despite a compliant
                // declared size at upload-url time — a presigned PUT enforces neither.
                await blobStorage.DeleteAsync(stagingPath, ct);
                await this.SendProblemAsync(413, ErrorCodes.ImageTooLarge,
                    "Staged image exceeds the maximum allowed size.", ct);
                return;
            }

            var sniffedContentType = ChatImagePolicy.SniffContentType(staged.Data);
            if (sniffedContentType is null)
            {
                await blobStorage.DeleteAsync(stagingPath, ct);
                await this.SendProblemAsync(400, ErrorCodes.InvalidImageContentType,
                    "Staged file is not a recognized jpeg/png/webp image.", ct);
                return;
            }

            // Assigned up front (not left to ApplyTimestamps) so the final blob key can be built
            // before the ChatMessage row exists.
            messageId = Guid.NewGuid();
            var finalPath = ChatImagePolicy.BuildFinalContainerPath(
                req.ConversationId, messageId.Value, ChatImagePolicy.ExtensionFor(sniffedContentType));

            await blobStorage.UploadAsync(finalPath, staged.Data, sniffedContentType, ct);
            promotedPath = finalPath;
            await blobStorage.DeleteAsync(stagingPath, ct);

            // Store the full public-URL form, not the bare container path — every other caller
            // of GenerateReadUrlAsync (e.g. GetPlanPhotosEndpoint) stores BuildPublicUrl's output,
            // and GenerateReadUrlAsync's TryExtractContainerPath reverses exactly that form. A
            // bare path fails the prefix match and GenerateReadUrlAsync fails closed to
            // string.Empty for every chat image (see MinioBlobStorageServiceTests for the
            // round-trip contract this depends on).
            imageBlobUrl = blobStorage.BuildPublicUrl(finalPath);
            imageContentType = sniffedContentType;
            imageSizeBytes = staged.SizeBytes;
        }

        var hasImage = imageBlobUrl is not null;

        var message = new ChatMessage
        {
            PublicId = messageId ?? Guid.Empty,
            ConversationId = conversation.Id,
            SenderUserId = userGuid,
            ClientMessageId = req.ClientMessageId,
            // An explicit JSON null overrides the string.Empty default; image-only sends allow it.
            Text = (req.Text ?? string.Empty).Trim(),
            IsRead = false,
            // SendMessageRequest carries no kind/eventType field — a client can never write
            // anything but a plain text message through this endpoint.
            Kind = ChatMessageKind.Text,
            ImageBlobUrl = imageBlobUrl,
            ImageContentType = imageContentType,
            ImageSizeBytes = imageSizeBytes,
            ImageWidth = hasImage ? req.ImageWidth : null,
            ImageHeight = hasImage ? req.ImageHeight : null,
        };

        db.ChatMessages.Add(message);

        var previousPreview = (
            Text: conversation.LastMessageText,
            At: conversation.LastMessageAt,
            SenderId: conversation.LastMessageSenderId,
            HasImage: conversation.LastMessageHasImage,
            EventType: conversation.LastMessageEventType);

        // Update conversation preview
        conversation.LastMessageText = message.Text.Length > 300
            ? message.Text[..300]
            : message.Text;
        conversation.LastMessageAt = DateTime.UtcNow;
        conversation.LastMessageSenderId = userGuid;
        conversation.LastMessageHasImage = hasImage;
        conversation.LastMessageEventType = null;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (req.ClientMessageId.HasValue && IsClientMessageIdViolation(ex))
        {
            // A concurrent send with the same id won the unique index: answer as a replay of it.
            var winner = await RecoverFromSendRaceAsync(
                message, conversation, previousPreview, promotedPath, userGuid, req.ClientMessageId.Value, ct);

            if (winner is null)
            {
                throw;
            }

            await DiscardStagedUploadAsync(req.ConversationId, userGuid, req.ImageUploadId, ct);
            await Send.OkAsync(ToResponse(winner, await ResolveImageUrlAsync(winner.ImageBlobUrl, ct)), ct);
            return;
        }

        // Get sender name for notification
        var sender = await db.Users.AsNoTracking()
            .Where(u => u.Id == userGuid)
            .Select(u => new { u.FirstName, u.LastName })
            .FirstAsync(ct);

        var senderName = $"{sender.FirstName} {sender.LastName}";

        // Determine recipient
        var recipientUserId = conversation.ProfessionalUserId == userGuid
            ? conversation.ClientUserId
            : conversation.ProfessionalUserId;

        // Auto-unarchive for recipient (unless former collaboration)
        bool autoUnarchived = false;
        if (!conversation.IsFormer)
        {
            // Only a live link may re-surface the thread for the client; an ended-link or
            // pending-request professional may send but must not un-archive.
            if (senderIsProfessional)
            {
                if (professionalAccess == ProfessionalSendAccess.SendAndUnarchive && conversation.ArchivedByClientAt != null)
                {
                    conversation.ArchivedByClientAt = null;
                    autoUnarchived = true;
                }
            }
            else if (conversation.ArchivedByProfessionalAt != null)
            {
                conversation.ArchivedByProfessionalAt = null;
                autoUnarchived = true;
            }

            if (autoUnarchived)
            {
                await db.SaveChangesAsync(ct);
                await notifier.NotifyAsync(recipientUserId, "conversationunarchived", new
                {
                    conversationId = conversation.PublicId,
                    isFormer = false,
                }, ct);
            }
        }

        var imageUrl = await ResolveImageUrlAsync(imageBlobUrl, ct);

        // SignalR event plus an offline push for either participant. No in-app notice row:
        // unread chat shows on the Clients list and Inbox instead.
        await chatMessageNotifier.NotifyNewMessageAsync(
            message, conversation.PublicId, recipientUserId, senderName, imageUrl, sendPush: true, ct);

        // The grouped email is for the coach only.
        if (!senderIsProfessional)
        {
            // Opens or extends the burst that the grouped email is sent for; the scheduler sends it later.
            try
            {
                await db.UpsertPendingNotificationEmailAsync(
                    recipientUserId, NotificationEvent.NewMessage, conversation.PublicId, message.DateCreated, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "Failed to queue the grouped new-message email for user {UserId} in conversation {ConversationId}",
                    recipientUserId, conversation.PublicId);
            }
        }

        await Send.OkAsync(ToResponse(message, imageUrl), ct);
    }

    private static bool IsClientMessageIdViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pgEx
        && pgEx.ConstraintName == ChatMessage.ClientMessageIdIndexName;

    private static SendMessageResponse ToResponse(ChatMessage message, string? imageUrl) => new()
    {
        Id = message.PublicId,
        SenderId = message.SenderUserId,
        Text = message.Text,
        Timestamp = message.DateCreated,
        IsRead = message.IsRead,
        ImageUrl = imageUrl,
        ImageWidth = message.ImageWidth,
        ImageHeight = message.ImageHeight,
        ClientMessageId = message.ClientMessageId,
    };

    // A stored ImageBlobUrl is never sent to a client directly — always a fresh, short-lived
    // signed URL, and never fall back to the stored value on a signing failure.
    private async Task<string?> ResolveImageUrlAsync(string? imageBlobUrl, CancellationToken ct) =>
        imageBlobUrl is null
            ? null
            : await blobStorage.GenerateReadUrlAsync(imageBlobUrl, ct) ?? string.Empty;

    /// <summary>
    /// Returns the caller's earlier message with this client id when it is still inside the replay
    /// window. An older one releases its id (so the unique index admits the new send) and yields null.
    /// </summary>
    private async Task<ChatMessage?> FindReplayableMessageAsync(
        long conversationId, Guid senderUserId, Guid clientMessageId, CancellationToken ct)
    {
        var existing = await db.ChatMessages.FirstOrDefaultAsync(m =>
            m.ConversationId == conversationId &&
            m.SenderUserId == senderUserId &&
            m.ClientMessageId == clientMessageId, ct);

        if (existing is null)
        {
            return null;
        }

        if (existing.DateCreated >= timeProvider.GetUtcNow().UtcDateTime - ReplayWindow)
        {
            return existing;
        }

        existing.ClientMessageId = null;
        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>
    /// Undoes the losing insert of a concurrent send with the same client id and loads the winner.
    /// Returns null when the winner cannot be found, so the caller rethrows.
    /// </summary>
    private async Task<ChatMessage?> RecoverFromSendRaceAsync(
        ChatMessage losingMessage,
        Conversation conversation,
        (string? Text, DateTime? At, Guid? SenderId, bool HasImage, ChatEventType? EventType) previousPreview,
        string? promotedPath,
        Guid senderUserId,
        Guid clientMessageId,
        CancellationToken ct)
    {
        // Remove() on an Added entity only untracks it; otherwise it would re-INSERT on the next save.
        db.ChatMessages.Remove(losingMessage);

        conversation.LastMessageText = previousPreview.Text;
        conversation.LastMessageAt = previousPreview.At;
        conversation.LastMessageSenderId = previousPreview.SenderId;
        conversation.LastMessageHasImage = previousPreview.HasImage;
        conversation.LastMessageEventType = previousPreview.EventType;

        if (promotedPath is not null)
        {
            try
            {
                await blobStorage.DeleteAsync(promotedPath, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "Failed to delete the image of a losing duplicate send in conversation {ConversationId}",
                    conversation.PublicId);
            }
        }

        return await db.ChatMessages.AsNoTracking().FirstOrDefaultAsync(m =>
            m.ConversationId == conversation.Id &&
            m.SenderUserId == senderUserId &&
            m.ClientMessageId == clientMessageId, ct);
    }

    /// <summary>Best-effort delete of the caller's own staging object that a replay will never promote.</summary>
    private async Task DiscardStagedUploadAsync(
        Guid conversationPublicId, Guid userId, Guid? imageUploadId, CancellationToken ct)
    {
        if (!imageUploadId.HasValue)
        {
            return;
        }

        try
        {
            await blobStorage.DeleteAsync(
                ChatImagePolicy.BuildStagingContainerPath(conversationPublicId, userId, imageUploadId.Value), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to delete an unused staged chat image for user {UserId}", userId);
        }
    }
}

public class SendMessageRequest
{
    public Guid ConversationId { get; set; }
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// References a staged upload from <c>GenerateChatImageUploadUrlEndpoint</c>. Optional —
    /// when absent, the message is text-only.
    /// </summary>
    public Guid? ImageUploadId { get; set; }

    /// <summary>Client-reported layout hint. Never trusted for security; ignored unless <see cref="ImageUploadId"/> is set.</summary>
    public int? ImageWidth { get; set; }

    /// <summary>Client-reported layout hint. Never trusted for security; ignored unless <see cref="ImageUploadId"/> is set.</summary>
    public int? ImageHeight { get; set; }

    /// <summary>
    /// Optional client-generated id that makes a retry safe. Reuse it only while the payload is
    /// unchanged; a repeat from the same sender in the same conversation within 24 hours returns
    /// the original message instead of creating another.
    /// </summary>
    public Guid? ClientMessageId { get; set; }
}

public class SendMessageValidator : FastEndpoints.Validator<SendMessageRequest>
{
    public SendMessageValidator()
    {
        RuleFor(x => x.Text)
            .MaximumLength(ChatMessage.MaxTextLength)
            .WithMessage($"Message must be at most {ChatMessage.MaxTextLength} characters.");

        RuleFor(x => x)
            .Must(x => !string.IsNullOrWhiteSpace(x.Text) || x.ImageUploadId.HasValue)
            .WithMessage("Message must include text or an image.")
            .OverridePropertyName(nameof(SendMessageRequest.Text));

        RuleFor(x => x.ClientMessageId)
            .NotEqual(Guid.Empty)
            .When(x => x.ClientMessageId.HasValue)
            .WithMessage("ClientMessageId must not be an empty GUID.");

        RuleFor(x => x.ImageWidth)
            .InclusiveBetween(1, 20000)
            .When(x => x.ImageWidth.HasValue)
            .WithMessage("Image width must be between 1 and 20000 pixels.");

        RuleFor(x => x.ImageHeight)
            .InclusiveBetween(1, 20000)
            .When(x => x.ImageHeight.HasValue)
            .WithMessage("Image height must be between 1 and 20000 pixels.");
    }
}

public class SendMessageResponse
{
    public Guid Id { get; set; }
    public Guid SenderId { get; set; }
    public string Text { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public bool IsRead { get; set; }

    /// <summary>Short-lived signed URL for the image attachment, or null for a text-only message.</summary>
    public string? ImageUrl { get; set; }

    public int? ImageWidth { get; set; }
    public int? ImageHeight { get; set; }

    /// <summary>Echo of the request's <c>ClientMessageId</c>; null when the send carried none.</summary>
    public Guid? ClientMessageId { get; set; }
}
