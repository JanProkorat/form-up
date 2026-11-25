namespace FormUp.Application.Features.Questionnaires.GenerateAnswerFileUploadUrl;

/// <summary>Pre-signed upload URL and the id to reference from the answer save.</summary>
public class GenerateAnswerFileUploadUrlResponse
{
    /// <summary>The pre-signed URL the client should PUT the file bytes to.</summary>
    public string UploadUrl { get; set; } = string.Empty;

    /// <summary>The identifier to send as <c>fileUploadId</c> in PUT /client/questionnaire/response/{id} once the upload completes.</summary>
    public Guid UploadId { get; set; }
}
