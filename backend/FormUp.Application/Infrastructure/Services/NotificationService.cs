using System.Text.Json;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Infrastructure.Services;

/// <summary>
/// Creates notifications in PostgreSQL and sends push notifications to mobile devices.
/// Resolves title/body from <see cref="NotificationTemplates"/> using the recipient's
/// stored language (falls back to English) so notifications are localized regardless of
/// which user/process triggered the notification (#788).
/// </summary>
public class NotificationService(IApplicationDbContext db, IPushNotificationService push) : INotificationService
{
    /// <inheritdoc />
    public async Task<Notification> CreateAsync(
        Guid recipientUserId,
        NotificationType type,
        IReadOnlyDictionary<string, string>? parameters = null,
        string? variant = null,
        CancellationToken ct = default)
    {
        var (notification, title, body) = await SaveNotificationAsync(recipientUserId, type, parameters, variant, ct);

        // Send push notification to the user's mobile devices. Uses the SAME resolved
        // title/body as the in-app notification — the OS push banner renders before the
        // app opens, so it must already be in the recipient's language at send time.
        await PushIfEnabledAsync(recipientUserId, type, title, body, parameters, ct);

        return notification;
    }

    /// <inheritdoc />
    public async Task<Notification> CreateWithoutPushAsync(
        Guid recipientUserId,
        NotificationType type,
        IReadOnlyDictionary<string, string>? parameters = null,
        string? variant = null,
        CancellationToken ct = default)
    {
        var (notification, _, _) = await SaveNotificationAsync(recipientUserId, type, parameters, variant, ct);

        return notification;
    }

    private async Task<(Notification Notification, string Title, string Body)> SaveNotificationAsync(
        Guid recipientUserId,
        NotificationType type,
        IReadOnlyDictionary<string, string>? parameters,
        string? variant,
        CancellationToken ct)
    {
        var (title, body) = await ResolveTemplateAsync(recipientUserId, type, parameters, variant, ct);

        var notification = new Notification
        {
            RecipientUserId = recipientUserId,
            Type = type,
            Title = title,
            Body = body,
            Data = parameters is { Count: > 0 } ? JsonSerializer.Serialize(parameters) : null
        };

        db.Notifications.Add(notification);
        await db.SaveChangesAsync(ct);

        return (notification, title, body);
    }

    /// <inheritdoc />
    public async Task SendPushOnlyAsync(
        Guid recipientUserId,
        NotificationType type,
        IReadOnlyDictionary<string, string>? parameters = null,
        string? variant = null,
        CancellationToken ct = default)
    {
        var (title, body) = await ResolveTemplateAsync(recipientUserId, type, parameters, variant, ct);

        await PushIfEnabledAsync(recipientUserId, type, title, body, parameters, ct);
    }

    private async Task<(string Title, string Body)> ResolveTemplateAsync(
        Guid recipientUserId,
        NotificationType type,
        IReadOnlyDictionary<string, string>? parameters,
        string? variant,
        CancellationToken ct)
    {
        var recipientLanguage = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == recipientUserId)
            .Select(u => u.Language)
            .FirstOrDefaultAsync(ct);

        return NotificationTemplates.Resolve(type, recipientLanguage, parameters, variant);
    }

    private async Task PushIfEnabledAsync(
        Guid recipientUserId,
        NotificationType type,
        string title,
        string body,
        IReadOnlyDictionary<string, string>? parameters,
        CancellationToken ct)
    {
        if (!await IsPushEnabledAsync(recipientUserId, type, ct))
        {
            return;
        }

        // The notification's own data (e.g. inviteId) rides along so a tapped push can deep-link;
        // "type" is written last so a parameter can never override it.
        var pushData = new Dictionary<string, string>(parameters ?? new Dictionary<string, string>())
        {
            ["type"] = type.ToString(),
        };

        await push.SendAsync(recipientUserId, title, body, pushData, ct);
    }

    private static NotificationEvent? MapToPreferenceEvent(NotificationType type) => type switch
    {
        NotificationType.ClientRequestReceived => NotificationEvent.JoinRequest,
        NotificationType.NewMessage => NotificationEvent.NewMessage,
        NotificationType.ProgressPhotosSubmitted => NotificationEvent.ProgressPhotosSubmitted,
        NotificationType.WorkoutFinished => NotificationEvent.WorkoutFinished,
        _ => null,
    };

    private async Task<bool> IsPushEnabledAsync(Guid recipientUserId, NotificationType type, CancellationToken ct)
    {
        if (MapToPreferenceEvent(type) is not { } preferenceEvent)
        {
            return true;
        }

        var saved = await db.NotificationPreferences
            .AsNoTracking()
            .Where(p => p.UserId == recipientUserId && p.Event == preferenceEvent)
            .Select(p => (bool?)p.PushEnabled)
            .FirstOrDefaultAsync(ct);

        return saved ?? NotificationPreferenceDefaults.For(preferenceEvent).Push;
    }
}
