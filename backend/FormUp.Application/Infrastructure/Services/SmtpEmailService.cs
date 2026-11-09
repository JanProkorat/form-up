using System.Net;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using MailKit.Net.Smtp;
using MimeKit;
using Polly;
using Polly.Retry;

namespace FormUp.Application.Infrastructure.Services;

/// <summary>
/// SMTP-based email service implementation using MailKit.
/// Loads localized HTML templates from embedded resources.
/// Compatible with MailHog (no authentication) for local development.
/// Includes Polly retry logic (3 attempts with exponential backoff).
/// </summary>
public class SmtpEmailService(IConfiguration configuration, ILogger<SmtpEmailService> logger) : IEmailService
{
    private readonly EmailTemplateRenderer _renderer = new(configuration);

    /// <summary>
    /// Polly retry pipeline: 3 retries with exponential backoff (1s, 2s, 4s).
    /// </summary>
    private static readonly ResiliencePipeline RetryPipeline = new ResiliencePipelineBuilder()
        .AddRetry(new RetryStrategyOptions
        {
            MaxRetryAttempts = 3,
            BackoffType = DelayBackoffType.Exponential,
            Delay = TimeSpan.FromSeconds(1)
        })
        .Build();

    /// <inheritdoc />
    public async Task SendInvitationEmailAsync(string toEmail, string trainerName, string invitationToken, string language, string? personalMessage, InvitationPackageInfo? package, CancellationToken ct, InvitationCoachRole? coachRole = null)
    {
        var lang = EmailTemplateRenderer.NormalizeLanguage(language);
        var acceptUrl = $"{BaseUrl}/invite/accept?token={WebUtility.UrlEncode(invitationToken)}";
        var (subject, html) = _renderer.RenderInvitation(trainerName, acceptUrl, lang, personalMessage, package, coachRole);

        await SendEmailAsync(toEmail, subject, html, ct);

        logger.LogInformation("Invitation email sent to {Email} from trainer {TrainerName} (lang={Language})", toEmail, trainerName, lang);
    }

    /// <inheritdoc />
    public async Task SendPasswordResetEmailAsync(string toEmail, string resetToken, string language, CancellationToken ct)
    {
        var lang = EmailTemplateRenderer.NormalizeLanguage(language);
        var resetUrl = $"{BaseUrl}/auth/reset-password?token={WebUtility.UrlEncode(resetToken)}&email={WebUtility.UrlEncode(toEmail)}";
        var (subject, html) = _renderer.Render(
            EmailTemplateRenderer.PasswordReset,
            lang,
            new Dictionary<string, string> { ["ResetUrl"] = resetUrl, ["Email"] = toEmail });

        await SendEmailAsync(toEmail, subject, html, ct);

        logger.LogInformation("Password reset email sent to {Email} (lang={Language})", toEmail, lang);
    }

    /// <inheritdoc />
    public async Task SendEmailVerificationAsync(string toEmail, string verificationToken, string language, CancellationToken ct)
    {
        var lang = EmailTemplateRenderer.NormalizeLanguage(language);
        var verifyUrl = $"{BaseUrl}/verify-email?token={WebUtility.UrlEncode(verificationToken)}";
        var (subject, html) = _renderer.Render(
            EmailTemplateRenderer.EmailVerification,
            lang,
            new Dictionary<string, string> { ["VerifyUrl"] = verifyUrl, ["Email"] = toEmail });

        await SendEmailAsync(toEmail, subject, html, ct);

        logger.LogInformation("Email verification sent to {Email} (lang={Language})", toEmail, lang);
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

        logger.LogInformation("Re-authentication code email sent to {Email} (lang={Language})", toEmail, lang);
    }

    /// <inheritdoc />
    public async Task SendBankAccountChangedEmailAsync(
        string toEmail, string maskedNewIban, string maskedPreviousIban, string undoToken, string language, CancellationToken ct)
    {
        var lang = EmailTemplateRenderer.NormalizeLanguage(language);
        var undoUrl = $"{BaseUrl}/bank-account/undo?token={WebUtility.UrlEncode(undoToken)}";
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

        logger.LogInformation("Bank account changed email sent to {Email} (lang={Language})", toEmail, lang);
    }

    /// <inheritdoc />
    public async Task SendPaymentRequestEmailAsync(PaymentRequestEmail email, CancellationToken ct)
    {
        var lang = EmailTemplateRenderer.NormalizeLanguage(email.Language);
        var html = PaymentMessageComposer.RenderEmailHtml(_renderer.Wrap(EmailTemplateRenderer.PaymentRequest, lang), email, lang, BaseUrl);
        var subject = PaymentMessageComposer.EmailSubject(email, lang);

        await SendEmailAsync(email.ToEmail, subject, html, ct, (email.QrContentId, email.QrPng));

        logger.LogInformation("Payment request email sent (lang={Language})", lang);
    }

    /// <inheritdoc />
    public async Task SendNotificationEmailAsync(NotificationEmailWorkItem item, CancellationToken ct)
    {
        var (subject, html) = _renderer.RenderNotification(item);

        await SendEmailAsync(item.Email, subject, html, ct, headers: _renderer.NotificationHeaders(item));

        logger.LogInformation("Notification email {Event} sent to user {UserId}", item.Event, item.UserId);
    }

    private string BaseUrl => configuration[ConfigKeys.AppBaseUrl] ?? "http://localhost:5173";

    /// <summary>
    /// Sends an email message via SMTP with Polly retry logic.
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
        var smtpHost = configuration[ConfigKeys.EmailSmtpHost] ?? "localhost";
        var smtpPort = int.TryParse(configuration[ConfigKeys.EmailSmtpPort], out var port) ? port : 1025;
        var fromAddress = configuration[ConfigKeys.EmailFromAddress] ?? "noreply@formup.local";
        var fromName = configuration[ConfigKeys.EmailFromName] ?? "Form Up";

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(fromName, fromAddress));
        message.To.Add(MailboxAddress.Parse(toEmail));
        message.Subject = subject;

        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            message.Headers.Add(name, value);
        }

        if (inlineImage is { } image)
        {
            var builder = new BodyBuilder { HtmlBody = htmlBody };
            var resource = builder.LinkedResources.Add("payment-qr.png", image.Png, ContentType.Parse("image/png"));
            resource.ContentId = image.ContentId;
            message.Body = builder.ToMessageBody();
        }
        else
        {
            message.Body = new TextPart("html")
            {
                Text = htmlBody
            };
        }

        await RetryPipeline.ExecuteAsync(async token =>
        {
            using var client = new SmtpClient();
            await client.ConnectAsync(smtpHost, smtpPort, MailKit.Security.SecureSocketOptions.None, token);
            await client.SendAsync(message, token);
            await client.DisconnectAsync(true, token);
        }, ct);
    }
}
