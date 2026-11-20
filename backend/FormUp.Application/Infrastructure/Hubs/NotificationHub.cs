using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Infrastructure.Hubs;

[Authorize]
public class NotificationHub(
    PresenceTracker presence,
    IServiceScopeFactory scopeFactory) : Hub
{
    /// <summary>Group every connected admin belongs to; receives the admin-only events.</summary>
    public const string AdminGroup = "admins";

    public override async Task OnConnectedAsync()
    {
        var userId = Context.User?.FindFirst(AppClaims.UserId)?.Value;
        if (userId is not null)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, userId);

            if (Context.User?.IsInRole(AppRoles.Admin) == true)
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, AdminGroup);
            }

            presence.UserConnected(userId);
            await BroadcastPresenceAsync(userId, true);
        }
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = Context.User?.FindFirst(AppClaims.UserId)?.Value;
        if (userId is not null)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, userId);

            if (Context.User?.IsInRole(AppRoles.Admin) == true)
            {
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, AdminGroup);
            }

            presence.UserDisconnected(userId);
            if (!presence.IsOnline(userId))
            {
                await BroadcastPresenceAsync(userId, false);
            }
        }
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Called by clients to notify the other participant that they are typing.
    /// </summary>
    public async Task SendTyping(string conversationId)
    {
        var userId = Context.User?.FindFirst(AppClaims.UserId)?.Value;
        if (userId is null) return;

        // Fail soft on a malformed conversationId, matching the rest of this hub
        // (which returns on a null/unresolved userId rather than throwing) — see #663.
        if (!Guid.TryParse(conversationId, out var conversationGuid)) return;

        var userGuid = Guid.Parse(userId);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();

        var conversation = await db.Conversations
            .AsNoTracking()
            .FirstOrDefaultAsync(c =>
                c.PublicId == conversationGuid &&
                (c.ProfessionalUserId == userGuid || c.ClientUserId == userGuid));

        if (conversation is null) return;

        // No typing either way in an invite-only thread: the inviter must not learn the invitee is
        // active, and the inviter cannot send there anyway.
        var access = await ProfessionalSendGuard.EvaluateAsync(
            db, conversation.ProfessionalUserId, conversation.ClientUserId, CancellationToken.None);

        if (access == ProfessionalSendAccess.Locked) return;

        var recipientId = conversation.ProfessionalUserId == userGuid
            ? conversation.ClientUserId
            : conversation.ProfessionalUserId;

        await Clients.Group(recipientId.ToString()).SendAsync("typing", new
        {
            conversationId,
            senderId = userId,
        });
    }

    /// <summary>
    /// Notify all conversation partners about a user's online/offline status.
    /// </summary>
    private async Task BroadcastPresenceAsync(string userId, bool isOnline)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();

        var userGuid = Guid.Parse(userId);

        // Find all conversation partners
        var partnerIds = await db.Conversations
            .AsNoTracking()
            .Where(c => c.ProfessionalUserId == userGuid || c.ClientUserId == userGuid)
            .Select(c => c.ProfessionalUserId == userGuid ? c.ClientUserId : c.ProfessionalUserId)
            .Distinct()
            .ToListAsync();

        // The user's presence is withheld from professionals whose thread with them is
        // invite-only (no link, no join request): they must not see the invitee online.
        var professionalPartnerIds = await db.Conversations
            .AsNoTracking()
            .Where(c => c.ClientUserId == userGuid)
            .Select(c => c.ProfessionalUserId)
            .Distinct()
            .ToListAsync();

        var lockedProfessionals = await ProfessionalSendGuard.FindLockedProfessionalsAsync(
            db, userGuid, professionalPartnerIds, CancellationToken.None);

        partnerIds = partnerIds
            .Where(partnerId => !lockedProfessionals.Contains(partnerId))
            .ToList();

        var payload = new { userId, isOnline };

        foreach (var partnerId in partnerIds)
        {
            await Clients.Group(partnerId.ToString()).SendAsync("userPresence", payload);
        }
    }
}
