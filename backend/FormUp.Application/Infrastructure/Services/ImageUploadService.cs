using FastEndpoints;
using FluentValidation.Results;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;

namespace FormUp.Application.Infrastructure.Services;

/// <summary>
/// Validates image uploads and delegates signed-URL generation to
/// <see cref="IBlobStorageService"/>.
///
/// <para><b>Allowed content types:</b> <c>image/jpeg</c>, <c>image/png</c>, <c>image/webp</c>, <c>image/heic</c>, <c>image/heif</c>.</para>
/// <para><b>Maximum file size:</b> <see cref="MaxImageSizeBytes"/> (5 MiB).</para>
///
/// <para><b>Blob-path conventions by scope:</b></para>
/// <list type="bullet">
///   <item><description><see cref="ImageUploadScope.Avatar"/>    — <c>avatars/{userId}-{guid:N}.{ext}</c> or <c>avatars/prof-{profileId}-{guid:N}.{ext}</c></description></item>
///   <item><description><see cref="ImageUploadScope.Food"/>      — <c>foods/{foodId}.{ext}</c></description></item>
///   <item><description><see cref="ImageUploadScope.Recipe"/>    — <c>recipes/{recipeId}/{slot}.{ext}</c></description></item>
///   <item><description><see cref="ImageUploadScope.PlanPhoto"/> — <c>plan-photos/{planId}/{photoId}.{ext}</c></description></item>
///   <item><description><see cref="ImageUploadScope.Diary"/>     — <c>diary/{diaryId}/{photoId}.{ext}</c></description></item>
/// </list>
/// </summary>
public class ImageUploadService(IBlobStorageService blobStorage) : IImageUploadService
{
    /// <summary>Maximum allowed image file size: 5 MiB.</summary>
    public const long MaxImageSizeBytes = 5L * 1024 * 1024;

    private static readonly IReadOnlyDictionary<string, string> AllowedContentTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["image/jpeg"] = "jpg",
            ["image/png"] = "png",
            ["image/webp"] = "webp",
            ["image/heic"] = "heic",
            ["image/heif"] = "heif",
        };

    /// <summary>Pre-signed URL validity window for image uploads.</summary>
    private static readonly TimeSpan UploadUrlExpiry = TimeSpan.FromMinutes(15);

    /// <inheritdoc />
    /// <remarks>
    /// Blob-path conventions by scope:
    /// <list type="bullet">
    ///   <item><description>Avatar    — <c>avatars/{userId}-{guid:N}.{ext}</c> or <c>avatars/prof-{profileId}-{guid:N}.{ext}</c></description></item>
    ///   <item><description>Food      — <c>foods/{foodId}.{ext}</c></description></item>
    ///   <item><description>Recipe    — <c>recipes/{recipeId}/{slot}.{ext}</c></description></item>
    ///   <item><description>PlanPhoto — <c>plan-photos/{planId}/{photoId}.{ext}</c></description></item>
    ///   <item><description>Diary     — <c>diary/{diaryId}/{photoId}.{ext}</c></description></item>
    /// </list>
    /// </remarks>
    /// <exception cref="ValidationFailureException">
    /// Also thrown with error code <c>INVALID_IMAGE_SUB_PATH</c> when <paramref name="subPath"/>
    /// is null, empty, or contains path-traversal sequences (<c>..</c>, <c>\</c>, or a leading <c>/</c>).
    /// </exception>
    public async Task<BlobUploadUrl> GenerateUploadUrlAsync(
        ImageUploadScope scope,
        string subPath,
        string contentType,
        long sizeBytes,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(subPath) ||
            subPath.Contains("..", StringComparison.Ordinal) ||
            subPath.StartsWith('/') ||
            subPath.Contains('\\', StringComparison.Ordinal))
        {
            var failures = new List<ValidationFailure>
            {
                new(nameof(subPath), "Invalid blob sub-path.")
                {
                    ErrorCode = ErrorCodes.InvalidImageSubPath
                }
            };
            throw new ValidationFailureException(failures, "Invalid blob sub-path.");
        }

        if (!AllowedContentTypes.TryGetValue(contentType, out _))
        {
            var allowed = string.Join(", ", AllowedContentTypes.Keys);
            var failures = new List<ValidationFailure>
            {
                new("contentType", $"Content type must be one of: {allowed}.")
                {
                    ErrorCode = ErrorCodes.InvalidImageContentType
                }
            };
            throw new ValidationFailureException(failures, "Invalid image content type.");
        }

        if (sizeBytes > MaxImageSizeBytes)
        {
            var limitMb = MaxImageSizeBytes / (1024 * 1024);
            var failures = new List<ValidationFailure>
            {
                new("sizeBytes", $"Image must not exceed {limitMb} MB.")
                {
                    ErrorCode = ErrorCodes.ImageTooLarge
                }
            };
            throw new ValidationFailureException(failures, "Image exceeds maximum allowed size.");
        }

        var prefix = ScopeToPrefix(scope);
        var containerPath = $"{prefix}/{subPath}";

        return await blobStorage.GenerateUploadUrlAsync(
            containerPath,
            contentType,
            UploadUrlExpiry,
            ct);
    }

    /// <inheritdoc />
    public bool IsValidBlobUrlForSubPath(ImageUploadScope scope, string subPathPrefix, string blobUrl)
    {
        if (string.IsNullOrWhiteSpace(blobUrl) || string.IsNullOrWhiteSpace(subPathPrefix))
        {
            return false;
        }

        var prefix = ScopeToPrefix(scope);

        // Try every allowed extension — the caller doesn't know which one was used at
        // upload time, only the identity-bound prefix (e.g. "{userId}").
        foreach (var extension in AllowedContentTypes.Values)
        {
            var containerPath = $"{prefix}/{subPathPrefix}.{extension}";
            var expectedUrl = blobStorage.BuildPublicUrl(containerPath);

            if (string.Equals(blobUrl, expectedUrl, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public bool IsValidBlobUrlUnderSubPath(ImageUploadScope scope, string subPathPrefix, string blobUrl)
    {
        if (string.IsNullOrWhiteSpace(blobUrl) || string.IsNullOrWhiteSpace(subPathPrefix))
        {
            return false;
        }

        var folderUrl = blobStorage.BuildPublicUrl($"{ScopeToPrefix(scope)}/{subPathPrefix}/");

        if (!blobUrl.StartsWith(folderUrl, StringComparison.Ordinal))
        {
            return false;
        }

        var fileName = blobUrl[folderUrl.Length..];

        if (fileName.Length == 0 || fileName.IndexOfAny(['/', '\\', '%', '?', '#']) >= 0 ||
            fileName.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var extension in AllowedContentTypes.Values)
        {
            var suffix = $".{extension}";

            if (fileName.Length > suffix.Length && fileName.EndsWith(suffix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string ScopeToPrefix(ImageUploadScope scope) => scope switch
    {
        ImageUploadScope.Avatar     => "avatars",
        ImageUploadScope.Food       => "foods",
        ImageUploadScope.Recipe     => "recipes",
        ImageUploadScope.PlanPhoto  => "plan-photos",
        ImageUploadScope.Diary      => "diary",
        // Must match FormUp.Application.Features.Messaging.Shared.ChatImagePolicy.StagingPrefix —
        // that class rebuilds this same container path directly against IBlobStorageService at send
        // time (download + delete), bypassing this service, so the two literals cannot drift silently.
        ImageUploadScope.ChatUpload => "chat-uploads",
        // Must match QuestionnaireFilePolicy.StagingPrefix for the same reason as ChatUpload above.
        ImageUploadScope.QuestionnaireUpload => "questionnaire-uploads",
        _                           => throw new ArgumentOutOfRangeException(nameof(scope), scope, null)
    };
}
