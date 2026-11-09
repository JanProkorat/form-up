using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Domain.Interfaces;

/// <summary>
/// Background work item for a client-invitation email send (#1109). Dispatched by
/// <c>EmailDispatchWorker</c> to <see cref="IEmailService.SendInvitationEmailAsync"/>.
/// Moving the send off the request path means an SMTP failure can no longer turn a
/// successful invite creation into a 500, or skip the notification, realtime event, and
/// cooperation-event seed that already happened synchronously before this is enqueued.
/// </summary>
/// <param name="Email">Recipient email address.</param>
/// <param name="TrainerName">Display name of the inviting professional.</param>
/// <param name="Token">The already-persisted invitation token value to send.</param>
/// <param name="Language">Two-letter language code (en, cs, de) for the email template.</param>
/// <param name="PersonalMessage">Optional personal message included in the invite.</param>
/// <param name="Package">Optional package the invitation offers; named in the email without a price.</param>
/// <param name="CoachRole">Discipline(s) the invitation grants; null leaves the role line out of the email.</param>
public sealed record InvitationEmailWorkItem(
    string Email,
    string TrainerName,
    string Token,
    string Language,
    string? PersonalMessage,
    InvitationPackageInfo? Package = null,
    InvitationCoachRole? CoachRole = null)
    : EmailDispatchWorkItem(Email, Language);
