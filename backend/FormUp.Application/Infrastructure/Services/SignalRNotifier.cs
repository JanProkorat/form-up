using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Infrastructure.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace FormUp.Application.Infrastructure.Services;

public class SignalRNotifier(IHubContext<NotificationHub> hubContext) : IRealtimeNotifier
{
    public async Task NotifyAsync(Guid userId, string eventType, object payload, CancellationToken ct = default)
    {
        await hubContext.Clients.Group(userId.ToString()).SendAsync(eventType, payload, ct);
    }

    public async Task NotifyAdminsAsync(string eventType, object payload, CancellationToken ct = default)
    {
        await hubContext.Clients.Group(NotificationHub.AdminGroup).SendAsync(eventType, payload, ct);
    }
}
