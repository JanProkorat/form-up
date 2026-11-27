using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace FormUp.Application.Infrastructure.Services;

/// <summary>
/// Holds the pushes and realtime events of an automation send until its transaction has committed,
/// so a rolled-back send tells the client nothing. One per scope; the caller flushes it after
/// <c>CommitAsync</c> or discards it.
/// </summary>
/// <param name="notifications">Sends the pushes.</param>
/// <param name="notifier">Broadcasts the realtime events.</param>
/// <param name="logger">Logger.</param>
public sealed class AutomationAfterCommitQueue(
    INotificationService notifications,
    IRealtimeNotifier notifier,
    ILogger<AutomationAfterCommitQueue> logger)
{
    private readonly List<Func<CancellationToken, Task>> _actions = [];

    /// <summary>Number of queued actions.</summary>
    public int Count => _actions.Count;

    /// <summary>Queues a push for the recipient; it sends no notification row.</summary>
    /// <param name="recipientUserId">The recipient's ApplicationUser.Id.</param>
    /// <param name="type">Notification type.</param>
    /// <param name="parameters">Template values.</param>
    /// <param name="variant">Optional template wording variant.</param>
    public void EnqueuePush(
        Guid recipientUserId,
        NotificationType type,
        IReadOnlyDictionary<string, string>? parameters,
        string? variant)
    {
        _actions.Add(ct => notifications.SendPushOnlyAsync(recipientUserId, type, parameters, variant, ct));
    }

    /// <summary>Queues a realtime event for the client.</summary>
    /// <param name="clientUserId">The client's ApplicationUser.Id.</param>
    /// <param name="eventName">Lowercase event name.</param>
    /// <param name="payload">Event payload.</param>
    public void EnqueueBroadcast(Guid clientUserId, string eventName, object payload)
    {
        _actions.Add(ct => notifier.NotifyAsync(clientUserId, eventName, payload, ct));
    }

    /// <summary>Drops everything queued without sending it.</summary>
    public void Discard() => _actions.Clear();

    /// <summary>
    /// Runs everything queued, one action at a time. A failing action is logged and never rethrown:
    /// the send is already committed.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task FlushAsync(CancellationToken ct)
    {
        var pending = _actions.ToList();
        _actions.Clear();

        foreach (var action in pending)
        {
            try
            {
                await action(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "AutomationScheduler: a push or realtime event after commit failed.");
            }
        }
    }
}
