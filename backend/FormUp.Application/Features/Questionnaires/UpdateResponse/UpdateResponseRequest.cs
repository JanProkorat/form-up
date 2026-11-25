namespace FormUp.Application.Features.Questionnaires.UpdateResponse;

public class UpdateResponseRequest
{
    public Guid ResponsePublicId { get; set; }
    public List<AnswerDto> Answers { get; set; } = [];
}

public class AnswerDto
{
    public Guid QuestionPublicId { get; set; }
    public string? ValueText { get; set; }
    public decimal? ValueNumber { get; set; }
    public string? ValueJson { get; set; }

    /// <summary>
    /// For file_upload questions: the uploadId issued by the file-upload-url endpoint, or the one
    /// already stored on this answer (a no-op resend). Never a URL.
    /// </summary>
    public Guid? FileUploadId { get; set; }
}
