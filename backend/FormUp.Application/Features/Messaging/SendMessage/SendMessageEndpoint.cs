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

namespace FormUp.Application.Features.Messaging.SendMessage;

/// <summary>
/// Sends a message in a conversation. Creates the conversation if it doesn't exist yet.
/// A message carries text, an image, or both — <see cref="SendMessageRequest.ImageUploadId"/>
/// references a staged upload from <c>GenerateChatImageUploadUrlEndpoint</c>; the server
/// rebuilds the staging path itself and never trusts a client-supplied blob URL.
/// </summary>
/// <param name="db">Relational database context.</param>
/// <param name="notifier">Realtime notifier — pushes the <c>newmessage</c> event.</param>
/// <param name="blobStorage">
/// Blob storage — downloads and sniffs a staged image before promoting it to a permanent
/// object, and signs the final <c>ImageUrl</c> echoed back in the response.
/// </param>
/// <param name="notificationService">Creates the coach's in-app new-message notice.</param>
/// <param name="logger">Logger for the best-effort notice.</param>
public class SendMessageEndpoint(
    IApplicationDbContext db,
    IRealtimeNotifier notifier,
    IBlobStorageService blobStorage,
    INotificationService notificationService,
    ILogger<SendMessageEndpoint> logger) : Endpoint<SendMessageRequest, SendMessageResponse>
{
    public override void Configure()
    {
        Post("/conversations/{ConversationId}/messages");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist, AppRoles.Client);
        Summary(s =>
        {
            s.Summary = "Send a message";
            s.Description = "Sends a text message, an image, or both, in a conversation.";
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

        string? imageBlobUrl = null;
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

        // Update conversation preview
        conversation.LastMessageText = message.Text.Length > 300
            ? message.Text[..300]
            : message.Text;
        conversation.LastMessageAt = DateTime.UtcNow;
        conversation.LastMessageSenderId = userGuid;
        conversation.LastMessageHasImage = hasImage;
        conversation.LastMessageEventType = null;

        await db.SaveChangesAsync(ct);

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

        // A stored ImageBlobUrl is never sent to a client directly — always a fresh, short-lived
        // signed URL, and never fall back to the stored value on a signing failure.
        var imageUrl = hasImage
            ? await blobStorage.GenerateReadUrlAsync(imageBlobUrl, ct) ?? string.Empty
            : null;

        // Notify recipient via SignalR
        await notifier.NotifyAsync(recipientUserId, "newmessage", new
        {
            conversationId = conversation.PublicId,
            messageId = message.PublicId,
            senderId = userGuid,
            senderName,
            text = message.Text,
            timestamp = message.DateCreated,
            imageUrl,
            imageWidth = message.ImageWidth,
            imageHeight = message.ImageHeight,
        }, ct);

        // The in-app notice is for the coach only; a client reads the chat itself.
        if (!senderIsProfessional)
        {
            try
            {
                await notificationService.CreateAsync(
                    recipientUserId,
                    NotificationType.NewMessage,
                    new Dictionary<string, string>
                    {
                        ["clientName"] = senderName,
                        ["conversationId"] = conversation.PublicId.ToString(),
                    },
                    ct: ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "Failed to create the new-message notice for user {UserId} in conversation {ConversationId}",
                    recipientUserId, conversation.PublicId);
            }

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

        await Send.OkAsync(new SendMessageResponse
        {
            Id = message.PublicId,
            SenderId = message.SenderUserId,
            Text = message.Text,
            Timestamp = message.DateCreated,
            IsRead = message.IsRead,
            ImageUrl = imageUrl,
            ImageWidth = message.ImageWidth,
            ImageHeight = message.ImageHeight,
        }, ct);
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
}
