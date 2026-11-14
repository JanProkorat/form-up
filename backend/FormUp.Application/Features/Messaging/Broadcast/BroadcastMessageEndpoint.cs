using FormUp.Application.Domain.Authorization;
using FormUp.Application.Domain.Enums;
using System.Security.Claims;
using System.Text.RegularExpressions;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Extensions;
using FormUp.Application.Domain.Interfaces;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace FormUp.Application.Features.Messaging.Broadcast;

/// <summary>
/// Broadcasts the same text message to several of the caller's clients at once, reusing the
/// existing conversation and SignalR machinery a single chat message already takes rather than
/// introducing a new messaging path.
/// </summary>
/// <param name="db">Relational database context — resolves recipient ids and names.</param>
/// <param name="linkAuthorizationService">Resolves the caller's active client links.</param>
/// <param name="conversationSeedService">
/// Shared get-or-create-conversation + append-message + broadcast "newmessage" seam.
/// </param>
/// <param name="notifier">
/// Raises "conversationunarchived" for a recipient whose thread auto-unarchives, mirroring
/// <c>SendMessageEndpoint</c>'s single-send path.
/// </param>
[RemovedCoachRole(RemovedCoachRoleMode.Refuse)]
public class BroadcastMessageEndpoint(
    IApplicationDbContext db,
    IClientLinkAuthorizationService linkAuthorizationService,
    IConversationSeedService conversationSeedService,
    IRealtimeNotifier notifier)
    : Endpoint<BroadcastMessageRequest, BroadcastMessageResponse>
{
    private static readonly Regex TemplatePlaceholderPattern =
        new("\\{\\{(firstName|fullName)\\}\\}", RegexOptions.Compiled);

    /// <inheritdoc />
    public override void Configure()
    {
        Post("/trainer/broadcast");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Broadcast a message to several clients";
            s.Description = "Sends the same text message to several of the caller's clients at " +
                             "once, substituting {{firstName}}/{{fullName}} per recipient. " +
                             "Auto-unarchives the thread for a recipient who had archived it " +
                             "(not for a former collaboration). No push notification is sent.";
            s.Responses[StatusCodes.Status200OK] = "Message sent; SentCount is the distinct recipient count.";
            s.Responses[StatusCodes.Status400BadRequest] = "Empty/too-long text, more than 50 recipients, or a " +
                                                             "recipient's substituted text exceeds the storage limit.";
            s.Responses[StatusCodes.Status403Forbidden] = "The subscription does not include messaging (SUBSCRIPTION_REQUIRED).";
            s.Responses[StatusCodes.Status404NotFound] = "One or more recipients are not a live client of the caller.";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(BroadcastMessageRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);

        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var professionalUserId = Guid.Parse(userId);

        if (await this.RespondIfFeatureNotEntitledAsync(
                db, professionalUserId, entitlements => entitlements.CanMessage,
                "The current subscription does not include messaging.", ct))
        {
            return;
        }

        var distinctClientPublicIds = req.ClientPublicIds.Distinct().ToList();

        var clientProfiles = await db.ClientProfiles
            .AsNoTracking()
            .Where(clientProfile => distinctClientPublicIds.Contains(clientProfile.PublicId))
            .Select(clientProfile => new { clientProfile.PublicId, clientProfile.UserId })
            .ToListAsync(ct);

        if (clientProfiles.Count != distinctClientPublicIds.Count)
        {
            await this.SendProblemAsync(
                404, ErrorCodes.BroadcastRecipientNotLinked, "One or more recipients could not be found.", ct);
            return;
        }

        // Deliberately NOT checking LinkCapabilities.CanView* here — a chat message is not plan
        // data, and a link granting neither plan domain can still be messaged one-by-one today.
        var accessibleClientUserIds = (await linkAuthorizationService.GetAccessibleClientsAsync(professionalUserId, ct))
            .Select(client => client.ClientUserId)
            .ToHashSet();

        if (clientProfiles.Any(clientProfile => !accessibleClientUserIds.Contains(clientProfile.UserId)))
        {
            await this.SendProblemAsync(
                404,
                ErrorCodes.BroadcastRecipientNotLinked,
                "One or more recipients are not a live client of the caller.",
                ct);
            return;
        }

        var recipientUserIds = clientProfiles.Select(clientProfile => clientProfile.UserId).ToList();

        var professional = await db.Users.AsNoTracking()
            .Where(user => user.Id == professionalUserId)
            .Select(user => new { user.FirstName, user.LastName })
            .FirstAsync(ct);

        var senderName = FormatFullName(professional.FirstName, professional.LastName);

        var recipients = await db.Users.AsNoTracking()
            .Where(user => recipientUserIds.Contains(user.Id))
            .Select(user => new { user.Id, user.FirstName, user.LastName })
            .ToListAsync(ct);

        // Substitution can only grow the text, so a template that passed the validator's raw-text
        // cap can still overflow chat_messages.text's storage limit once expanded per recipient.
        // Precompute every recipient's substituted text and check the same limit BEFORE the first
        // write — otherwise a length overflow becomes a partial send instead of a bad request.
        var personalizedMessages = recipients
            .Select(recipient => (
                Recipient: recipient,
                Text: SubstituteTemplate(
                    req.Text.Trim(), recipient.FirstName, FormatFullName(recipient.FirstName, recipient.LastName))))
            .ToList();

        if (personalizedMessages.Any(message => message.Text.Length > ChatMessage.MaxTextLength))
        {
            await this.SendProblemAsync(
                400,
                ErrorCodes.BroadcastMessageTooLongAfterSubstitution,
                $"One or more recipients' personalized message exceeds {ChatMessage.MaxTextLength} " +
                "characters after {{firstName}}/{{fullName}} substitution.",
                ct);
            return;
        }

        // No transaction, no per-recipient result list: every id above was authorized before the
        // first write, so a failure partway through this loop is infrastructure, not a bad
        // request. Earlier recipients keep their message and the caller gets a 500; a client
        // retry re-sends to those recipients again (no idempotency key by design).
        foreach (var (recipient, personalizedText) in personalizedMessages)
        {
            var conversation = await conversationSeedService.GetOrSeedConversationAsync(
                professionalUserId,
                recipient.Id,
                professionalUserId,
                senderName,
                personalizedText,
                seedIntoExisting: true,
                ct);

            // Auto-unarchive for the recipient, mirroring SendMessageEndpoint.HandleAsync's
            // auto-unarchive branch — a delivered message should always be visible, not stuck in
            // an archived thread. The coach is always the sender here, so ArchivedByProfessionalAt
            // (the coach's own archive flag) is never touched. A former collaboration is never
            // resurrected.
            if (!conversation.IsFormer && conversation.ArchivedByClientAt is not null)
            {
                conversation.ArchivedByClientAt = null;
                await db.SaveChangesAsync(ct);

                await notifier.NotifyAsync(recipient.Id, "conversationunarchived", new
                {
                    conversationId = conversation.PublicId,
                    isFormer = false,
                }, ct);
            }
        }

        await Send.OkAsync(new BroadcastMessageResponse { SentCount = recipients.Count }, ct);
    }

    private static string FormatFullName(string firstName, string lastName) =>
        string.Join(" ", new[] { firstName, lastName }.Where(part => !string.IsNullOrWhiteSpace(part)));

    private static string SubstituteTemplate(string text, string firstName, string fullName) =>
        TemplatePlaceholderPattern.Replace(text, match => match.Groups[1].Value switch
        {
            "firstName" => firstName,
            "fullName" => fullName,
            _ => match.Value,
        });
}
