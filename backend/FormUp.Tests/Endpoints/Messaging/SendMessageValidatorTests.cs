using FastEndpoints;
using FluentAssertions;
using FluentValidation.TestHelper;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Features.Messaging.SendMessage;

namespace FormUp.Tests.Endpoints.Messaging;

/// <summary>
/// Unit tests for <see cref="SendMessageValidator"/>.
/// </summary>
public class SendMessageValidatorTests
{
    private static SendMessageRequest TextOnlyRequest() => new()
    {
        ConversationId = Guid.NewGuid(),
        Text = "Hello there",
    };

    private static SendMessageRequest ImageOnlyRequest() => new()
    {
        ConversationId = Guid.NewGuid(),
        Text = "",
        ImageUploadId = Guid.NewGuid(),
        ImageWidth = 800,
        ImageHeight = 600,
    };

    [Fact]
    public void Validate_TextOnly_Passes()
    {
        var result = new SendMessageValidator().TestValidate(TextOnlyRequest());

        result.ShouldNotHaveValidationErrorFor(x => x.Text);
    }

    [Fact]
    public void Validate_ImageOnly_EmptyText_Passes()
    {
        var result = new SendMessageValidator().TestValidate(ImageOnlyRequest());

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_TextAndImage_Passes()
    {
        var request = ImageOnlyRequest();
        request.Text = "check this out";

        var result = new SendMessageValidator().TestValidate(request);

        result.ShouldNotHaveAnyValidationErrors();
    }

    // Assert on ErrorMessage, not PropertyName — the global property-name resolver is
    // camelCased by any test that boots the app, so a PropertyName-based assertion here
    // (ShouldHaveValidationErrorFor) passes in isolation but flakes under the full suite
    // (rules/validation.md#testing-validators).

    [Fact]
    public void Validate_EmptyGuidClientMessageId_Fails()
    {
        var request = TextOnlyRequest();
        request.ClientMessageId = Guid.Empty;

        var result = new SendMessageValidator().TestValidate(request);

        result.Errors.Should().ContainSingle(e => e.ErrorMessage == "ClientMessageId must not be an empty GUID.");
    }

    [Fact]
    public void Validate_RealOrAbsentClientMessageId_Passes()
    {
        var request = TextOnlyRequest();
        new SendMessageValidator().TestValidate(request).ShouldNotHaveAnyValidationErrors();

        request.ClientMessageId = Guid.NewGuid();
        new SendMessageValidator().TestValidate(request).ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_NeitherTextNorImage_FailsOnText()
    {
        var request = TextOnlyRequest();
        request.Text = "   ";

        var result = new SendMessageValidator().TestValidate(request);

        result.Errors.Should().Contain(e => e.ErrorMessage == "Message must include text or an image.");
    }

    [Fact]
    public void Validate_EmptyText_NoImage_Fails()
    {
        var request = TextOnlyRequest();
        request.Text = "";

        var result = new SendMessageValidator().TestValidate(request);

        result.Errors.Should().Contain(e => e.ErrorMessage == "Message must include text or an image.");
    }

    [Fact]
    public void Validate_TextExceedsMaxLength_Fails()
    {
        var request = TextOnlyRequest();
        request.Text = new string('a', ChatMessage.MaxTextLength + 1);

        var result = new SendMessageValidator().TestValidate(request);

        result.Errors.Should().Contain(
            e => e.ErrorMessage == $"Message must be at most {ChatMessage.MaxTextLength} characters.");
    }

    [Fact]
    public void Validate_TextExactlyAtMaxLength_Passes()
    {
        var request = TextOnlyRequest();
        request.Text = new string('a', ChatMessage.MaxTextLength);

        var result = new SendMessageValidator().TestValidate(request);

        result.ShouldNotHaveValidationErrorFor(x => x.Text);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(20001)]
    [InlineData(-1)]
    public void Validate_ImageWidthOutsideRange_Fails(int width)
    {
        var request = ImageOnlyRequest();
        request.ImageWidth = width;

        var result = new SendMessageValidator().TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.ImageWidth);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(20000)]
    public void Validate_ImageWidthAtBoundary_Passes(int width)
    {
        var request = ImageOnlyRequest();
        request.ImageWidth = width;

        var result = new SendMessageValidator().TestValidate(request);

        result.ShouldNotHaveValidationErrorFor(x => x.ImageWidth);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(20001)]
    public void Validate_ImageHeightOutsideRange_Fails(int height)
    {
        var request = ImageOnlyRequest();
        request.ImageHeight = height;

        var result = new SendMessageValidator().TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.ImageHeight);
    }

    [Fact]
    public void Validate_NoImageUploadId_WidthHeightUnset_DoesNotValidateThem()
    {
        var request = TextOnlyRequest();

        var result = new SendMessageValidator().TestValidate(request);

        result.ShouldNotHaveValidationErrorFor(x => x.ImageWidth);
        result.ShouldNotHaveValidationErrorFor(x => x.ImageHeight);
    }
}
