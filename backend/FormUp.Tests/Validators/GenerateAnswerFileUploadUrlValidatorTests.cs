using FluentAssertions;
using FluentValidation.TestHelper;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Features.Questionnaires.GenerateAnswerFileUploadUrl;
using FormUp.Application.Infrastructure.Services;

namespace FormUp.Tests.Validators;

public class GenerateAnswerFileUploadUrlValidatorTests
{
    private static GenerateAnswerFileUploadUrlRequest Valid() => new()
    {
        ResponsePublicId = Guid.NewGuid(),
        QuestionPublicId = Guid.NewGuid(),
        ContentType = "image/png",
        SizeBytes = 1024,
    };

    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("image/png")]
    [InlineData("image/webp")]
    public void Validate_AllowedContentType_Passes(string contentType)
    {
        var request = Valid();
        request.ContentType = contentType;

        new GenerateAnswerFileUploadUrlValidator().TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData("image/heic")]
    [InlineData("application/pdf")]
    [InlineData("image/svg+xml")]
    [InlineData("")]
    public void Validate_DisallowedContentType_FailsWithInvalidContentTypeCode(string contentType)
    {
        var request = Valid();
        request.ContentType = contentType;

        var result = new GenerateAnswerFileUploadUrlValidator().TestValidate(request);

        result.Errors.Should().ContainSingle(e => e.ErrorCode == ErrorCodes.InvalidImageContentType);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(ImageUploadService.MaxImageSizeBytes, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(ImageUploadService.MaxImageSizeBytes + 1, false)]
    public void Validate_SizeBoundaries_MatchTheImageLimit(long sizeBytes, bool expectedValid)
    {
        var request = Valid();
        request.SizeBytes = sizeBytes;

        var result = new GenerateAnswerFileUploadUrlValidator().TestValidate(request);

        (result.Errors.Count == 0).Should().Be(expectedValid);

        if (!expectedValid)
        {
            result.Errors.Should().ContainSingle(e => e.ErrorCode == ErrorCodes.ImageTooLarge);
        }
    }
}
