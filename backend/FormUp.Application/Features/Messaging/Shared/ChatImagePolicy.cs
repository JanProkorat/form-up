using FormUp.Application.Domain.Services;

namespace FormUp.Application.Features.Messaging.Shared;

/// <summary>
/// Chat-image-specific policy shared by <c>GenerateChatImageUploadUrlEndpoint</c> and
/// <c>SendMessageEndpoint</c>: the narrower jpeg/png/webp allowlist (excluding the shared
/// <see cref="Infrastructure.Services.ImageUploadService"/>'s heic/heif, which browsers cannot
/// render inline), magic-byte signature verification of the staged bytes, and the deterministic
/// container-path builders both actions use to agree on the same object key without either one
/// trusting a client-supplied path or URL.
/// </summary>
public static class ChatImagePolicy
{
    /// <summary>
    /// Container-path prefix for a staged (not-yet-sent) chat image upload. Must match
    /// <c>ImageUploadService.ScopeToPrefix(ImageUploadScope.ChatUpload)</c> — see the comment
    /// there.
    /// </summary>
    public const string StagingPrefix = "chat-uploads";

    /// <summary>Container-path prefix for a sent chat image's permanent object.</summary>
    public const string FinalPrefix = "chat";

    /// <summary>
    /// Content types accepted for a chat image attachment — narrower than
    /// <see cref="Infrastructure.Services.ImageUploadService"/>'s shared allowlist (excludes
    /// heic/heif).
    /// </summary>
    public static readonly IReadOnlyCollection<string> AllowedContentTypes = ["image/jpeg", "image/png", "image/webp"];

    /// <summary>
    /// Builds the scope-relative sub-path passed to <c>IImageUploadService.GenerateUploadUrlAsync</c>
    /// when minting the upload URL — <see cref="StagingPrefix"/> is prepended by that service, not
    /// here.
    /// </summary>
    public static string BuildStagingSubPath(Guid conversationId, Guid callerUserId, Guid uploadId) =>
        $"{conversationId}/{callerUserId}/{uploadId}";

    /// <summary>
    /// Builds the full container path of a staged upload — reconstructed identically at
    /// upload-url time and at send time from (conversation, caller, uploadId) alone, so the
    /// server never has to trust a client-supplied path or URL. Used for direct
    /// <see cref="Domain.Interfaces.IBlobStorageService"/> calls (download, delete) that bypass
    /// <c>IImageUploadService</c>.
    /// </summary>
    public static string BuildStagingContainerPath(Guid conversationId, Guid callerUserId, Guid uploadId) =>
        $"{StagingPrefix}/{BuildStagingSubPath(conversationId, callerUserId, uploadId)}";

    /// <summary>
    /// Builds the permanent, final blob key for a sent chat image — one per message, so a resend
    /// or a duplicate upload never collides with another message's image.
    /// </summary>
    public static string BuildFinalContainerPath(Guid conversationId, Guid messageId, string extension) =>
        $"{FinalPrefix}/{conversationId}/{messageId}.{extension}";

    /// <summary>Forwards to <see cref="ImageSignatureSniffer.SniffContentType"/>.</summary>
    public static string? SniffContentType(byte[] data) => ImageSignatureSniffer.SniffContentType(data);

    /// <summary>Forwards to <see cref="ImageSignatureSniffer.ExtensionFor"/>.</summary>
    public static string ExtensionFor(string sniffedContentType) => ImageSignatureSniffer.ExtensionFor(sniffedContentType);
}
