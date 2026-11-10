using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Features.ClientPhotos.FinalizeUnlinkedPhoto;

/// <summary>
/// Request model for finalizing a progress photo upload that belongs to no plan.
/// The caller must have uploaded the image using the URL from POST /client/photos/upload-url.
/// </summary>
public class FinalizeUnlinkedPhotoRequest
{
    /// <summary>
    /// Blob URL returned by the upload-url endpoint.
    /// </summary>
    public string BlobUrl { get; set; } = string.Empty;

    /// <summary>
    /// Category of the photo. Only Body and FreeForm are accepted.
    /// </summary>
    public PlanPhotoCategory Category { get; set; }

    /// <summary>
    /// Optional caption / description (max 500 chars).
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// When the photo was taken. Defaults to UtcNow when not provided.
    /// </summary>
    public DateTime? TakenAt { get; set; }

    /// <summary>
    /// Optional diary request ID. When set, the photo answers that request; the same rules apply as
    /// for a plan photo (owned by the caller, Accepted or InProgress, not past its end date).
    /// </summary>
    public Guid? DiaryRequestId { get; set; }
}
