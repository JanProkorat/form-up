namespace FormUp.Application.Domain.Interfaces;

public interface IRealtimeNotifier
{
    Task NotifyAsync(Guid userId, string eventType, object payload, CancellationToken ct = default);

    /// <summary>Sends an event to every connected admin.</summary>
    Task NotifyAdminsAsync(string eventType, object payload, CancellationToken ct = default);
}
