namespace FormUp.Application.Features.Questionnaires.GenerateAnswerFileUploadUrl;

/// <summary>Request for a pre-signed upload URL for one file_upload answer.</summary>
public class GenerateAnswerFileUploadUrlRequest
{
    /// <summary>Route parameter — the in-progress response the file belongs to.</summary>
    public Guid ResponsePublicId { get; set; }

    /// <summary>Public id of the file_upload question being answered.</summary>
    public Guid QuestionPublicId { get; set; }

    /// <summary>Declared content type: image/jpeg, image/png or image/webp.</summary>
    public string ContentType { get; set; } = string.Empty;

    /// <summary>Declared size in bytes, 1 to 5 MiB.</summary>
    public long SizeBytes { get; set; }
}
