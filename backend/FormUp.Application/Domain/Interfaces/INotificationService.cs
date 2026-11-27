using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;

namespace FormUp.Application.Domain.Interfaces;

/// <summary>
/// Service for creating in-app notifications and for push-only delivery.
/// </summary>
public interface INotificationService
{
    /// <summary>
    /// Creates an in-app notification (and its push) for the specified user, localized to the recipient's
    /// stored <see cref="Entities.ApplicationUser.Language"/> (falls back to English
    /// when unset — #788). The same localized title/body are used for both the
    /// persisted in-app notification and the push notification, since the OS-level
    /// push banner must already be in the right language at send time.
    /// </summary>
    /// <param name="recipientUserId">The recipient's ApplicationUser.Id.</param>
    /// <param name="type">Type of notification — selects the title/body template.</param>
    /// <param name="parameters">
    /// Interpolation values for the template's <c>{key}</c> placeholders (e.g.
    /// <c>{ ["clientName"] = "Petra Nováková" }</c>). Also persisted verbatim as JSON in
    /// <see cref="Entities.Notification.Data"/> so any extra keys (e.g. an id needed for
    /// client-side deep-linking) ride along even if the current templates don't use them.
    /// </param>
    /// <param name="variant">
    /// Distinguishes multiple title/body wordings under the same <paramref name="type"/> —
    /// see the named constants on <see cref="Infrastructure.Services.NotificationTemplates"/>.
    /// Null for types with a single wording.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The persisted <see cref="Notification"/> entity — callers that need to broadcast
    /// its id (e.g. schedulers emitting a SignalR "newnotification" event) can read it
    /// off the return value instead of re-querying.
    /// </returns>
    Task<Notification> CreateAsync(
        Guid recipientUserId,
        NotificationType type,
        IReadOnlyDictionary<string, string>? parameters = null,
        string? variant = null,
        CancellationToken ct = default);

    /// <summary>
    /// Writes the in-app <see cref="Notification"/> row exactly like <see cref="CreateAsync"/> but sends
    /// no push; the caller sends it later with <see cref="SendPushOnlyAsync"/>, e.g. after a transaction commits.
    /// </summary>
    /// <param name="recipientUserId">The recipient's ApplicationUser.Id.</param>
    /// <param name="type">Type of notification — selects the title/body template.</param>
    /// <param name="parameters">Template values; persisted as JSON in the notification's data.</param>
    /// <param name="variant">Optional template wording variant.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The persisted <see cref="Notification"/> entity.</returns>
    Task<Notification> CreateWithoutPushAsync(
        Guid recipientUserId,
        NotificationType type,
        IReadOnlyDictionary<string, string>? parameters = null,
        string? variant = null,
        CancellationToken ct = default);

    /// <summary>
    /// Sends only the push notification (localized, subject to the recipient's push preference)
    /// and writes no <see cref="Notification"/> row. Same parameters and push data as
    /// <see cref="CreateAsync"/>.
    /// </summary>
    /// <param name="recipientUserId">The recipient's ApplicationUser.Id.</param>
    /// <param name="type">Type of notification — selects the title/body template.</param>
    /// <param name="parameters">Template values; also sent as push data.</param>
    /// <param name="variant">Optional template wording variant.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SendPushOnlyAsync(
        Guid recipientUserId,
        NotificationType type,
        IReadOnlyDictionary<string, string>? parameters = null,
        string? variant = null,
        CancellationToken ct = default);
}
