using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Infrastructure.Services;

namespace FormUp.Application.Domain.Services;

/// <summary>
/// Duplicates a library entry's images (food or recipe) into blobs owned by its copy, so the copy never
/// shares a storage path with its source. A missing, foreign or oversized source image is skipped.
/// </summary>
/// <param name="imageUpload">Validates that a source URL belongs to the source entry.</param>
/// <param name="blobStorage">Blob storage used to read the source object and write the duplicate.</param>
/// <param name="logger">Logger for skipped images.</param>
public sealed class LibraryImageCopier(
    IImageUploadService imageUpload,
    IBlobStorageService blobStorage,
    ILogger logger)
{
    private const int GalleryCap = 6;

    private static readonly IReadOnlyDictionary<string, string> ContentTypesByExtension =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["jpg"] = "image/jpeg",
            ["png"] = "image/png",
            ["webp"] = "image/webp",
            ["heic"] = "image/heic",
            ["heif"] = "image/heif",
        };

    /// <summary>
    /// Copies one image to a fresh key under the copy's folder.
    /// </summary>
    /// <param name="scope">Food or recipe image scope.</param>
    /// <param name="sourceId">Public id of the source entry.</param>
    /// <param name="copyId">Public id of the new entry.</param>
    /// <param name="slot">Slot name used in the new key (<c>main</c> or <c>gallery</c>).</param>
    /// <param name="sourceUrl">The source entry's stored image URL.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The copy's image URL, or <see langword="null"/> when the image was skipped.</returns>
    public async Task<string?> CopyAsync(
        ImageUploadScope scope, Guid sourceId, Guid copyId, string slot, string? sourceUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            return null;
        }

        var prefix = PrefixFor(scope);
        var sourcePath = ResolveSourcePath(scope, prefix, sourceId, sourceUrl);

        if (sourcePath is null)
        {
            logger.LogWarning("Skipping image copy: source URL does not belong to {Prefix} entry {SourceId}", prefix, sourceId);
            return null;
        }

        var extension = sourcePath[(sourcePath.LastIndexOf('.') + 1)..];

        if (!ContentTypesByExtension.TryGetValue(extension, out var contentType))
        {
            return null;
        }

        try
        {
            var source = await blobStorage.DownloadAsync(sourcePath, ImageUploadService.MaxImageSizeBytes, ct);

            if (source?.Data is null)
            {
                logger.LogWarning("Skipping image copy: source object missing or oversized for {Prefix} entry {SourceId}", prefix, sourceId);
                return null;
            }

            var newPath = $"{prefix}/{copyId}/{slot}-{Guid.NewGuid():N}.{extension}";
            await blobStorage.UploadAsync(newPath, source.Data, contentType, ct);

            return blobStorage.BuildPublicUrl(newPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Skipping image copy: blob storage failed for {Prefix} entry {SourceId}", prefix, sourceId);
            return null;
        }
    }

    /// <summary>
    /// Copies up to six gallery images, dropping the ones that could not be copied.
    /// </summary>
    /// <param name="scope">Food or recipe image scope.</param>
    /// <param name="sourceId">Public id of the source entry.</param>
    /// <param name="copyId">Public id of the new entry.</param>
    /// <param name="sourceUrls">The source entry's gallery URLs.</param>
    /// <param name="gallerySlot">Slot name used in the new keys.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<List<string>> CopyGalleryAsync(
        ImageUploadScope scope, Guid sourceId, Guid copyId, string gallerySlot, IEnumerable<string> sourceUrls, CancellationToken ct)
    {
        var copied = new List<string>();

        foreach (var sourceUrl in sourceUrls.Take(GalleryCap))
        {
            var copyUrl = await CopyAsync(scope, sourceId, copyId, gallerySlot, sourceUrl, ct);

            if (copyUrl is not null)
            {
                copied.Add(copyUrl);
            }
        }

        return copied;
    }

    private static string PrefixFor(ImageUploadScope scope) => scope switch
    {
        ImageUploadScope.Food => "foods",
        ImageUploadScope.Recipe => "recipes",
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Only food and recipe images can be copied.")
    };

    /// <summary>
    /// Returns the container path of <paramref name="sourceUrl"/> when it is an image of the source entry — either
    /// in its folder (<c>{prefix}/{id}/{file}</c>) or the legacy <c>{prefix}/{id}.{ext}</c> main image.
    /// </summary>
    private string? ResolveSourcePath(ImageUploadScope scope, string prefix, Guid sourceId, string sourceUrl)
    {
        var fileName = sourceUrl[(sourceUrl.LastIndexOf('/') + 1)..];

        if (imageUpload.IsValidBlobUrlUnderSubPath(scope, sourceId.ToString(), sourceUrl))
        {
            return $"{prefix}/{sourceId}/{fileName}";
        }

        if (imageUpload.IsValidBlobUrlForSubPath(scope, sourceId.ToString(), sourceUrl))
        {
            return $"{prefix}/{fileName}";
        }

        return null;
    }
}
