using System.Security.Claims;
using FastEndpoints;
using FluentValidation;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Features.Messaging.Shared;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.Messaging.GenerateChatImageUploadUrl;

/// <summary>
/// Generates a pre-signed URL for the caller to upload a chat image attachment directly to blob
/// storage, staged under <see cref="ChatImagePolicy.StagingPrefix"/>. <c>SendMessageEndpoint</c>
/// later rebuilds this exact container path from (conversation, caller, uploadId) — the client
/// never submits a blob URL or path.
/// </summary>
/// <param name="db">Relational database context — conversation participant lookup.</param>
/// <param name="imageUpload">Image upload service — validates size, then issues the signed URL.</param>
public class GenerateChatImageUploadUrlEndpoint(
    IApplicationDbContext db,
    IImageUploadService imageUpload)
    : Endpoint<GenerateChatImageUploadUrlRequest, GenerateChatImageUploadUrlResponse>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post("/conversations/{ConversationId}/messages/image-upload-url");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist, AppRoles.Client);
        Summary(s =>
        {
            s.Summary = "Generate a chat image upload URL";
            s.Description = "Returns a time-limited pre-signed URL for direct upload of a chat "
                + "image attachment, together with the uploadId to reference from POST "
                + "/conversations/{ConversationId}/messages.";
            s.Responses[StatusCodes.Status200OK] = "Upload URL and uploadId";
            s.Responses[StatusCodes.Status403Forbidden] = "CONVERSATION_LOCKED: professional caller in an invite-only thread; SUBSCRIPTION_REQUIRED: coach whose subscription does not include messaging";
            s.Responses[StatusCodes.Status404NotFound] = "Conversation not found, or caller is not a participant";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(GenerateChatImageUploadUrlRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);

        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var callerUserId = Guid.Parse(userId);

        var conversation = await db.Conversations
            .AsNoTracking()
            .FirstOrDefaultAsync(c =>
                c.PublicId == req.ConversationId &&
                (c.ProfessionalUserId == callerUserId || c.ClientUserId == callerUserId), ct);

        if (conversation is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        if (conversation.ProfessionalUserId == callerUserId)
        {
            if (await this.RespondIfFeatureNotEntitledAsync(
                    db, callerUserId, entitlements => entitlements.CanMessage,
                    "The current subscription does not include messaging.", ct))
            {
                return;
            }

            var access = await this.RequireProfessionalAccessOrRespondAsync(
                db, callerUserId, conversation.ClientUserId, ct);

            if (access is null)
            {
                return;
            }
        }

        var uploadId = Guid.NewGuid();
        var subPath = ChatImagePolicy.BuildStagingSubPath(req.ConversationId, callerUserId, uploadId);

        var result = await imageUpload.GenerateUploadUrlAsync(
            ImageUploadScope.ChatUpload, subPath, req.ContentType, req.SizeBytes, ct);

        await Send.OkAsync(new GenerateChatImageUploadUrlResponse
        {
            UploadUrl = result.UploadUrl,
            UploadId = uploadId,
        }, ct);
    }
}

/// <summary>Request for generating a chat image upload URL.</summary>
public class GenerateChatImageUploadUrlRequest
{
    public Guid ConversationId { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
}

/// <summary>Validates the <see cref="GenerateChatImageUploadUrlRequest"/>.</summary>
public class GenerateChatImageUploadUrlValidator : Validator<GenerateChatImageUploadUrlRequest>
{
    /// <summary>Initializes validation rules for a chat image upload-url request.</summary>
    public GenerateChatImageUploadUrlValidator()
    {
        RuleFor(x => x.ContentType)
            .Must(contentType => ChatImagePolicy.AllowedContentTypes.Contains(contentType))
            .WithErrorCode(ErrorCodes.InvalidImageContentType)
            .WithMessage($"Content type must be one of: {string.Join(", ", ChatImagePolicy.AllowedContentTypes)}.");

        RuleFor(x => x.SizeBytes)
            .Must(size => size > 0 && size <= ImageUploadService.MaxImageSizeBytes)
            .WithErrorCode(ErrorCodes.ImageTooLarge)
            .WithMessage($"Image size must be between 1 and {ImageUploadService.MaxImageSizeBytes} bytes.");
    }
}

/// <summary>Response for a chat image upload-url request.</summary>
public class GenerateChatImageUploadUrlResponse
{
    /// <summary>The pre-signed URL the client should PUT the image bytes to.</summary>
    public string UploadUrl { get; set; } = string.Empty;

    /// <summary>
    /// The identifier to pass as <c>SendMessageRequest.ImageUploadId</c> once the PUT completes.
    /// </summary>
    public Guid UploadId { get; set; }
}
