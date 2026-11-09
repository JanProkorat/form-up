using System.Globalization;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace FormUp.Application.Infrastructure.Services;

/// <summary>
/// Claims one due <see cref="PendingNotificationEmail"/> burst, works out what it stands for and sends the one
/// grouped email. The row is deleted and the transaction committed before anything is queued, so delivery is at
/// most once: a crash after the commit loses the email rather than sending it twice. The email carries only the
/// sender's name and how many messages are unread, never message text, and nothing is loaded from message bodies.
/// </summary>
/// <param name="db">Application database context.</param>
/// <param name="sender">Sends the email; it checks the recipient's email switch.</param>
/// <param name="notifications">Creates the in-app notice that goes with a photo burst.</param>
/// <param name="logger">Logger. Receives ids only.</param>
public sealed class NotificationEmailGroupProcessor(
    IApplicationDbContext db,
    INotificationEmailSender sender,
    INotificationService notifications,
    ILogger<NotificationEmailGroupProcessor> logger)
{
    private sealed record PendingSend(
        Guid RecipientUserId,
        NotificationEvent Event,
        Dictionary<string, string> Values,
        NotificationType? NoticeType = null,
        Dictionary<string, string>? NoticeParameters = null);

    /// <summary>
    /// Claims and handles the earliest due burst. Call it in a fresh scope per burst.
    /// </summary>
    /// <param name="nowUtc">The current instant.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True when a burst was claimed (sent, skipped or discarded); false when nothing was due.</returns>
    public async Task<bool> ProcessNextAsync(DateTime nowUtc, CancellationToken ct)
    {
        PendingSend? send = null;
        long pendingId;
        var failed = false;

        await using (var transaction = await db.BeginTransactionAsync(ct))
        {
            var pending = await db.ClaimDuePendingNotificationEmailAsync(nowUtc, ct);

            if (pending is null)
            {
                return false;
            }

            pendingId = pending.Id;

            try
            {
                send = await BuildAsync(pending, ct);
                db.PendingNotificationEmails.Remove(pending);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "NotificationEmailGroupingScheduler: handling pending email {PendingId} failed.", pendingId);
                failed = true;
                await RollbackQuietlyAsync(transaction);

                // Forget the failed attempt's tracked Remove(pending) so nothing can replay it.
                (db as DbContext)?.ChangeTracker.Clear();
            }
        }

        if (failed)
        {
            // The claim transaction has ended, so this delete commits on its own instead of being rolled back with it.
            await DiscardAsync(pendingId, ct);

            return true;
        }

        if (send is not null)
        {
            // The notice comes first and does not depend on the email switch; the sender checks the switch itself.
            if (send.NoticeType is { } noticeType)
            {
                await CreateNoticeAsync(send, noticeType, ct);
            }

            await sender.SendAsync(send.RecipientUserId, send.Event, send.Values, ct);
        }

        return true;
    }

    private async Task CreateNoticeAsync(PendingSend send, NotificationType noticeType, CancellationToken ct)
    {
        try
        {
            await notifications.CreateAsync(send.RecipientUserId, noticeType, send.NoticeParameters, ct: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "NotificationEmailGroupingScheduler: could not create the {Event} notice for user {UserId}.",
                send.Event, send.RecipientUserId);
        }
    }

    /// <summary>
    /// Builds what to send, or null when there is nothing to send: the conversation is gone or no longer the
    /// recipient's, or the recipient has read every message of the burst.
    /// </summary>
    private async Task<PendingSend?> BuildAsync(PendingNotificationEmail pending, CancellationToken ct)
    {
        // Each grouped event has its own branch here.
        if (pending.Event == NotificationEvent.ProgressPhotosSubmitted)
        {
            return await BuildPhotosAsync(pending, ct);
        }

        if (pending.Event != NotificationEvent.NewMessage)
        {
            return null;
        }

        var conversation = await db.Conversations
            .AsNoTracking()
            .Where(c => c.PublicId == pending.GroupId && c.ProfessionalUserId == pending.RecipientUserId)
            .Select(c => new { c.Id, c.ClientUserId })
            .FirstOrDefaultAsync(ct);

        if (conversation is null)
        {
            return null;
        }

        // Only the number is read, never the text. The window is the burst itself: from its first to its last message.
        var unreadCount = await db.ChatMessages
            .AsNoTracking()
            .CountAsync(m => m.ConversationId == conversation.Id
                             && m.SenderUserId == conversation.ClientUserId
                             && m.Kind == ChatMessageKind.Text
                             && !m.IsRead
                             && m.DateCreated >= pending.FirstEventAt
                             && m.DateCreated <= pending.LastEventAt, ct);

        if (unreadCount == 0)
        {
            return null;
        }

        var client = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == conversation.ClientUserId)
            .Select(u => new { u.FirstName, u.LastName })
            .FirstOrDefaultAsync(ct);

        if (client is null)
        {
            return null;
        }

        var clientProfilePublicId = await db.ClientProfiles
            .AsNoTracking()
            .Where(cp => cp.UserId == conversation.ClientUserId)
            .Select(cp => (Guid?)cp.PublicId)
            .FirstOrDefaultAsync(ct);

        var values = new Dictionary<string, string>
        {
            [NotificationEmailWorkItem.ClientNameKey] = $"{client.FirstName} {client.LastName}".Trim(),
            [NotificationEmailWorkItem.MessageCountKey] = unreadCount.ToString(CultureInfo.InvariantCulture),
        };

        if (clientProfilePublicId is { } publicId)
        {
            values[NotificationEmailWorkItem.ClientPublicIdKey] = publicId.ToString();
        }

        return new PendingSend(pending.RecipientUserId, pending.Event, values);
    }

    /// <summary>
    /// Builds the photo email and notice, or null when there is nothing to send: the coach no longer has a live link
    /// that grants a plan domain, or no qualifying photo of the burst is still visible to them. Only the number of
    /// photos is read, never their addresses.
    /// </summary>
    private async Task<PendingSend?> BuildPhotosAsync(PendingNotificationEmail pending, CancellationToken ct)
    {
        var client = await db.ClientProfiles
            .AsNoTracking()
            .Where(cp => cp.PublicId == pending.GroupId)
            .Select(cp => new { cp.Id, cp.User.FirstName, cp.User.LastName })
            .FirstOrDefaultAsync(ct);

        if (client is null)
        {
            return null;
        }

        var linkIsLive = await db.ClientProfessionalLinks
            .AsNoTracking()
            .AnyAsync(l => l.ClientProfileId == client.Id
                           && l.ConnectionState != ConnectionState.Ended
                           && (l.CanViewNutritionPlans || l.CanViewTrainingPlans)
                           && l.ProfessionalProfile.UserId == pending.RecipientUserId, ct);

        if (!linkIsLive)
        {
            return null;
        }

        var photoCount = await db.PlanPhotos
            .AsNoTracking()
            .Where(ProgressPhotoBurst.IsFreeFormProgressPhoto)
            .CountAsync(p => p.ClientProfileId == client.Id
                             && p.DateCreated >= pending.FirstEventAt
                             && p.DateCreated <= pending.LastEventAt
                             && db.PlanPhotoViewers.Any(v => v.PlanPhotoId == p.Id && v.ProfessionalUserId == pending.RecipientUserId), ct);

        if (photoCount == 0)
        {
            return null;
        }

        var clientName = $"{client.FirstName} {client.LastName}".Trim();
        var count = photoCount.ToString(CultureInfo.InvariantCulture);

        return new PendingSend(
            pending.RecipientUserId,
            pending.Event,
            new Dictionary<string, string>
            {
                [NotificationEmailWorkItem.ClientNameKey] = clientName,
                [NotificationEmailWorkItem.PhotoCountKey] = count,
                [NotificationEmailWorkItem.FreeFormKey] = "true",
                [NotificationEmailWorkItem.ClientPublicIdKey] = pending.GroupId.ToString(),
            },
            NotificationType.ProgressPhotosSubmitted,
            new Dictionary<string, string>
            {
                ["clientName"] = clientName,
                ["photoCount"] = count,
                ["clientId"] = pending.GroupId.ToString(),
            });
    }

    private async Task RollbackQuietlyAsync(IDbContextTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "NotificationEmailGroupingScheduler: could not roll back the claim transaction.");
        }
    }

    // A row that cannot be handled is dropped so it cannot starve the ones behind it. Call it outside any transaction.
    private async Task DiscardAsync(long pendingId, CancellationToken ct)
    {
        try
        {
            await db.PendingNotificationEmails.Where(p => p.Id == pendingId).ExecuteDeleteAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "NotificationEmailGroupingScheduler: could not discard pending email {PendingId}.", pendingId);
        }
    }
}
