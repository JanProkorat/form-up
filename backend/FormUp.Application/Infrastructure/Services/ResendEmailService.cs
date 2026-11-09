using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using Resend;

namespace FormUp.Application.Infrastructure.Services;

/// <summary>
/// Email service implementation using the Resend HTTP API.
/// Loads the same localized HTML templates from embedded resources as SmtpEmailService.
/// Intended for production use; for local development use SmtpEmailService with MailHog.
/// </summary>
public class ResendEmailService(IResend resend, IConfiguration configuration, ILogger<ResendEmailService> logger) : IEmailService
{
    private readonly EmailTemplateRenderer _renderer = new(configuration);

    /// <inheritdoc />
    public async Task SendInvitationEmailAsync(string toEmail, string trainerName, string invitationToken, string language, string? personalMessage, InvitationPackageInfo? package, CancellationToken ct, InvitationCoachRole? coachRole = null)
    {
        var lang = EmailTemplateRenderer.NormalizeLanguage(language);
        var acceptUrl = $"{BaseUrl}/invite/accept?token={System.Net.WebUtility.UrlEncode(invitationToken)}";
        var (subject, html) = _renderer.RenderInvitation(trainerName, acceptUrl, lang, personalMessage, package, coachRole);

        await SendEmailAsync(toEmail, subject, html, ct);

        logger.LogInformation("Invitation email sent via Resend to {Email} from trainer {TrainerName} (lang={Language})", toEmail, trainerName, lang);
    }

    /// <inheritdoc />
    public async Task SendPasswordResetEmailAsync(string toEmail, string resetToken, string language, CancellationToken ct)
    {
        var lang = EmailTemplateRenderer.NormalizeLanguage(language);
        var resetUrl = $"{BaseUrl}/auth/reset-password?token={System.Net.WebUtility.UrlEncode(resetToken)}&email={System.Net.WebUtility.UrlEncode(toEmail)}";
        var (subject, html) = _renderer.Render(
            EmailTemplateRenderer.PasswordReset,
            lang,
            new Dictionary<string, string> { ["ResetUrl"] = resetUrl, ["Email"] = toEmail });

        await SendEmailAsync(toEmail, subject, html, ct);

        logger.LogInformation("Password reset email sent via Resend to {Email} (lang={Language})", toEmail, lang);
    }

    /// <inheritdoc />
    public async Task SendEmailVerificationAsync(string toEmail, string verificationToken, string language, CancellationToken ct)
    {
        var lang = EmailTemplateRenderer.NormalizeLanguage(language);
        var verifyUrl = $"{BaseUrl}/verify-email?token={System.Net.WebUtility.UrlEncode(verificationToken)}";
        var (subject, html) = _renderer.Render(
            EmailTemplateRenderer.EmailVerification,
            lang,
            new Dictionary<string, string> { ["VerifyUrl"] = verifyUrl, ["Email"] = toEmail });

        await SendEmailAsync(toEmail, subject, html, ct);

        logger.LogInformation("Email verification sent via Resend to {Email} (lang={Language})", toEmail, lang);
    }

    /// <inheritdoc />
    public async Task SendReauthCodeEmailAsync(string toEmail, string code, string language, CancellationToken ct)
    {
        var lang = EmailTemplateRenderer.NormalizeLanguage(language);
        var (subject, html) = _renderer.Render(
            EmailTemplateRenderer.ReauthCode,
            lang,
            new Dictionary<string, string> { ["Code"] = code });

        await SendEmailAsync(toEmail, subject, html, ct);

        logger.LogInformation("Re-authentication code email sent via Resend to {Email} (lang={Language})", toEmail, lang);
    }

    /// <inheritdoc />
    public async Task SendBankAccountChangedEmailAsync(
        string toEmail, string maskedNewIban, string maskedPreviousIban, string undoToken, string language, CancellationToken ct)
    {
        var lang = EmailTemplateRenderer.NormalizeLanguage(language);
        var undoUrl = $"{BaseUrl}/bank-account/undo?token={System.Net.WebUtility.UrlEncode(undoToken)}";
        var (subject, html) = _renderer.Render(
            EmailTemplateRenderer.BankAccountChanged,
            lang,
            new Dictionary<string, string>
            {
                ["NewIban"] = maskedNewIban,
                ["PreviousIban"] = maskedPreviousIban,
                ["UndoUrl"] = undoUrl
            });

        await SendEmailAsync(toEmail, subject, html, ct);

        logger.LogInformation("Bank account changed email sent via Resend to {Email} (lang={Language})", toEmail, lang);
    }

    /// <inheritdoc />
    public async Task SendPaymentRequestEmailAsync(PaymentRequestEmail email, CancellationToken ct)
    {
        var lang = EmailTemplateRenderer.NormalizeLanguage(email.Language);
        var html = PaymentMessageComposer.RenderEmailHtml(_renderer.Wrap(EmailTemplateRenderer.PaymentRequest, lang), email, lang, BaseUrl);
        var subject = PaymentMessageComposer.EmailSubject(email, lang);

        await SendEmailAsync(email.ToEmail, subject, html, ct, (email.QrContentId, email.QrPng));

        logger.LogInformation("Payment request email sent via Resend (lang={Language})", lang);
    }

    /// <inheritdoc />
    public async Task SendNotificationEmailAsync(NotificationEmailWorkItem item, CancellationToken ct)
    {
        var (subject, html) = _renderer.RenderNotification(item);

        await SendEmailAsync(item.Email, subject, html, ct, headers: _renderer.NotificationHeaders(item));

        logger.LogInformation("Notification email {Event} sent via Resend to user {UserId}", item.Event, item.UserId);
    }

    private string BaseUrl => configuration[ConfigKeys.AppBaseUrl] ?? "http://localhost:5173";

    /// <summary>
    /// Sends an email message via the Resend API.
    /// </summary>
    /// <param name="toEmail">The recipient's email address.</param>
    /// <param name="subject">The email subject line.</param>
    /// <param name="htmlBody">The HTML body of the email.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="inlineImage">A PNG to embed in the HTML as <c>cid:</c>, or null for a plain HTML mail.</param>
    /// <param name="headers">Extra message headers by name, or null for none.</param>
    private async Task SendEmailAsync(
        string toEmail,
        string subject,
        string htmlBody,
        CancellationToken ct,
        (string ContentId, byte[] Png)? inlineImage = null,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        var fromAddress = configuration[ConfigKeys.EmailFromAddress] ?? "noreply@formup.local";
        var fromName = configuration[ConfigKeys.EmailFromName] ?? "Form Up";

        var message = new EmailMessage
        {
            From = $"{fromName} <{fromAddress}>",
            Subject = subject,
            HtmlBody = htmlBody
        };
        message.To.Add(toEmail);

        if (headers is { Count: > 0 })
        {
            message.Headers = headers.ToDictionary(header => header.Key, header => header.Value);
        }

        if (inlineImage is { } image)
        {
            message.Attachments =
            [
                new EmailAttachment
                {
                    Filename = "payment-qr.png",
                    Content = image.Png,
                    ContentType = "image/png",
                    ContentId = image.ContentId
                }
            ];
        }

        await resend.EmailSendAsync(message, ct);
    }
}
