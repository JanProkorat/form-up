using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Services;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace FormUp.Tests.Infrastructure.Services;

/// <summary>
/// The shared email renderer: every template wrapped in the layout, no leftovers of the old look, user text
/// encoded and never expanded, language fallback, subjects.
/// </summary>
public class EmailTemplateRendererTests
{
    private const string AssetBaseUrl = "https://api.example.com";

    private static readonly string[] Languages = ["cs", "en", "de"];

    private static readonly string[] Templates =
    [
        EmailTemplateRenderer.EmailVerification,
        EmailTemplateRenderer.PasswordReset,
        EmailTemplateRenderer.Invitation,
        EmailTemplateRenderer.PaymentRequest,
        EmailTemplateRenderer.BankAccountChanged,
        EmailTemplateRenderer.ReauthCode
    ];

    public static TheoryData<string, string> EveryTemplateInEveryLanguage()
    {
        var data = new TheoryData<string, string>();

        foreach (var template in Templates)
        {
            foreach (var language in Languages)
            {
                data.Add(template, language);
            }
        }

        return data;
    }

    private static EmailTemplateRenderer Renderer(string? assetBaseUrl = AssetBaseUrl) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Email:AssetBaseUrl"] = assetBaseUrl
        }).Build());

    private static PaymentRequestEmail Payment(string language) => new(
        "eva@example.com",
        language,
        "Martin Král",
        "Online coaching",
        new DateOnly(2026, 11, 1),
        new DateOnly(2026, 11, 30),
        1750m,
        2500m,
        "Friends & family −30 %",
        new DateOnly(2026, 11, 1),
        "CZ6508000000192000145399",
        "2026110142",
        "Online coaching 11/2026",
        [1, 2, 3],
        PaymentMessageComposer.QrContentId);

    private static string RenderTemplate(EmailTemplateRenderer renderer, string template, string language)
    {
        var values = new Dictionary<string, string>
        {
            ["Email"] = "coach@example.com",
            ["VerifyUrl"] = "https://app.example.com/verify-email?token=abc",
            ["ResetUrl"] = "https://app.example.com/auth/reset-password?token=abc&email=coach%40example.com",
            ["Code"] = "123456",
            ["NewIban"] = "CZ65 **** 5399",
            ["PreviousIban"] = "CZ12 **** 0001",
            ["UndoUrl"] = "https://app.example.com/bank-account/undo?token=abc"
        };

        return template switch
        {
            EmailTemplateRenderer.Invitation => renderer.RenderInvitation(
                "Jan Novák",
                "https://app.example.com/invite/accept?token=abc",
                language,
                "Hi, see you soon.",
                new InvitationPackageInfo("Monthly coaching", PackageBillingInterval.Monthly, null)).Html,
            EmailTemplateRenderer.PaymentRequest => PaymentMessageComposer.RenderEmailHtml(
                renderer.Wrap(template, language), Payment(language), language, "https://app.example.com"),
            _ => renderer.Render(template, language, values).Html
        };
    }

    [Theory]
    [MemberData(nameof(EveryTemplateInEveryLanguage))]
    public void Render_EveryTemplate_IsWrappedInTheSharedLayoutWithNothingLeftOver(string template, string language)
    {
        var html = RenderTemplate(Renderer(), template, language);

        html.Should().Contain($"<html lang=\"{language}\">").And.Contain($"{AssetBaseUrl}/email/formup-logo.png");
        html.Should().Contain("prefers-color-scheme").And.Contain("display:none;max-height:0");
        html.Should().NotContain("{{").And.NotContain("}}");
        html.Should().NotContain("c9a84c").And.NotContain("rgba(201,168,76").And.NotContain("GF Platform")
            .And.NotContain("PLATFORM").And.NotContain("#0d0d0d");
        html.Should().NotContainEquivalentOf("kouč");
    }

    private static readonly Dictionary<string, (string En, string Cs, string De)> ExpectedLabels = new()
    {
        [EmailTemplateRenderer.EmailVerification] = ("CONFIRM EMAIL", "POTVRZENÍ E-MAILU", "E-MAIL BESTÄTIGEN"),
        [EmailTemplateRenderer.PasswordReset] = ("PASSWORD RESET", "OBNOVENÍ HESLA", "PASSWORT ZURÜCKSETZEN"),
        [EmailTemplateRenderer.Invitation] = ("INVITATION", "POZVÁNKA", "EINLADUNG"),
        [EmailTemplateRenderer.PaymentRequest] = ("PAYMENT REQUEST", "ŽÁDOST O PLATBU", "ZAHLUNGSAUFFORDERUNG"),
        [EmailTemplateRenderer.BankAccountChanged] = ("SECURITY", "ZABEZPEČENÍ", "SICHERHEIT"),
        [EmailTemplateRenderer.ReauthCode] = ("SECURITY", "ZABEZPEČENÍ", "SICHERHEIT")
    };

    private static string ExpectedLabel(string template, string language) => language switch
    {
        "cs" => ExpectedLabels[template].Cs,
        "de" => ExpectedLabels[template].De,
        _ => ExpectedLabels[template].En
    };

    [Theory]
    [MemberData(nameof(EveryTemplateInEveryLanguage))]
    public void Render_EveryTemplate_ShowsTheLocalizedLabelBetweenTheIconTileAndTheHeading(string template, string language)
    {
        var html = RenderTemplate(Renderer(), template, language);
        var label = $">{ExpectedLabel(template, language)}</p>";

        var tile = html.IndexOf("<td class=\"fu-tile-", StringComparison.Ordinal);
        var labelAt = html.IndexOf(label, StringComparison.Ordinal);
        var heading = html.IndexOf("<h1", StringComparison.Ordinal);

        tile.Should().BeGreaterThan(-1, "the test renderer has an asset base URL, so the icon tile renders");
        labelAt.Should().BeGreaterThan(tile);
        heading.Should().BeGreaterThan(labelAt);
        html.Should().Contain("margin:0 0 10px;font-size:12px;font-weight:600;letter-spacing:2px;color:#6B6863;");
    }

    [Theory]
    [InlineData(InvitationCoachRole.Trainer, "en", "Personal trainer")]
    [InlineData(InvitationCoachRole.Trainer, "cs", "Osobní trenér")]
    [InlineData(InvitationCoachRole.Trainer, "de", "Personal Trainer")]
    [InlineData(InvitationCoachRole.Nutritionist, "en", "Nutritionist")]
    [InlineData(InvitationCoachRole.Nutritionist, "cs", "Nutriční poradce")]
    [InlineData(InvitationCoachRole.Nutritionist, "de", "Ernährungsberater")]
    [InlineData(InvitationCoachRole.Both, "en", "Personal trainer &amp; nutritionist")]
    [InlineData(InvitationCoachRole.Both, "cs", "Osobní trenér a nutriční poradce")]
    [InlineData(InvitationCoachRole.Both, "de", "Personal Trainer und Ernährungsberater")]
    public void RenderInvitation_WithCoachRole_ShowsTheRoleLineUnderTheName(InvitationCoachRole role, string language, string expected)
    {
        var html = Renderer().RenderInvitation("Jan Novak", "https://app.example.com/i", language, null, null, role).Html;

        html.Should().Contain($"Jan Novak</p><p class=\"fu-muted\" style=\"margin:2px 0 0;font-size:13px;color:#6B6863;\">{expected}</p>");
    }

    [Fact]
    public void RenderInvitation_WithoutCoachRole_LeavesTheRoleLineOut()
    {
        var html = Renderer().RenderInvitation("Jan Novák", "https://app.example.com/i", "en", null, null).Html;

        html.Should().NotContain("font-size:13px;color:#6B6863").And.NotContain("{{");
    }

    [Theory]
    [InlineData("cs", "Váš plán. Společně.")]
    [InlineData("en", "Your plan. Together.")]
    [InlineData("de", "Ihr Plan. Gemeinsam.")]
    public void Render_Footer_CarriesTheTranslatedTagline(string language, string tagline)
    {
        var html = Renderer().Render(EmailTemplateRenderer.PasswordReset, language, new Dictionary<string, string>
        {
            ["Email"] = "a@example.com",
            ["ResetUrl"] = "https://app.example.com/r"
        }).Html;

        html.Should().Contain("Form Up &middot; " + tagline);
    }

    [Fact]
    public void Render_NoAssetBaseUrl_ShowsATextWordmarkInsteadOfABrokenImage()
    {
        var html = Renderer(assetBaseUrl: null).Render(EmailTemplateRenderer.ReauthCode, "en", new Dictionary<string, string> { ["Code"] = "1" }).Html;

        html.Should().NotContain("<img").And.Contain("FORM <span");
    }

    [Fact]
    public void Render_AssetBaseUrlWithTrailingSlash_BuildsOneAbsoluteLogoUrl()
    {
        var html = Renderer("https://api.example.com/").Render(EmailTemplateRenderer.ReauthCode, "en", new Dictionary<string, string> { ["Code"] = "1" }).Html;

        html.Should().Contain("src=\"https://api.example.com/email/formup-logo.png\"");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("fr")]
    [InlineData("fr-FR")]
    public void Render_UnknownLanguage_FallsBackToEnglish(string? language)
    {
        var (subject, html) = Renderer().Render(EmailTemplateRenderer.EmailVerification, language, new Dictionary<string, string>
        {
            ["Email"] = "a@example.com",
            ["VerifyUrl"] = "https://app.example.com/v"
        });

        subject.Should().Be("Confirm your email for Form Up");
        html.Should().Contain("<html lang=\"en\">").And.Contain("Confirm your email");
    }

    [Theory]
    [InlineData("cs-CZ", "cs")]
    [InlineData("de-AT", "de")]
    [InlineData("EN", "en")]
    public void NormalizeLanguage_KeepsTheTwoLetterCode(string language, string expected)
    {
        EmailTemplateRenderer.NormalizeLanguage(language).Should().Be(expected);
    }

    [Theory]
    [InlineData("en", "Confirm your email for Form Up", "Reset your Form Up password", "Jan Novák invited you to Form Up")]
    [InlineData("cs", "Potvrďte svůj e-mail pro Form Up", "Obnovení hesla ve Form Up", "Jan Novák vás zve do Form Up")]
    [InlineData("de", "Bestätigen Sie Ihre E-Mail für Form Up", "Form-Up-Passwort zurücksetzen", "Jan Novák hat Sie zu Form Up eingeladen")]
    public void Subjects_MatchTheBoardsInEveryLanguage(string language, string verify, string reset, string invitation)
    {
        var renderer = Renderer();
        var values = new Dictionary<string, string>
        {
            ["Email"] = "a@example.com",
            ["VerifyUrl"] = "https://app.example.com/v",
            ["ResetUrl"] = "https://app.example.com/r"
        };

        renderer.Render(EmailTemplateRenderer.EmailVerification, language, values).Subject.Should().Be(verify);
        renderer.Render(EmailTemplateRenderer.PasswordReset, language, values).Subject.Should().Be(reset);
        renderer.RenderInvitation("Jan Novák", "https://app.example.com/i", language, null, null).Subject.Should().Be(invitation);
    }

    [Fact]
    public void Render_Preheader_IsTheHiddenPreviewTextOfTheBoard()
    {
        var html = Renderer().Render(EmailTemplateRenderer.EmailVerification, "en", new Dictionary<string, string>
        {
            ["Email"] = "a@example.com",
            ["VerifyUrl"] = "https://app.example.com/v"
        }).Html;

        html.Should().Contain("mso-hide:all;\">One click and your account is ready.</div>");
    }

    [Fact]
    public void Render_FooterLine_IsEmptyByDefaultAndShownWhenGiven()
    {
        var values = new Dictionary<string, string> { ["Code"] = "1" };

        Renderer().Render(EmailTemplateRenderer.ReauthCode, "en", values).Html.Should().NotContain("Turn off");
        Renderer().Render(EmailTemplateRenderer.ReauthCode, "en", values, footerLine: "<a href=\"https://x.example\">Turn off</a>")
            .Html.Should().Contain("<a href=\"https://x.example\">Turn off</a><br>");
    }

    [Fact]
    public void RenderInvitation_WithoutPersonalMessage_HasNoQuoteBlock()
    {
        var html = Renderer().RenderInvitation("Jan Novák", "https://app.example.com/i", "en", "  ", null).Html;

        html.Should().NotContain("&ldquo;").And.NotContain("Message from");
    }

    [Fact]
    public void RenderInvitation_WithPersonalMessage_ShowsItUnderTheCoachsFirstName()
    {
        var html = Renderer().RenderInvitation("Jan Novák", "https://app.example.com/i", "en", "Hi Petra, welcome!", null).Html;

        html.Should().Contain("Message from Jan").And.Contain("&ldquo;Hi Petra, welcome!&rdquo;");
        html.Should().Contain("Jan invited you to work together").And.Contain(">JN<");
    }

    [Fact]
    public void RenderInvitation_UserTextWithMarkupAndPlaceholders_IsEncodedAndNotExpanded()
    {
        var package = new InvitationPackageInfo("{{AcceptUrl}} <i>pkg</i>", PackageBillingInterval.Monthly, null);

        var html = Renderer().RenderInvitation(
            "<b>{{PackageBlock}}</b>", "https://app.example.com/accept", "en", "<script>x</script> {{TrainerName}}", package).Html;

        html.Should().NotContain("<script>").And.NotContain("<b>{{").And.NotContain("<i>pkg</i>");
        html.Should().Contain("&lt;script&gt;x&lt;/script&gt; {{TrainerName}}");
        html.Should().Contain("&lt;b&gt;{{PackageBlock}}&lt;/b&gt;");
        html.Should().Contain("{{AcceptUrl}} &lt;i&gt;pkg&lt;/i&gt;");
    }

    [Fact]
    public void RenderInvitation_WithPackage_KeepsThePackageBlock()
    {
        var html = Renderer().RenderInvitation(
            "Jan Novák", "https://app.example.com/i", "cs", null, new InvitationPackageInfo("Online", PackageBillingInterval.Monthly, null)).Html;

        System.Net.WebUtility.HtmlDecode(html).Should().Contain("Balíček").And.Contain("Online · Měsíčně");
    }

    [Fact]
    public void Render_ReauthCode_EncodesTheCode()
    {
        var html = Renderer().Render(EmailTemplateRenderer.ReauthCode, "en", new Dictionary<string, string> { ["Code"] = "<1&2>" }).Html;

        html.Should().Contain("&lt;1&amp;2&gt;").And.NotContain("<1&2>");
    }

    [Fact]
    public void Render_UrlWithQueryString_IsHtmlEncodedInTheHref()
    {
        var html = Renderer().Render(EmailTemplateRenderer.PasswordReset, "en", new Dictionary<string, string>
        {
            ["Email"] = "a@example.com",
            ["ResetUrl"] = "https://app.example.com/r?token=t&email=a%40example.com"
        }).Html;

        html.Should().Contain("href=\"https://app.example.com/r?token=t&amp;email=a%40example.com\"");
    }

    private const string IconsUrl = AssetBaseUrl + "/email/icons/";

    [Theory]
    [InlineData(EmailTemplateRenderer.EmailVerification, "mail", "#EFF7E6", 20)]
    [InlineData(EmailTemplateRenderer.PasswordReset, "lock", "#FFE8D2", 20)]
    [InlineData(EmailTemplateRenderer.ReauthCode, "lock", "#FFE8D2", 20)]
    [InlineData(EmailTemplateRenderer.Invitation, "user-plus", "#FDE4E1", 20)]
    [InlineData(EmailTemplateRenderer.PaymentRequest, "banknote", "#FFE8D2", 17)]
    [InlineData(EmailTemplateRenderer.BankAccountChanged, "banknote", "#FFE8D2", 17)]
    [InlineData(EmailTemplateRenderer.NewMessage, "message", "#E3EBFA", 20)]
    [InlineData(EmailTemplateRenderer.CheckIn, "activity", "#FDE4E1", 20)]
    [InlineData(EmailTemplateRenderer.JoinRequest, "inbox", "#EEE7FA", 20)]
    [InlineData(EmailTemplateRenderer.Workout, "dumbbell", "#FFE8D2", 20)]
    [InlineData(EmailTemplateRenderer.Photos, "camera", "#F8E4EF", 20)]
    public void Wrap_EveryTemplate_PutsTheIconTileAboveTheHeading(string template, string icon, string background, int size)
    {
        var html = Renderer().Wrap(template, "en");
        var tile = html[..html.IndexOf("<h1", StringComparison.Ordinal)];

        tile.Should().Contain($"src=\"{IconsUrl}tile-{icon}-light.png\"").And.Contain($"src=\"{IconsUrl}tile-{icon}-dark.png\"");
        tile.Should().Contain("width=\"48\" height=\"48\"").And.Contain("border-radius:14px").And.Contain($"background-color:{background}");
        tile.Should().Contain($"width=\"{size}\" height=\"{size}\" alt=\"\"").And.Contain("margin:0 0 16px");
        tile.Should().Contain("display:none;mso-hide:all;");
        html.Should().NotContain("{{HeaderIcon}}").And.NotContain("{{NoteIcon}}");
    }

    [Theory]
    [InlineData("cs")]
    [InlineData("en")]
    [InlineData("de")]
    public void Wrap_EveryLanguage_HasTheTileTheNoteIconAndTheDarkModeSwap(string language)
    {
        var html = Renderer().Wrap(EmailTemplateRenderer.EmailVerification, language);

        html.Should().Contain("tile-mail-light.png").And.Contain("note-clock-light.png").And.Contain("note-clock-dark.png");
        html.Should().Contain("margin-right:10px").And.Contain("width=\"16\" height=\"16\" alt=\"\"");
        html.Should().Contain(".fu-ic-light { display: none !important; }").And.Contain(".fu-ic-dark { display: inline-block");
        html.Should().Contain(".fu-tile-mail { background-color: rgba(140,193,82,0.12) !important; }");
        html.Should().Contain(".fu-tile-banknote { background-color: #3A2414 !important; }");
    }

    [Theory]
    [InlineData(EmailTemplateRenderer.EmailVerification, "note-clock")]
    [InlineData(EmailTemplateRenderer.PasswordReset, "note-clock")]
    [InlineData(EmailTemplateRenderer.Photos, "note-shield")]
    [InlineData(EmailTemplateRenderer.ReauthCode, null)]
    [InlineData(EmailTemplateRenderer.Workout, null)]
    public void Wrap_NoteRow_CarriesItsIconOnlyWhereTheTemplateHasOne(string template, string? noteIcon)
    {
        var html = Renderer().Wrap(template, "en");

        if (noteIcon is null)
        {
            html.Should().NotContain("/email/icons/note-");
            return;
        }

        html.Should().Contain($"{IconsUrl}{noteIcon}-light.png");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void Wrap_NoAssetBaseUrl_LeavesOutEveryIconAndTile(string? assetBaseUrl)
    {
        foreach (var template in new[]
                 {
                     EmailTemplateRenderer.EmailVerification, EmailTemplateRenderer.PasswordReset, EmailTemplateRenderer.ReauthCode,
                     EmailTemplateRenderer.Invitation, EmailTemplateRenderer.PaymentRequest, EmailTemplateRenderer.BankAccountChanged,
                     EmailTemplateRenderer.NewMessage, EmailTemplateRenderer.CheckIn, EmailTemplateRenderer.JoinRequest,
                     EmailTemplateRenderer.Workout, EmailTemplateRenderer.Photos
                 })
        {
            var html = Renderer(assetBaseUrl).Wrap(template, "en");

            html.Should().NotContain("/email/icons/").And.NotContain("width=\"48\"").And.NotContain("fu-ic-light\"")
                .And.NotContain("{{HeaderIcon}}").And.NotContain("{{NoteIcon}}");
        }
    }

    [Fact]
    public void RenderInvitation_WithPackage_ShowsThePackageIconOnlyWithAnAssetBaseUrl()
    {
        var package = new InvitationPackageInfo("Online", PackageBillingInterval.Monthly, null);

        Renderer().RenderInvitation("Jan Novák", "https://app.example.com/i", "en", null, package).Html
            .Should().Contain($"{IconsUrl}package-light.png");
        Renderer(assetBaseUrl: null).RenderInvitation("Jan Novák", "https://app.example.com/i", "en", null, package).Html
            .Should().NotContain("package-light.png");
    }

    [Fact]
    public void IconFileNames_AllExistInTheStaticAssetsFolder()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "FormUp.Application", "wwwroot")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the source tree is the only place wwwroot lives for a test run");

        var icons = Path.Combine(directory!.FullName, "FormUp.Application", "wwwroot", "email", "icons");

        EmailTemplateRenderer.IconFileNames.Should().NotBeEmpty();

        foreach (var file in EmailTemplateRenderer.IconFileNames)
        {
            File.Exists(Path.Combine(icons, file)).Should().BeTrue($"{file} must be served from /email/icons/");
        }
    }
}
