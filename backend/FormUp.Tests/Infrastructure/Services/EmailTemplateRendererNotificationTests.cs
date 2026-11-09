using System.Net;
using FluentAssertions;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Infrastructure.Services;
using Microsoft.Extensions.Configuration;

namespace FormUp.Tests.Infrastructure.Services;

/// <summary>
/// The coach notification emails (check-in, join request, photos, workout, new messages) rendered in every language.
/// </summary>
public class EmailTemplateRendererNotificationTests
{
    private const string ActionUrl = "https://app.example/clients/abc/forms";

    private const string BaseUrl = "https://app.example";

    private static readonly IConfiguration Configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["App:BaseUrl"] = BaseUrl,
            ["Jwt:Secret"] = new string('s', 64),
        })
        .Build();

    private static readonly EmailTemplateRenderer Renderer = new(Configuration);

    private static NotificationEmailWorkItem Item(
        NotificationEvent notificationEvent, string language, params (string Key, string Value)[] extra)
    {
        var values = new Dictionary<string, string>
        {
            [NotificationEmailWorkItem.ClientNameKey] = "Petra Svobodova",
            [NotificationEmailWorkItem.ActionUrlKey] = ActionUrl,
        };

        foreach (var (key, value) in extra)
        {
            values[key] = value;
        }

        return new NotificationEmailWorkItem("coach@example.com", language, Guid.NewGuid(), notificationEvent, values);
    }

    public static TheoryData<NotificationEvent, string> EveryEventAndLanguage()
    {
        var data = new TheoryData<NotificationEvent, string>();

        foreach (var notificationEvent in new[]
                 {
                     NotificationEvent.WeeklyCheckInSubmitted, NotificationEvent.JoinRequest,
                     NotificationEvent.ProgressPhotosSubmitted, NotificationEvent.WorkoutFinished,
                     NotificationEvent.NewMessage
                 })
        {
            foreach (var language in new[] { "en", "cs", "de" })
            {
                data.Add(notificationEvent, language);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryEventAndLanguage))]
    public void RenderNotification_EveryEventAndLanguage_FillsEveryPlaceholderAndLinksThePortal(
        NotificationEvent notificationEvent, string language)
    {
        var (subject, html) = Renderer.RenderNotification(Item(
            notificationEvent, language, (NotificationEmailWorkItem.PhotoCountKey, "3")));

        subject.Should().NotBeNullOrWhiteSpace().And.NotContain("{{");
        html.Should().NotContain("{{").And.Contain(ActionUrl).And.Contain("Petra");
        html.Should().Contain($"lang=\"{language}\"");
    }

    private static string ExpectedLabel(NotificationEvent notificationEvent, string language) =>
        (notificationEvent, language) switch
        {
            (NotificationEvent.NewMessage, "cs") => "NOVÉ ZPRÁVY",
            (NotificationEvent.NewMessage, "de") => "NEUE NACHRICHTEN",
            (NotificationEvent.NewMessage, _) => "NEW MESSAGES",
            (NotificationEvent.WeeklyCheckInSubmitted, "cs") => "TÝDENNÍ CHECK-IN",
            (NotificationEvent.WeeklyCheckInSubmitted, "de") => "WOCHEN-CHECK-IN",
            (NotificationEvent.WeeklyCheckInSubmitted, _) => "WEEKLY CHECK-IN",
            (NotificationEvent.JoinRequest, "cs") => "ŽÁDOST O SPOLUPRÁCI",
            (NotificationEvent.JoinRequest, "de") => "BEITRITTSANFRAGE",
            (NotificationEvent.JoinRequest, _) => "JOIN REQUEST",
            (NotificationEvent.ProgressPhotosSubmitted, "cs") => "FOTKY POKROKU",
            (NotificationEvent.ProgressPhotosSubmitted, "de") => "FORTSCHRITTSFOTOS",
            (NotificationEvent.ProgressPhotosSubmitted, _) => "PROGRESS PHOTOS",
            (NotificationEvent.WorkoutFinished, "cs") => "DOKONČENÝ TRÉNINK",
            (NotificationEvent.WorkoutFinished, "de") => "TRAINING BEENDET",
            (NotificationEvent.WorkoutFinished, _) => "WORKOUT FINISHED",
            _ => throw new ArgumentOutOfRangeException(nameof(notificationEvent))
        };

    [Theory]
    [MemberData(nameof(EveryEventAndLanguage))]
    public void RenderNotification_EveryEventAndLanguage_ShowsTheLocalizedLabelAboveTheHeading(
        NotificationEvent notificationEvent, string language)
    {
        var (_, html) = Renderer.RenderNotification(Item(
            notificationEvent, language, (NotificationEmailWorkItem.PhotoCountKey, "3")));

        var label = html.IndexOf($">{ExpectedLabel(notificationEvent, language)}</p>", StringComparison.Ordinal);

        label.Should().BeGreaterThan(-1);
        html.IndexOf("<h1", StringComparison.Ordinal).Should().BeGreaterThan(label);
    }

    [Theory]
    [MemberData(nameof(EveryEventAndLanguage))]
    public void RenderNotification_EveryEventAndLanguage_UsesNeutralCopyAndNeverTheBannedCzechWord(
        NotificationEvent notificationEvent, string language)
    {
        var (subject, html) = Renderer.RenderNotification(Item(
            notificationEvent, language, (NotificationEmailWorkItem.PhotoCountKey, "1")));
        var copy = $"{subject} {html}".ToLowerInvariant();

        copy.Should().NotContain("kouč");
        copy.Should().NotContain(" her ").And.NotContain(" she ").And.NotContain(" his ");
        copy.Should().NotContain("(a)");
    }

    [Fact]
    public void RenderNotification_ClientNameWithMarkup_IsHtmlEncoded()
    {
        var item = Item(NotificationEvent.JoinRequest, "en");
        var hostile = item with
        {
            Values = new Dictionary<string, string>(item.Values) { [NotificationEmailWorkItem.ClientNameKey] = "<script>alert(1)</script>" }
        };

        var (_, html) = Renderer.RenderNotification(hostile);

        html.Should().NotContain("<script>alert(1)</script>").And.Contain("&lt;script&gt;");
    }

    [Fact]
    public void RenderNotification_CheckInWithWeight_ShowsTheWeightTile()
    {
        var (_, withWeight) = Renderer.RenderNotification(Item(
            NotificationEvent.WeeklyCheckInSubmitted, "en", (NotificationEmailWorkItem.WeightKey, "68.4 kg")));
        var (_, withoutWeight) = Renderer.RenderNotification(Item(NotificationEvent.WeeklyCheckInSubmitted, "en"));

        withWeight.Should().Contain("68.4 kg").And.Contain("Weight");
        withoutWeight.Should().NotContain("Weight");
    }

    [Fact]
    public void RenderNotification_JoinRequestMessageAndPackageAreOptional()
    {
        var (_, full) = Renderer.RenderNotification(Item(
            NotificationEvent.JoinRequest, "en",
            (NotificationEmailWorkItem.MessageKey, "Two sessions a week"),
            (NotificationEmailWorkItem.PackageNameKey, "Starter")));
        var (_, bare) = Renderer.RenderNotification(Item(NotificationEvent.JoinRequest, "en"));

        full.Should().Contain("Two sessions a week").And.Contain("Package: Starter");
        bare.Should().NotContain("Package:").And.NotContain("&ldquo;");
    }

    [Theory]
    [InlineData("en", "3", "3 photos sent")]
    [InlineData("en", "1", "1 photo sent")]
    [InlineData("cs", "1", "odeslána 1 fotka")]
    [InlineData("cs", "2", "odeslány 2 fotky")]
    [InlineData("cs", "4", "odeslány 4 fotky")]
    [InlineData("cs", "5", "odesláno 5 fotek")]
    [InlineData("cs", "21", "odesláno 21 fotek")]
    [InlineData("de", "2", "2 Fotos gesendet")]
    public void RenderNotification_RequestAnswer_SaysPhotosGenericallyWithTheRightPlural(string language, string count, string expected)
    {
        var (_, html) = Renderer.RenderNotification(Item(
            NotificationEvent.ProgressPhotosSubmitted, language, (NotificationEmailWorkItem.PhotoCountKey, count)));

        WebUtility.HtmlDecode(html).Should().Contain(expected);
    }

    [Theory]
    [InlineData("en", "1", "Petra Svobodova · 1 new progress photo")]
    [InlineData("en", "3", "Petra Svobodova · 3 new progress photos")]
    [InlineData("cs", "1", "Petra Svobodova · 1 nová fotka pokroku")]
    [InlineData("cs", "2", "Petra Svobodova · 2 nové fotky pokroku")]
    [InlineData("cs", "4", "Petra Svobodova · 4 nové fotky pokroku")]
    [InlineData("cs", "5", "Petra Svobodova · 5 nových fotek pokroku")]
    [InlineData("de", "1", "Petra Svobodova · 1 neues Fortschrittsfoto")]
    [InlineData("de", "3", "Petra Svobodova · 3 neue Fortschrittsfotos")]
    public void RenderNotification_FreeFormPhotos_LeadsWithTheNameAndTheCountAndNamesNoRequest(string language, string count, string expectedLead)
    {
        var (_, html) = Renderer.RenderNotification(Item(
            NotificationEvent.ProgressPhotosSubmitted, language,
            (NotificationEmailWorkItem.PhotoCountKey, count),
            (NotificationEmailWorkItem.FreeFormKey, "true")));
        var copy = WebUtility.HtmlDecode(html);

        copy.Should().Contain($">{expectedLead}</p>");
        copy.ToLowerInvariant().Should().NotContain("request").And.NotContain("žádost").And.NotContain("anfrage");
    }

    [Theory]
    [InlineData("en", "New progress photos from Petra Svobodova.")]
    [InlineData("cs", "Nové fotky pokroku od: Petra Svobodova.")]
    [InlineData("de", "Neue Fortschrittsfotos von Petra Svobodova.")]
    public void RenderNotification_FreeFormPhotos_PreheaderNamesTheClientWithoutARequest(string language, string expectedPreheader)
    {
        var (_, html) = Renderer.RenderNotification(Item(
            NotificationEvent.ProgressPhotosSubmitted, language,
            (NotificationEmailWorkItem.PhotoCountKey, "2"),
            (NotificationEmailWorkItem.FreeFormKey, "true")));

        WebUtility.HtmlDecode(html).Should().Contain(expectedPreheader);
    }

    [Theory]
    [InlineData("en", "in reply to your request")]
    [InlineData("cs", "jako odpověď na vaši žádost")]
    [InlineData("de", "als Antwort auf Ihre Anfrage")]
    public void RenderNotification_RequestAnswer_PreheaderStillMentionsTheRequest(string language, string expectedPart)
    {
        var (_, html) = Renderer.RenderNotification(Item(
            NotificationEvent.ProgressPhotosSubmitted, language, (NotificationEmailWorkItem.PhotoCountKey, "2")));

        WebUtility.HtmlDecode(html).Should().Contain(expectedPart);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RenderNotification_Photos_NeverCarriesAPhotoAddress(bool freeForm)
    {
        var (_, html) = Renderer.RenderNotification(Item(
            NotificationEvent.ProgressPhotosSubmitted, "en",
            (NotificationEmailWorkItem.PhotoCountKey, "2"),
            (NotificationEmailWorkItem.FreeFormKey, freeForm ? "true" : "false")));

        html.Should().NotContain("plan-photos").And.NotContain("<img").And.NotContain("blob");
    }

    [Fact]
    public void RenderNotification_PhotosWithRequestDate_NamesTheLocalizedDate()
    {
        var (_, html) = Renderer.RenderNotification(Item(
            NotificationEvent.ProgressPhotosSubmitted, "cs",
            (NotificationEmailWorkItem.PhotoCountKey, "2"),
            (NotificationEmailWorkItem.RequestDateKey, "2026-10-12")));

        html.Should().Contain("12. 10. 2026");
    }

    [Fact]
    public void RenderNotification_WorkoutWithStats_ShowsTilesAndTheClientNote()
    {
        var (_, html) = Renderer.RenderNotification(Item(
            NotificationEvent.WorkoutFinished, "en",
            (NotificationEmailWorkItem.SessionNameKey, "Upper body A"),
            (NotificationEmailWorkItem.PlanNameKey, "Lean cut"),
            (NotificationEmailWorkItem.DurationMinutesKey, "47"),
            (NotificationEmailWorkItem.ExerciseCountKey, "6"),
            (NotificationEmailWorkItem.SetCountKey, "18"),
            (NotificationEmailWorkItem.NoteKey, "Felt strong")));

        html.Should().Contain("Upper body A").And.Contain("47 min").And.Contain("18").And.Contain("Felt strong");
    }

    [Theory]
    [InlineData("en", "4", "Petra sent you 4 messages")]
    [InlineData("en", "1", "Petra sent you a message")]
    [InlineData("cs", "1", "Petra · 1 nová zpráva")]
    [InlineData("cs", "3", "Petra · 3 nové zprávy")]
    [InlineData("cs", "5", "Petra · 5 nových zpráv")]
    [InlineData("de", "4", "Petra hat Ihnen 4 Nachrichten gesendet")]
    [InlineData("de", "1", "Petra hat Ihnen eine Nachricht gesendet")]
    public void RenderNotification_NewMessage_TitlesTheBurstWithTheCountAndTheRightPlural(string language, string count, string expectedTitle)
    {
        var (_, html) = Renderer.RenderNotification(Item(
            NotificationEvent.NewMessage, language, (NotificationEmailWorkItem.MessageCountKey, count)));

        // The encoder turns accented letters into entities; decode before comparing the visible text.
        WebUtility.HtmlDecode(html).Should().Contain($">{expectedTitle}</h1>");
    }

    [Theory]
    [InlineData("en", "Petra sent you 4 messages")]
    [InlineData("cs", "Nové zprávy: Petra Svobodova (4)")]
    [InlineData("de", "Petra hat Ihnen 4 Nachrichten gesendet")]
    public void RenderNotification_NewMessage_PutsTheCountInTheSubject(string language, string expectedSubject)
    {
        var (subject, _) = Renderer.RenderNotification(Item(
            NotificationEvent.NewMessage, language, (NotificationEmailWorkItem.MessageCountKey, "4")));

        subject.Should().Be(expectedSubject);
    }

    [Fact]
    public void RenderNotification_NewMessage_HasNoMessageBubblesOrMoreLine()
    {
        var (_, html) = Renderer.RenderNotification(Item(
            NotificationEvent.NewMessage, "en", (NotificationEmailWorkItem.MessageCountKey, "9")));

        // The footer's random link token is cut out so a chance "more" inside it cannot fail the check.
        var copy = System.Text.RegularExpressions.Regex.Replace(html, "token=[^\"]+", string.Empty);

        copy.Should().NotContain("&ldquo;").And.NotContain("more");
    }

    [Theory]
    [MemberData(nameof(EveryEventAndLanguage))]
    public void RenderNotification_EveryEventAndLanguage_FootersTheTurnOffAndSettingsLinks(
        NotificationEvent notificationEvent, string language)
    {
        var item = Item(notificationEvent, language, (NotificationEmailWorkItem.PhotoCountKey, "2"));

        var (_, html) = Renderer.RenderNotification(item);

        var (turnOff, settings) = language switch
        {
            "cs" => ("Vypnout tyto e-maily", "Nastavení oznámení"),
            "de" => ("Diese E-Mails ausschalten", "Benachrichtigungseinstellungen"),
            _ => ("Turn off these emails", "Notification settings"),
        };
        var token = System.Text.RegularExpressions.Regex.Match(html, $"href=\"{BaseUrl}/email-off\\?token=([A-Za-z0-9_-]+)\"[^>]*>{turnOff}</a>");

        token.Success.Should().BeTrue("the footer links the turn-off page under the localized label");
        html.Should().Contain($"href=\"{BaseUrl}/settings\"").And.Contain($">{settings}</a>");
        new EmailTurnOffTokenService(Configuration, TimeProvider.System).Validate(token.Groups[1].Value)
            .Should().Be(new EmailTurnOffTokenClaims(item.UserId, notificationEvent));
    }

    [Fact]
    public void NotificationHeaders_CarryAOneClickLinkForTheSameUserAndEventAndThePostMarker()
    {
        var item = Item(NotificationEvent.JoinRequest, "en");

        var headers = Renderer.NotificationHeaders(item);

        headers["List-Unsubscribe-Post"].Should().Be("List-Unsubscribe=One-Click");
        var link = System.Text.RegularExpressions.Regex.Match(
            headers["List-Unsubscribe"], $"^<{BaseUrl}/users/email-links/one-click\\?token=([A-Za-z0-9_-]+)>$");
        link.Success.Should().BeTrue();
        new EmailTurnOffTokenService(Configuration, TimeProvider.System).Validate(link.Groups[1].Value)
            .Should().Be(new EmailTurnOffTokenClaims(item.UserId, NotificationEvent.JoinRequest));
    }

    [Fact]
    public void RenderNotification_UnknownEvent_Throws()
    {
        var act = () => Renderer.RenderNotification(Item((NotificationEvent)999, "en"));

        act.Should().Throw<NotSupportedException>();
    }
}
