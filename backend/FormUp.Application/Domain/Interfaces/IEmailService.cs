using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Domain.Interfaces;

/// <summary>
/// Provides methods for sending transactional emails.
/// </summary>
public interface IEmailService
{
    /// <summary>
    /// Sends a client invitation email containing a link to accept the invitation.
    /// </summary>
    /// <param name="toEmail">The recipient's email address.</param>
    /// <param name="trainerName">The name of the trainer who sent the invitation.</param>
    /// <param name="invitationToken">The unique token for accepting the invitation.</param>
    /// <param name="language">Two-letter language code (en, cs, de). Defaults to en.</param>
    /// <param name="personalMessage">Optional personal message from the trainer to include in the email.</param>
    /// <param name="package">Optional package the invitation offers; the email names it without a price.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="coachRole">Discipline(s) the invitation grants, shown under the coach's name; null shows no role line.</param>
    Task SendInvitationEmailAsync(string toEmail, string trainerName, string invitationToken, string language, string? personalMessage, InvitationPackageInfo? package, CancellationToken ct, InvitationCoachRole? coachRole = null);

    /// <summary>
    /// Sends a password reset email containing a link to reset the user's password.
    /// </summary>
    /// <param name="toEmail">The recipient's email address.</param>
    /// <param name="resetToken">The password reset token.</param>
    /// <param name="language">Two-letter language code (en, cs, de). Defaults to en.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SendPasswordResetEmailAsync(string toEmail, string resetToken, string language, CancellationToken ct);

    /// <summary>
    /// Sends an email verification email containing a link to verify the user's email address.
    /// </summary>
    /// <param name="toEmail">The recipient's email address.</param>
    /// <param name="verificationToken">The verification token.</param>
    /// <param name="language">Two-letter language code (en, cs, de). Defaults to en.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SendEmailVerificationAsync(string toEmail, string verificationToken, string language, CancellationToken ct);

    /// <summary>
    /// Sends a one-time code that re-authenticates a user without a password before a bank account change.
    /// </summary>
    /// <param name="toEmail">The recipient's email address.</param>
    /// <param name="code">The one-time code.</param>
    /// <param name="language">Two-letter language code (en, cs, de). Defaults to en.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SendReauthCodeEmailAsync(string toEmail, string code, string language, CancellationToken ct);

    /// <summary>
    /// Notifies a coach that their bank account changed, with a link to undo it.
    /// </summary>
    /// <param name="toEmail">The recipient's email address.</param>
    /// <param name="maskedNewIban">The new IBAN, masked.</param>
    /// <param name="maskedPreviousIban">The previous IBAN, masked.</param>
    /// <param name="undoToken">The single-use undo token.</param>
    /// <param name="language">Two-letter language code (en, cs, de). Defaults to en.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SendBankAccountChangedEmailAsync(
        string toEmail, string maskedNewIban, string maskedPreviousIban, string undoToken, string language, CancellationToken ct);

    /// <summary>
    /// Asks a client to pay: the amount, the bank details and a QR Platba code attached inline.
    /// </summary>
    /// <param name="email">The resolved email content.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SendPaymentRequestEmailAsync(PaymentRequestEmail email, CancellationToken ct);

    /// <summary>
    /// Sends a coach notification email (check-in, join request, photos, workout) in the item's language.
    /// </summary>
    /// <param name="item">The queued notification with its template values.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SendNotificationEmailAsync(NotificationEmailWorkItem item, CancellationToken ct);
}
