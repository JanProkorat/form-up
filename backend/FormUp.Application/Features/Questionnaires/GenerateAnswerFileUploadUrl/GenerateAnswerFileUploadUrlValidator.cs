using FastEndpoints;
using FluentValidation;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Features.Questionnaires.Shared;
using FormUp.Application.Infrastructure.Services;

namespace FormUp.Application.Features.Questionnaires.GenerateAnswerFileUploadUrl;

/// <summary>Validates the <see cref="GenerateAnswerFileUploadUrlRequest"/>.</summary>
public class GenerateAnswerFileUploadUrlValidator : Validator<GenerateAnswerFileUploadUrlRequest>
{
    /// <summary>Initializes validation rules for a questionnaire file upload-url request.</summary>
    public GenerateAnswerFileUploadUrlValidator()
    {
        RuleFor(x => x.ResponsePublicId).NotEmpty();
        RuleFor(x => x.QuestionPublicId).NotEmpty();

        RuleFor(x => x.ContentType)
            .Must(contentType => QuestionnaireFilePolicy.AllowedContentTypes.Contains(contentType))
            .WithErrorCode(ErrorCodes.InvalidImageContentType)
            .WithMessage($"Content type must be one of: {string.Join(", ", QuestionnaireFilePolicy.AllowedContentTypes)}.");

        RuleFor(x => x.SizeBytes)
            .Must(size => size > 0 && size <= ImageUploadService.MaxImageSizeBytes)
            .WithErrorCode(ErrorCodes.ImageTooLarge)
            .WithMessage($"File size must be between 1 and {ImageUploadService.MaxImageSizeBytes} bytes.");
    }
}
