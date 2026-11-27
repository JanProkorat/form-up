using FluentAssertions;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Services;

namespace FormUp.Tests.Services;

/// <summary>
/// The new-message push copy: sender name as title, message text as body, a localized photo
/// body when there is no text, and message text that is never re-interpolated (#1067).
/// </summary>
public class NotificationTemplatesNewMessageTests
{
    [Theory]
    [InlineData("cs", "📷 Fotka")]
    [InlineData("en", "📷 Photo")]
    [InlineData("de", "📷 Foto")]
    public void Resolve_ImageOnlyVariant_UsesLocalizedPhotoBody(string language, string expectedBody)
    {
        var (title, body) = NotificationTemplates.Resolve(
            NotificationType.NewMessage,
            language,
            new Dictionary<string, string> { ["senderName"] = "Petra" },
            NotificationTemplates.NewMessageImageOnly);

        title.Should().Be("Petra");
        body.Should().Be(expectedBody);
    }

    [Fact]
    public void Resolve_TextContainingPlaceholder_IsNotSubstituted()
    {
        var (title, body) = NotificationTemplates.Resolve(
            NotificationType.NewMessage,
            "en",
            new Dictionary<string, string>
            {
                [NotificationTemplates.MessageTextKey] = "literal {senderName} here",
                ["senderName"] = "Petra",
            });

        title.Should().Be("Petra");
        body.Should().Be("literal {senderName} here");
    }

    [Fact]
    public void TruncateForPush_ShortText_IsReturnedUnchanged()
    {
        ChatMessageNotifier.TruncateForPush("Hello").Should().Be("Hello");
    }

    [Fact]
    public void TruncateForPush_LongText_FitsTheLimitAndEndsWithEllipsis()
    {
        var result = ChatMessageNotifier.TruncateForPush(new string('a', 500));

        result.Length.Should().Be(ChatMessageNotifier.MaxPushTextLength);
        result.Should().EndWith("…");
    }

    [Theory]
    [InlineData("😀")] // surrogate pair
    [InlineData("👨‍👩‍👧")] // ZWJ family sequence
    public void TruncateForPush_EmojiAtTheCutBoundary_IsNeverSplit(string emoji)
    {
        // Place the emoji so the cut falls inside it for every possible offset.
        foreach (var padding in Enumerable.Range(
                     ChatMessageNotifier.MaxPushTextLength - 1 - emoji.Length,
                     emoji.Length + 1))
        {
            var text = new string('a', padding) + emoji + new string('b', 50);

            var result = ChatMessageNotifier.TruncateForPush(text);

            result.Length.Should().BeLessThanOrEqualTo(ChatMessageNotifier.MaxPushTextLength);
            result.Should().EndWith("…");
            var body = result[..^1];
            var elements = System.Globalization.StringInfo.GetTextElementEnumerator(body);
            while (elements.MoveNext())
            {
                var element = (string)elements.Current;
                // Every kept element is a whole 'a' or a whole emoji, never a fragment of one.
                (element == "a" || element == emoji).Should().BeTrue($"padding {padding} produced fragment '{element}'");
            }
        }
    }
}
