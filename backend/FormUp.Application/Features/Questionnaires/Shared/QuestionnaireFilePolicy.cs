using FormUp.Application.Domain.Interfaces;

namespace FormUp.Application.Features.Questionnaires.Shared;

/// <summary>
/// Policy for questionnaire file_upload answers, shared by the upload-url endpoint and
/// <c>UpdateResponseEndpoint</c>: the jpeg/png/webp allowlist and the deterministic container-path
/// builders both actions use to agree on one object key without trusting a client-supplied path.
/// Neither prefix is covered by the bucket's public-read grant — reads go through signed URLs.
/// </summary>
public static class QuestionnaireFilePolicy
{
    /// <summary>
    /// Container-path prefix for a staged upload. Must match
    /// <c>ImageUploadService.ScopeToPrefix(ImageUploadScope.QuestionnaireUpload)</c>.
    /// </summary>
    public const string StagingPrefix = "questionnaire-uploads";

    /// <summary>Container-path prefix for a saved answer's permanent object.</summary>
    public const string FinalPrefix = "questionnaire-files";

    /// <summary>Content types accepted for a file_upload answer.</summary>
    public static readonly IReadOnlyCollection<string> AllowedContentTypes = ["image/jpeg", "image/png", "image/webp"];

    private static readonly string[] FinalExtensions = ["jpg", "png", "webp"];

    /// <summary>Scope-relative sub-path passed to <c>IImageUploadService.GenerateUploadUrlAsync</c>.</summary>
    public static string BuildStagingSubPath(Guid responsePublicId, Guid callerUserId, Guid uploadId) =>
        $"{responsePublicId}/{callerUserId}/{uploadId}";

    /// <summary>
    /// Full container path of a staged upload, rebuilt from (response, caller, uploadId) alone so
    /// another client's upload, or one staged for another response, can never resolve.
    /// </summary>
    public static string BuildStagingContainerPath(Guid responsePublicId, Guid callerUserId, Guid uploadId) =>
        $"{StagingPrefix}/{BuildStagingSubPath(responsePublicId, callerUserId, uploadId)}";

    /// <summary>Permanent blob key of a saved answer's file — one per uploadId.</summary>
    public static string BuildFinalContainerPath(Guid responsePublicId, Guid uploadId, string extension) =>
        $"{FinalPrefix}/{responsePublicId}/{uploadId}.{extension}";

    /// <summary>
    /// Parses the uploadId and extension out of a stored URL, accepting only a value this service
    /// would have written for <paramref name="responsePublicId"/>. Foreign, legacy or malformed
    /// values return false.
    /// </summary>
    public static bool TryParseOwnFinalUrl(
        IBlobStorageService blobStorage, Guid responsePublicId, string? storedUrl, out Guid uploadId, out string extension)
    {
        uploadId = Guid.Empty;
        extension = string.Empty;

        if (string.IsNullOrWhiteSpace(storedUrl))
        {
            return false;
        }

        var folderUrl = blobStorage.BuildPublicUrl($"{FinalPrefix}/{responsePublicId}/");

        if (!storedUrl.StartsWith(folderUrl, StringComparison.Ordinal))
        {
            return false;
        }

        var fileName = storedUrl[folderUrl.Length..];
        var dot = fileName.IndexOf('.');

        if (dot <= 0 || !Guid.TryParse(fileName[..dot], out var parsedId))
        {
            return false;
        }

        var parsedExtension = fileName[(dot + 1)..];

        // Strict round-trip: rebuild the exact value and compare, so query strings, extra path
        // segments or alternate GUID spellings are all rejected.
        if (!FinalExtensions.Contains(parsedExtension, StringComparer.Ordinal)
            || !string.Equals(
                storedUrl,
                blobStorage.BuildPublicUrl(BuildFinalContainerPath(responsePublicId, parsedId, parsedExtension)),
                StringComparison.Ordinal))
        {
            return false;
        }

        uploadId = parsedId;
        extension = parsedExtension;
        return true;
    }

    /// <summary>True when <paramref name="storedUrl"/> is exactly the canonical URL for (response, uploadId) with any allowed extension.</summary>
    public static bool IsOwnFinalUrlFor(
        IBlobStorageService blobStorage, Guid responsePublicId, Guid uploadId, string? storedUrl) =>
        TryParseOwnFinalUrl(blobStorage, responsePublicId, storedUrl, out var storedUploadId, out _)
        && storedUploadId == uploadId;
}
