using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;

namespace FormUp.Application.Infrastructure.Services;

/// <summary>
/// Turns an email template into a finished message: loads the localized body, wraps it in the shared layout
/// (logo, card, footer, hidden preheader) and fills the placeholders. Used by both email providers.
/// </summary>
/// <param name="configuration">Application configuration; supplies the public asset base URL for the logo.</param>
public sealed partial class EmailTemplateRenderer(IConfiguration configuration)
{
    /// <summary>Template name of the email address verification email.</summary>
    public const string EmailVerification = "EmailVerification";

    /// <summary>Template name of the password reset email.</summary>
    public const string PasswordReset = "PasswordReset";

    /// <summary>Template name of the client invitation email.</summary>
    public const string Invitation = "Invitation";

    /// <summary>Template name of the payment request email.</summary>
    public const string PaymentRequest = "PaymentRequest";

    /// <summary>Template name of the bank account changed notice.</summary>
    public const string BankAccountChanged = "BankAccountChanged";

    /// <summary>Template name of the bank account re-authentication code email.</summary>
    public const string ReauthCode = "ReauthCode";

    /// <summary>Template name of the weekly check-in submitted email.</summary>
    public const string CheckIn = "CheckIn";

    /// <summary>Template name of the join request email.</summary>
    public const string JoinRequest = "JoinRequest";

    /// <summary>Template name of the progress photos submitted email.</summary>
    public const string Photos = "Photos";

    /// <summary>Template name of the workout finished email.</summary>
    public const string Workout = "Workout";

    /// <summary>Template name of the grouped new-message email.</summary>
    public const string NewMessage = "NewMessage";

    /// <summary>Name of the header that points a mail client at the one-click unsubscribe link.</summary>
    public const string ListUnsubscribeHeader = "List-Unsubscribe";

    /// <summary>Name of the header that tells a mail client the link accepts an RFC 8058 one-click post.</summary>
    public const string ListUnsubscribePostHeader = "List-Unsubscribe-Post";

    /// <summary>Value of <see cref="ListUnsubscribePostHeader"/>.</summary>
    public const string ListUnsubscribePostValue = "List-Unsubscribe=One-Click";

    private const string ResourcePrefix = "FormUp.Application.Infrastructure.EmailTemplates.";
    private const string LogoPath = "/email/formup-logo.png";

    private const string IconsPath = "/email/icons/";

    // Icon file stem, light tile background and icon size (px) per template; the dark background is in Layout.*.html.
    private static readonly Dictionary<string, (string Icon, string LightBackground, int Size)> HeaderIcons = new()
    {
        [EmailVerification] = ("mail", "#EFF7E6", 20),
        [PasswordReset] = ("lock", "#FFE8D2", 20),
        [ReauthCode] = ("lock", "#FFE8D2", 20),
        [Invitation] = ("user-plus", "#FDE4E1", 20),
        [PaymentRequest] = ("banknote", "#FFE8D2", 17),
        [BankAccountChanged] = ("banknote", "#FFE8D2", 17),
        [NewMessage] = ("message", "#E3EBFA", 20),
        [CheckIn] = ("activity", "#FDE4E1", 20),
        [JoinRequest] = ("inbox", "#EEE7FA", 20),
        [Workout] = ("dumbbell", "#FFE8D2", 20),
        [Photos] = ("camera", "#F8E4EF", 20)
    };

    // Icon file stem of the note row's leading icon, for templates that have such a row.
    private static readonly Dictionary<string, string> NoteIcons = new()
    {
        [EmailVerification] = "note-clock",
        [PasswordReset] = "note-clock",
        [Photos] = "note-shield"
    };

    private static readonly HashSet<string> SupportedLanguages = ["en", "cs", "de"];

    /// <summary>
    /// Every icon file the renderer can reference (light and dark variants, tile, note and package icons).
    /// </summary>
    internal static IEnumerable<string> IconFileNames =>
        HeaderIcons.Values.Select(entry => "tile-" + entry.Icon)
            .Concat(NoteIcons.Values)
            .Append("package")
            .Distinct()
            .SelectMany(stem => new[] { $"{stem}-light.png", $"{stem}-dark.png" });

    /// <summary>
    /// A light icon image followed by its hidden dark twin; the layout's dark-mode block swaps them.
    /// </summary>
    /// <param name="assetBaseUrl">Public asset base URL without a trailing slash.</param>
    /// <param name="stem">Icon file stem, without the -light/-dark suffix.</param>
    /// <param name="size">Rendered width and height in pixels.</param>
    /// <param name="extraStyle">Extra CSS appended to both images.</param>
    internal static string IconPair(string assetBaseUrl, string stem, int size, string extraStyle)
    {
        string Image(string variant, string cssClass, string display) =>
            $"""<img class="{cssClass}" src="{WebUtility.HtmlEncode($"{assetBaseUrl}{IconsPath}{stem}-{variant}.png")}" width="{size}" height="{size}" alt="" style="{display}border:0;{extraStyle}">""";

        return Image("light", "fu-ic-light", "display:inline-block;")
               + Image("dark", "fu-ic-dark", "display:none;mso-hide:all;");
    }

    // Derives its signing key on first use, so a renderer built without a JWT secret still renders account emails.
    private readonly EmailTurnOffTokenService _tokens = new(configuration, TimeProvider.System);

    private static readonly ConcurrentDictionary<string, string> ResourceCache = new();

    private static readonly Dictionary<string, Dictionary<string, string>> Subjects = new()
    {
        [Invitation] = new()
        {
            ["en"] = "{{TrainerName}} invited you to Form Up",
            ["cs"] = "{{TrainerName}} vás zve do Form Up",
            ["de"] = "{{TrainerName}} hat Sie zu Form Up eingeladen"
        },
        [PasswordReset] = new()
        {
            ["en"] = "Reset your Form Up password",
            ["cs"] = "Obnovení hesla ve Form Up",
            ["de"] = "Form-Up-Passwort zurücksetzen"
        },
        [EmailVerification] = new()
        {
            ["en"] = "Confirm your email for Form Up",
            ["cs"] = "Potvrďte svůj e-mail pro Form Up",
            ["de"] = "Bestätigen Sie Ihre E-Mail für Form Up"
        },
        [ReauthCode] = new()
        {
            ["en"] = "Your Form Up confirmation code",
            ["cs"] = "Potvrzovací kód pro Form Up",
            ["de"] = "Ihr Bestätigungscode für Form Up"
        },
        [BankAccountChanged] = new()
        {
            ["en"] = "Your Form Up bank account was changed",
            ["cs"] = "Bankovní účet ve Form Up byl změněn",
            ["de"] = "Ihr Bankkonto bei Form Up wurde geändert"
        },
        [CheckIn] = new()
        {
            ["en"] = "{{ClientFirstName}} sent a weekly check-in",
            ["cs"] = "Nový týdenní check-in: {{ClientFirstName}}",
            ["de"] = "Neuer Wochen-Check-in: {{ClientFirstName}}"
        },
        [JoinRequest] = new()
        {
            ["en"] = "{{ClientName}} wants to work with you",
            ["cs"] = "Nová žádost o spolupráci: {{ClientName}}",
            ["de"] = "Neue Anfrage: {{ClientName}} möchte mit Ihnen zusammenarbeiten"
        },
        [Photos] = new()
        {
            ["en"] = "New photos from {{ClientFirstName}}",
            ["cs"] = "Nové fotky: {{ClientFirstName}}",
            ["de"] = "Neue Fotos von {{ClientFirstName}}"
        },
        [Workout] = new()
        {
            ["en"] = "{{ClientFirstName}} finished a workout",
            ["cs"] = "Dokončený trénink: {{ClientFirstName}}",
            ["de"] = "Training beendet: {{ClientFirstName}}"
        },
        [NewMessage] = new()
        {
            ["en"] = "{{MessageTitle}}",
            ["cs"] = "Nové zprávy: {{ClientName}} ({{MessageCount}})",
            ["de"] = "{{MessageTitle}}"
        }
    };

    /// <summary>
    /// Normalizes a language code to a supported two-letter code. Falls back to "en".
    /// </summary>
    /// <param name="language">The raw language string (e.g. "cs", "cs-CZ", "de-DE").</param>
    public static string NormalizeLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return "en";
        }

        var code = language.Length >= 2 ? language[..2].ToLowerInvariant() : language.ToLowerInvariant();

        return SupportedLanguages.Contains(code) ? code : "en";
    }

    /// <summary>
    /// Renders a template and its subject. Plain values are HTML-encoded, raw values are inserted as given,
    /// and placeholders are replaced in a single pass so a value containing a placeholder is never expanded.
    /// </summary>
    /// <param name="template">One of the template name constants.</param>
    /// <param name="language">The recipient's language; unsupported values fall back to English.</param>
    /// <param name="values">Plain-text values by placeholder name; they are HTML-encoded.</param>
    /// <param name="rawValues">Pre-built HTML (or URLs) by placeholder name, inserted as is.</param>
    /// <param name="footerLine">Optional HTML line shown above the brand line in the footer.</param>
    public (string Subject, string Html) Render(
        string template,
        string? language,
        IReadOnlyDictionary<string, string>? values = null,
        IReadOnlyDictionary<string, string>? rawValues = null,
        string footerLine = "")
    {
        var lang = NormalizeLanguage(language);
        var html = Substitute(Wrap(template, lang, footerLine), values, rawValues, encode: true);
        var subjectFormat = Subjects[template].GetValueOrDefault(lang, Subjects[template]["en"]);

        return (Substitute(subjectFormat, values, null, encode: false), html);
    }

    /// <summary>
    /// The template body inside the shared layout, with the body placeholders still unresolved.
    /// </summary>
    /// <param name="template">One of the template name constants.</param>
    /// <param name="language">A supported language code (en, cs, de).</param>
    /// <param name="footerLine">Optional HTML line shown above the brand line in the footer.</param>
    public string Wrap(string template, string language, string footerLine = "")
    {
        var lang = NormalizeLanguage(language);
        var (body, preheader, footnote) = Split(LoadResource(template, lang));
        var iconValues = new Dictionary<string, string>
        {
            ["HeaderIcon"] = HeaderIconHtml(template),
            ["NoteIcon"] = NoteIconHtml(template)
        };

        body = PlaceholderPattern().Replace(body, match => iconValues.GetValueOrDefault(match.Groups[1].Value, match.Value));

        var layoutValues = new Dictionary<string, string>
        {
            ["Logo"] = LogoHtml(),
            ["Preheader"] = preheader,
            ["FooterNote"] = footnote,
            ["FooterLine"] = footerLine.Length == 0 ? string.Empty : footerLine + "<br>"
        };

        return PlaceholderPattern().Replace(LoadResource("Layout", lang), match =>
            match.Groups[1].Value == "Body" ? body : layoutValues.GetValueOrDefault(match.Groups[1].Value, match.Value));
    }

    /// <summary>
    /// Renders the client invitation email.
    /// </summary>
    /// <param name="trainerName">Display name of the inviting coach.</param>
    /// <param name="acceptUrl">Absolute link that accepts the invitation.</param>
    /// <param name="language">The recipient's language.</param>
    /// <param name="personalMessage">The coach's optional note.</param>
    /// <param name="package">The package offered with the invitation, if any.</param>
    /// <param name="coachRole">The discipline(s) the invitation grants; null leaves the role line out.</param>
    public (string Subject, string Html) RenderInvitation(
        string trainerName, string acceptUrl, string? language, string? personalMessage, InvitationPackageInfo? package,
        InvitationCoachRole? coachRole = null)
    {
        var lang = NormalizeLanguage(language);
        var firstName = FirstName(trainerName);

        return Render(
            Invitation,
            lang,
            new Dictionary<string, string>
            {
                ["TrainerName"] = trainerName,
                ["TrainerFirstName"] = firstName,
                ["TrainerInitials"] = Initials(trainerName),
                ["AcceptUrl"] = acceptUrl
            },
            new Dictionary<string, string>
            {
                ["TrainerRoleLine"] = CoachRoleLine(coachRole, lang),
                ["PackageBlock"] = InvitationPackageBlock.Render(package, lang, AssetBaseUrl()),
                ["PersonalMessage"] = PersonalMessageBlock(personalMessage, lang, firstName)
            });
    }

    /// <summary>
    /// The template that carries an event's email, or null when the event has no email template yet.
    /// </summary>
    /// <param name="notificationEvent">The notification event.</param>
    public static string? TemplateFor(NotificationEvent notificationEvent) => notificationEvent switch
    {
        NotificationEvent.WeeklyCheckInSubmitted => CheckIn,
        NotificationEvent.JoinRequest => JoinRequest,
        NotificationEvent.ProgressPhotosSubmitted => Photos,
        NotificationEvent.WorkoutFinished => Workout,
        NotificationEvent.NewMessage => NewMessage,
        _ => null
    };

    /// <summary>
    /// Renders a coach notification email. Optional blocks (stat tiles, quoted notes, the client card) are built
    /// here from the plain values and left empty when their values are missing.
    /// </summary>
    /// <param name="item">The queued notification.</param>
    public (string Subject, string Html) RenderNotification(NotificationEmailWorkItem item)
    {
        var template = TemplateFor(item.Event)
            ?? throw new NotSupportedException($"No email template for notification event {item.Event}.");
        var lang = NormalizeLanguage(item.Language);
        var values = new Dictionary<string, string>(item.Values);
        var clientName = values.GetValueOrDefault(NotificationEmailWorkItem.ClientNameKey, string.Empty);
        var firstName = FirstName(clientName);

        values["ClientFirstName"] = firstName;
        values["ClientInitials"] = Initials(clientName);

        var raw = new Dictionary<string, string>();

        switch (item.Event)
        {
            case NotificationEvent.WeeklyCheckInSubmitted:
                raw["Tiles"] = StatTiles(Tile(values, NotificationEmailWorkItem.WeightKey, lang, "Weight", "Váha", "Gewicht"));
                break;
            case NotificationEvent.JoinRequest:
                raw["ClientCard"] = ClientCard(clientName, values.GetValueOrDefault(NotificationEmailWorkItem.PackageNameKey), lang);
                raw["MessageBlock"] = QuoteBlock(
                    values.GetValueOrDefault(NotificationEmailWorkItem.MessageKey), Localize(lang, "Message", "Zpráva", "Nachricht"));
                break;
            case NotificationEvent.ProgressPhotosSubmitted:
                values["PhotoLead"] = PhotoLead(clientName, values, lang);
                values["PhotoPreheader"] = PhotoPreheader(IsFreeForm(values), clientName, lang);
                break;
            case NotificationEvent.WorkoutFinished:
                values["WorkoutLead"] = string.Join(" · ", new[]
                    {
                        clientName,
                        values.GetValueOrDefault(NotificationEmailWorkItem.SessionNameKey),
                        values.GetValueOrDefault(NotificationEmailWorkItem.PlanNameKey)
                    }.Where(part => !string.IsNullOrWhiteSpace(part)));
                raw["Tiles"] = StatTiles(
                    Tile(values, NotificationEmailWorkItem.DurationMinutesKey, lang, "Duration", "Délka", "Dauer", " min"),
                    Tile(values, NotificationEmailWorkItem.ExerciseCountKey, lang, "Exercises", "Cviky", "Übungen"),
                    Tile(values, NotificationEmailWorkItem.SetCountKey, lang, "Sets", "Série", "Sätze"));
                raw["NoteBlock"] = QuoteBlock(
                    values.GetValueOrDefault(NotificationEmailWorkItem.NoteKey), Localize(lang, "Note", "Poznámka", "Notiz"));
                break;
            case NotificationEvent.NewMessage:
                values["MessageCount"] = MessageCount(values).ToString(CultureInfo.InvariantCulture);
                values["MessageTitle"] = MessageTitle(firstName, MessageCount(values), lang);
                break;
        }

        return Render(template, lang, values, raw, NotificationFooterLine(item, lang));
    }

    /// <summary>
    /// The headers a mail client needs to offer its own unsubscribe button for a coach notification email (RFC 8058
    /// one-click). Only these emails carry them; account, payment and security emails never do.
    /// </summary>
    /// <param name="item">The queued notification.</param>
    public IReadOnlyDictionary<string, string> NotificationHeaders(NotificationEmailWorkItem item) =>
        new Dictionary<string, string>
        {
            [ListUnsubscribeHeader] = $"<{AppBaseUrl()}/users/email-links/one-click?token={_tokens.Create(item.UserId, item.Event)}>",
            [ListUnsubscribePostHeader] = ListUnsubscribePostValue,
        };

    private string AppBaseUrl() => (configuration[ConfigKeys.AppBaseUrl] ?? "http://localhost:5173").TrimEnd('/');

    private string NotificationFooterLine(NotificationEmailWorkItem item, string language)
    {
        var baseUrl = AppBaseUrl();
        var turnOffUrl = $"{baseUrl}/email-off?token={_tokens.Create(item.UserId, item.Event)}";
        var settingsUrl = $"{baseUrl}/settings";

        return FooterLink(turnOffUrl, Localize(language, "Turn off these emails", "Vypnout tyto e-maily", "Diese E-Mails ausschalten"))
               + "&nbsp;&nbsp;&middot;&nbsp;&nbsp;"
               + FooterLink(settingsUrl, Localize(language, "Notification settings", "Nastavení oznámení", "Benachrichtigungseinstellungen"));
    }

    // The label is one of this class's own literals, so only the URL needs encoding.
    private static string FooterLink(string url, string label) =>
        $"""<a class="fu-text" href="{WebUtility.HtmlEncode(url)}" style="color:#141414;font-weight:600;">{label}</a>""";

    private static string Localize(string language, string en, string cs, string de) =>
        language switch
        {
            "cs" => cs,
            "de" => de,
            _ => en
        };

    private static (string Label, string Value)? Tile(
        IReadOnlyDictionary<string, string> values, string key, string language, string en, string cs, string de, string suffix = "") =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? (Localize(language, en, cs, de), value + suffix)
            : null;

    private static string StatTiles(params (string Label, string Value)?[] tiles)
    {
        var present = tiles.Where(tile => tile is not null).Select(tile => tile!.Value).ToList();

        if (present.Count == 0)
        {
            return string.Empty;
        }

        var cells = string.Join(
            """<td width="10" style="font-size:0;line-height:0;">&nbsp;</td>""",
            present.Select(tile => $"""
                <td class="fu-rule" valign="top" style="padding:14px 16px;border:1px solid #E7E4DE;border-radius:14px;">
                  <p class="fu-muted" style="margin:0 0 4px;font-size:12px;color:#6B6863;">{WebUtility.HtmlEncode(tile.Label)}</p>
                  <p class="fu-text" style="margin:0;font-family:'Outfit','Helvetica Neue',Helvetica,Arial,sans-serif;font-size:22px;font-weight:600;color:#141414;">{WebUtility.HtmlEncode(tile.Value)}</p>
                </td>
                """));

        return $"""
              <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="margin:0 0 24px;">
                <tr>{cells}</tr>
              </table>
              """;
    }

    private static string QuoteBlock(string? text, string label)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return $"""
              <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="margin:0 0 24px;">
                <tr>
                  <td class="fu-panel" style="background-color:#F6F4F0;border-radius:14px;padding:14px 16px;">
                    <p class="fu-muted" style="margin:0 0 6px;font-size:12px;font-weight:600;color:#6B6863;">{WebUtility.HtmlEncode(label)}</p>
                    <p class="fu-text" style="margin:0;font-size:15px;line-height:1.55;color:#141414;">&ldquo;{WebUtility.HtmlEncode(text)}&rdquo;</p>
                  </td>
                </tr>
              </table>
              """;
    }

    private static string ClientCard(string clientName, string? packageName, string language)
    {
        var detail = string.IsNullOrWhiteSpace(packageName)
            ? string.Empty
            : $"""<p class="fu-muted" style="margin:2px 0 0;font-size:13px;color:#6B6863;">{Localize(language, "Package", "Balíček", "Paket")}: {WebUtility.HtmlEncode(packageName)}</p>""";

        return $"""
              <table role="presentation" cellpadding="0" cellspacing="0" style="margin:0 0 24px;">
                <tr>
                  <td class="fu-btn" width="40" height="40" align="center" style="width:40px;height:40px;border-radius:20px;background-color:#141414;color:#FFFFFF;font-size:14px;font-weight:600;">{WebUtility.HtmlEncode(Initials(clientName))}</td>
                  <td style="padding-left:12px;">
                    <p class="fu-text" style="margin:0;font-size:15px;font-weight:700;color:#141414;">{WebUtility.HtmlEncode(clientName)}</p>
                    {detail}
                  </td>
                </tr>
              </table>
              """;
    }

    private static int MessageCount(IReadOnlyDictionary<string, string> values) =>
        int.TryParse(values.GetValueOrDefault(NotificationEmailWorkItem.MessageCountKey), NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            ? Math.Max(count, 1)
            : 1;

    // Czech never declines the name or uses a gendered past tense: the name stays in the nominative before a dot.
    private static string MessageTitle(string firstName, int count, string language)
    {
        var number = count.ToString(CultureInfo.InvariantCulture);

        return language switch
        {
            "cs" => count == 1
                ? $"{firstName} · 1 nová zpráva"
                : count is >= 2 and <= 4 ? $"{firstName} · {number} nové zprávy" : $"{firstName} · {number} nových zpráv",
            "de" => count == 1
                ? $"{firstName} hat Ihnen eine Nachricht gesendet"
                : $"{firstName} hat Ihnen {number} Nachrichten gesendet",
            _ => count == 1
                ? $"{firstName} sent you a message"
                : $"{firstName} sent you {number} messages"
        };
    }

    private static bool IsFreeForm(IReadOnlyDictionary<string, string> values) =>
        values.GetValueOrDefault(NotificationEmailWorkItem.FreeFormKey) == "true";

    private static int PhotoCount(IReadOnlyDictionary<string, string> values) =>
        int.TryParse(values.GetValueOrDefault(NotificationEmailWorkItem.PhotoCountKey), out var parsed) ? parsed : 0;

    // Free-form progress photos arrive without a request, so the copy names no request and no date.
    private static string FreeFormPhotoLead(string clientName, int count, string language) =>
        language switch
        {
            "cs" => count == 1
                ? $"{clientName} · 1 nová fotka pokroku"
                : count is >= 2 and <= 4 ? $"{clientName} · {count} nové fotky pokroku" : $"{clientName} · {count} nových fotek pokroku",
            "de" => count == 1
                ? $"{clientName} · 1 neues Fortschrittsfoto"
                : $"{clientName} · {count} neue Fortschrittsfotos",
            _ => count == 1
                ? $"{clientName} · 1 new progress photo"
                : $"{clientName} · {count} new progress photos"
        };

    // The value is HTML-encoded when substituted, so the name goes in as plain text.
    private static string PhotoPreheader(bool freeForm, string clientName, string language) =>
        (freeForm, language) switch
        {
            (true, "cs") => $"Nové fotky pokroku od: {clientName}.",
            (true, "de") => $"Neue Fortschrittsfotos von {clientName}.",
            (true, _) => $"New progress photos from {clientName}.",
            (false, "cs") => $"Nové fotky od: {clientName} jako odpověď na vaši žádost.",
            (false, "de") => $"Neue Fotos von {clientName} als Antwort auf Ihre Anfrage.",
            _ => $"New photos from {clientName}, in reply to your request."
        };

    private static string PhotoLead(string clientName, IReadOnlyDictionary<string, string> values, string language)
    {
        var count = PhotoCount(values);

        if (IsFreeForm(values))
        {
            return FreeFormPhotoLead(clientName, count, language);
        }

        // Czech agrees the verb with the number: odeslána 1 fotka, odeslány 2-4 fotky, odesláno 5 fotek.
        var summary = language switch
        {
            "cs" => count == 1
                ? "odeslána 1 fotka"
                : count is >= 2 and <= 4 ? $"odeslány {count} fotky" : $"odesláno {count} fotek",
            "de" => count == 1 ? "1 Foto" : $"{count} Fotos",
            _ => count == 1 ? "1 photo" : $"{count} photos"
        };
        var hasDate = DateOnly.TryParseExact(
            values.GetValueOrDefault(NotificationEmailWorkItem.RequestDateKey),
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsedDate);
        var requestDate = hasDate
            ? parsedDate.ToString(Localize(language, "d MMM yyyy", "d. M. yyyy", "dd.MM.yyyy"), CultureInfo.InvariantCulture)
            : string.Empty;

        return language switch
        {
            "cs" => $"{clientName} · {summary}. " + (hasDate ? $"Odpověď na vaši žádost z {requestDate}." : "Odpověď na vaši žádost o fotky."),
            "de" => $"{clientName} · {summary} gesendet. " + (hasDate ? $"Antwort auf Ihre Anfrage vom {requestDate}." : "Antwort auf Ihre Fotoanfrage."),
            _ => $"{clientName} · {summary} sent. " + (hasDate ? $"In reply to your request from {requestDate}." : "In reply to your photo request.")
        };
    }

    /// <summary>
    /// The quoted note from the coach; empty when there is no note.
    /// </summary>
    /// <param name="message">The note, or null.</param>
    /// <param name="language">A supported language code (en, cs, de).</param>
    /// <param name="trainerFirstName">The coach's first name for the label.</param>
    internal static string PersonalMessageBlock(string? message, string language, string trainerFirstName)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        var label = language switch
        {
            "cs" => "Osobní zpráva",
            "de" => "Persönliche Nachricht",
            _ => $"Message from {WebUtility.HtmlEncode(trainerFirstName)}"
        };

        return $"""
              <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="margin:0 0 24px;">
                <tr>
                  <td class="fu-panel" style="background-color:#F6F4F0;border-radius:14px;padding:14px 16px;">
                    <p class="fu-muted" style="margin:0 0 6px;font-size:12px;font-weight:600;color:#6B6863;">{label}</p>
                    <p class="fu-text" style="margin:0;font-size:15px;line-height:1.55;color:#141414;">&ldquo;{WebUtility.HtmlEncode(message)}&rdquo;</p>
                  </td>
                </tr>
              </table>
              """;
    }

    // The wording is one of this class's own literals, so it needs no encoding.
    private static string CoachRoleLine(InvitationCoachRole? coachRole, string language)
    {
        var text = coachRole switch
        {
            InvitationCoachRole.Trainer => Localize(language, "Personal trainer", "Osobní trenér", "Personal Trainer"),
            InvitationCoachRole.Nutritionist => Localize(language, "Nutritionist", "Nutriční poradce", "Ernährungsberater"),
            InvitationCoachRole.Both => Localize(
                language,
                "Personal trainer &amp; nutritionist",
                "Osobní trenér a nutriční poradce",
                "Personal Trainer und Ernährungsberater"),
            _ => string.Empty
        };

        return text.Length == 0
            ? string.Empty
            : $"""<p class="fu-muted" style="margin:2px 0 0;font-size:13px;color:#6B6863;">{text}</p>""";
    }

    private static string FirstName(string fullName) =>
        fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? fullName;

    private static string Initials(string fullName)
    {
        var parts = fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return parts.Length == 0
            ? string.Empty
            : string.Concat(parts.Take(2).Select(part => char.ToUpperInvariant(part[0])));
    }

    // Null when no public asset base URL is configured; every image in the email is then left out.
    private string? AssetBaseUrl()
    {
        var assetBaseUrl = configuration[ConfigKeys.EmailAssetBaseUrl]?.Trim().TrimEnd('/');

        return string.IsNullOrEmpty(assetBaseUrl) ? null : assetBaseUrl;
    }

    private string HeaderIconHtml(string template)
    {
        var assetBaseUrl = AssetBaseUrl();

        if (assetBaseUrl is null || !HeaderIcons.TryGetValue(template, out var entry))
        {
            return string.Empty;
        }

        return $"""
              <table role="presentation" cellpadding="0" cellspacing="0" style="margin:0 0 16px;">
                <tr>
                  <td class="fu-tile-{entry.Icon}" width="48" height="48" align="center" valign="middle" style="width:48px;height:48px;border-radius:14px;background-color:{entry.LightBackground};line-height:0;">{IconPair(assetBaseUrl, "tile-" + entry.Icon, entry.Size, "")}</td>
                </tr>
              </table>
              """;
    }

    private string NoteIconHtml(string template)
    {
        var assetBaseUrl = AssetBaseUrl();

        return assetBaseUrl is null || !NoteIcons.TryGetValue(template, out var stem)
            ? string.Empty
            : IconPair(assetBaseUrl, stem, 16, "vertical-align:-3px;margin-right:10px;");
    }

    private string LogoHtml()
    {
        var assetBaseUrl = AssetBaseUrl();

        if (string.IsNullOrEmpty(assetBaseUrl))
        {
            return """<span class="fu-text" style="font-size:20px;font-weight:300;letter-spacing:4px;color:#141414;">FORM <span style="color:#C8382F;">UP</span></span>""";
        }

        var source = WebUtility.HtmlEncode(assetBaseUrl + LogoPath);

        return $"""
              <table role="presentation" cellpadding="0" cellspacing="0">
                <tr>
                  <td style="padding-right:5px;line-height:0;"><img src="{source}" width="30" height="27" alt="F" style="display:block;border:0;font-size:20px;color:#C8382F;"></td>
                  <td class="fu-text" style="font-size:20px;font-weight:300;letter-spacing:4px;color:#141414;">ORM <span style="color:#C8382F;">UP</span></td>
                </tr>
              </table>
              """;
    }

    private static string Substitute(
        string text,
        IReadOnlyDictionary<string, string>? values,
        IReadOnlyDictionary<string, string>? rawValues,
        bool encode) =>
        PlaceholderPattern().Replace(text, match =>
        {
            var name = match.Groups[1].Value;

            if (rawValues is not null && rawValues.TryGetValue(name, out var raw))
            {
                return raw;
            }

            if (values is not null && values.TryGetValue(name, out var plain))
            {
                return encode ? WebUtility.HtmlEncode(plain) : plain;
            }

            return match.Value;
        });

    private static (string Body, string Preheader, string Footnote) Split(string template)
    {
        var preheader = string.Empty;
        var footnote = string.Empty;

        var body = MetaPattern().Replace(template, match =>
        {
            if (match.Groups[1].Value == "preheader")
            {
                preheader = match.Groups[2].Value;
            }
            else
            {
                footnote = match.Groups[2].Value;
            }

            return string.Empty;
        });

        return (body.Trim(), preheader, footnote);
    }

    private static string LoadResource(string name, string language)
    {
        var assembly = Assembly.GetExecutingAssembly();

        return ResourceCache.GetOrAdd($"{name}.{language}", _ =>
        {
            using var stream = assembly.GetManifestResourceStream($"{ResourcePrefix}{name}.{language}.html")
                ?? assembly.GetManifestResourceStream($"{ResourcePrefix}{name}.en.html")
                ?? throw new InvalidOperationException($"Email template '{name}.en.html' not found in embedded resources.");
            using var reader = new StreamReader(stream);

            return reader.ReadToEnd();
        });
    }

    [GeneratedRegex(@"\{\{(\w+)\}\}")]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex(@"<!--\s*(preheader|footnote):\s*(.*?)\s*-->", RegexOptions.Singleline)]
    private static partial Regex MetaPattern();
}
