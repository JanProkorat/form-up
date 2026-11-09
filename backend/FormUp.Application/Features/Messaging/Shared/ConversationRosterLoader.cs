using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using Microsoft.EntityFrameworkCore;
using MongoDB.Driver;

namespace FormUp.Application.Features.Messaging.Shared;

/// <summary>
/// Loads the professional caller's inbox roster: live links only (archived links excluded —
/// the same population <c>GetClientsEndpoint</c> shows on its default tab) AND only clients who
/// already have a conversation with the caller. A linked client with no conversation yet is not
/// an inbox row. Each row is joined with the per-client facts
/// <see cref="ClientRosterFilterClassifier"/> needs, plus the client's conversation for display. Shared by <c>GetConversationsEndpoint</c> and
/// <c>GetConversationFilterCountsEndpoint</c> — the two new actions in this slice — so a chip's
/// membership and its count are always computed from the identical roster.
/// </summary>
public static class ConversationRosterLoader
{
    /// <summary>
    /// A plan's window ending within this many days of "now" counts toward the EndingSoon chip —
    /// same threshold as <c>GetClientsEndpoint</c>.
    /// </summary>
    private const int EndingSoonWindowDays = 14;

    /// <summary>
    /// Loads every live-linked client of <paramref name="professionalUserId"/> that has a
    /// conversation with them (an existing but empty conversation still counts). Independent
    /// SQL passes (links, conversations, check-in responses — each keyed by link or client
    /// so no per-row round trip, mirroring the correlated-subquery cost shape
    /// <c>GetClientsEndpoint.LoadRosterAsync</c> uses) plus two Mongo plan-window projections.
    /// </summary>
    public static async Task<List<ConversationRosterRow>> LoadAsync(
        IApplicationDbContext db,
        IMongoContext mongo,
        long professionalProfileId,
        Guid professionalUserId,
        DateTime now,
        CancellationToken ct)
    {
        var links = await db.ClientProfessionalLinks
            .AsNoTracking()
            .Where(l => l.ProfessionalProfileId == professionalProfileId && l.ConnectionState != ConnectionState.Ended)
            .Select(l => new LinkRow
            {
                LinkId = l.Id,
                ActivatedAtUtc = l.ActivatedAtUtc,
                ClientPublicId = l.ClientProfile.PublicId,
                ClientUserId = l.ClientProfile.User.Id,
                FirstName = l.ClientProfile.User.FirstName,
                LastName = l.ClientProfile.User.LastName,
                Email = l.ClientProfile.User.Email,
                AvatarBlobUrl = l.ClientProfile.User.AvatarBlobUrl,
                CanViewNutritionPlans = l.CanViewNutritionPlans,
                CanViewTrainingPlans = l.CanViewTrainingPlans
            })
            .ToListAsync(ct);

        if (links.Count == 0)
        {
            return [];
        }

        var today = DateOnly.FromDateTime(now);
        var clientUserIds = links.Select(l => l.ClientUserId).ToList();

        var conversationByClient = await db.Conversations
            .AsNoTracking()
            .Where(c => c.ProfessionalUserId == professionalUserId && clientUserIds.Contains(c.ClientUserId))
            .Select(c => new ConversationRow
            {
                ClientUserId = c.ClientUserId,
                PublicId = c.PublicId,
                LastMessageText = c.LastMessageText,
                LastMessageAt = c.LastMessageAt,
                LastMessageSenderId = c.LastMessageSenderId,
                LastMessageHasImage = c.LastMessageHasImage,
                LastMessageEventType = c.LastMessageEventType,
                ArchivedByProfessionalAt = c.ArchivedByProfessionalAt,
                MessageCount = c.Messages.Count,
                UnreadMessageCount = c.Messages.Count(m => !m.IsRead && m.SenderUserId == c.ClientUserId && m.Kind != ChatMessageKind.Event)
            })
            .ToDictionaryAsync(c => c.ClientUserId, ct);

        // A client without a conversation is not an inbox row. Drop them here so the remaining
        // per-client queries only run for the clients that will be returned.
        links = links.Where(l => conversationByClient.ContainsKey(l.ClientUserId)).ToList();

        if (links.Count == 0)
        {
            return [];
        }

        clientUserIds = links.Select(l => l.ClientUserId).ToList();


        // Check-ins are form responses; only the link's current collaboration counts, like GetClientsEndpoint.
        var linkIds = links.Select(l => l.LinkId).ToList();
        var activatedByLink = links.ToDictionary(l => l.LinkId, l => l.ActivatedAtUtc);

        var openCheckIns = (await db.QuestionnaireResponses
            .AsNoTracking()
            .Where(r =>
                r.Kind == QuestionnaireResponseKind.CheckIn &&
                r.ProfessionalId == professionalUserId &&
                linkIds.Contains(r.LinkId) &&
                r.Status != QuestionnaireResponseStatus.Cancelled)
            .Select(r => new
            {
                r.LinkId,
                r.Status,
                r.SubmittedAt,
                r.DateCreated,
                r.CheckInDueAtUtc,
                r.ReviewedByCoachAtUtc
            })
            .ToListAsync(ct))
            .Where(r => activatedByLink[r.LinkId] is not { } activatedAt
                        || (r.Status == QuestionnaireResponseStatus.Submitted ? r.SubmittedAt >= activatedAt : r.DateCreated >= activatedAt))
            .ToList();

        var newCheckInLinkIds = openCheckIns
            .Where(r => r.Status == QuestionnaireResponseStatus.Submitted && r.ReviewedByCoachAtUtc is null)
            .Select(r => r.LinkId)
            .ToHashSet();

        var missingCheckInLinkIds = openCheckIns
            .Where(r => r.Status != QuestionnaireResponseStatus.Submitted && r.CheckInDueAtUtc != null && r.CheckInDueAtUtc < now)
            .Select(r => r.LinkId)
            .ToHashSet();

        var nutritionClientIds = links.Where(l => l.CanViewNutritionPlans).Select(l => l.ClientUserId).Distinct().ToList();
        var trainingClientIds = links.Where(l => l.CanViewTrainingPlans).Select(l => l.ClientUserId).Distinct().ToList();

        var nutritionByClient = await LoadCurrentNutritionPlansAsync(mongo, nutritionClientIds, professionalUserId, today, ct);
        var trainingByClient = await LoadCurrentTrainingPlansAsync(mongo, trainingClientIds, professionalUserId, today, ct);

        return links.Select(l =>
        {
            var conversation = conversationByClient[l.ClientUserId];

            return new ConversationRosterRow
            {
                ClientUserId = l.ClientUserId,
                ClientPublicId = l.ClientPublicId,
                ClientFirstName = l.FirstName,
                ClientLastName = l.LastName,
                ClientEmail = l.Email,
                ClientAvatarBlobUrl = l.AvatarBlobUrl,
                ConversationPublicId = conversation.PublicId,
                LastMessage = conversation.LastMessageText ?? string.Empty,
                LastMessageAt = conversation.LastMessageAt ?? DateTime.MinValue,
                LastMessageSenderId = conversation.LastMessageSenderId,
                LastMessageHasImage = conversation.LastMessageHasImage,
                LastMessageEventType = conversation.LastMessageEventType,
                IsArchivedByProfessional = conversation.ArchivedByProfessionalAt is not null,
                ConversationMessageCount = conversation.MessageCount,
                UnreadMessageCount = conversation.UnreadMessageCount,
                HasNewCheckIn = newCheckInLinkIds.Contains(l.LinkId),
                HasMissingCheckIn = missingCheckInLinkIds.Contains(l.LinkId),
                IsEndingSoon =
                    (l.CanViewNutritionPlans && nutritionByClient.TryGetValue(l.ClientUserId, out var nutritionPlan) &&
                     IsEndingWithinWindow(nutritionPlan, today)) ||
                    (l.CanViewTrainingPlans && trainingByClient.TryGetValue(l.ClientUserId, out var trainingPlan) &&
                     IsEndingWithinWindow(trainingPlan, today))
            };
        }).ToList();
    }

    private static async Task<Dictionary<Guid, PlanWindowProjection>> LoadCurrentNutritionPlansAsync(
        IMongoContext mongo, List<Guid> clientUserIds, Guid professionalUserId, DateOnly today, CancellationToken ct)
    {
        if (clientUserIds.Count == 0)
        {
            return [];
        }

        var filter = Builders<NutritionPlan>.Filter.And(
            Builders<NutritionPlan>.Filter.In(p => p.ClientId, clientUserIds),
            Builders<NutritionPlan>.Filter.Eq(p => p.NutritionistId, professionalUserId),
            Builders<NutritionPlan>.Filter.Eq(p => p.Status, NutritionPlanStatus.Active));

        var candidates = await mongo.NutritionPlans
            .Find(filter)
            .Project(p => new PlanWindowProjection { ClientId = p.ClientId, StartDate = p.StartDate, WeekCount = p.Weeks.Count })
            .ToListAsync(ct);

        return ResolveCurrentPlanByClient(candidates, today);
    }

    private static async Task<Dictionary<Guid, PlanWindowProjection>> LoadCurrentTrainingPlansAsync(
        IMongoContext mongo, List<Guid> clientUserIds, Guid professionalUserId, DateOnly today, CancellationToken ct)
    {
        if (clientUserIds.Count == 0)
        {
            return [];
        }

        var filter = Builders<TrainingPlan>.Filter.And(
            Builders<TrainingPlan>.Filter.In(p => p.ClientId, clientUserIds),
            Builders<TrainingPlan>.Filter.Eq(p => p.TrainerId, professionalUserId),
            Builders<TrainingPlan>.Filter.Eq(p => p.Status, TrainingPlanStatus.Active));

        var candidates = await mongo.TrainingPlans
            .Find(filter)
            .Project(p => new PlanWindowProjection { ClientId = p.ClientId, StartDate = p.StartDate, WeekCount = p.Weeks.Count })
            .ToListAsync(ct);

        return ResolveCurrentPlanByClient(candidates, today);
    }

    /// <summary>
    /// Groups Active-status plan candidates (authored by the caller — another coach's plan never
    /// feeds this roster) by client and keeps only the one (if any) whose
    /// window contains <paramref name="today"/>, via
    /// <see cref="PlanWindowResolver.ResolveCurrentPlanStrict{T}"/> — same strict (no legacy
    /// unranged fallback) resolution <c>GetClientsEndpoint</c> uses, so the two surfaces can
    /// never disagree about which plan is current.
    /// </summary>
    private static Dictionary<Guid, PlanWindowProjection> ResolveCurrentPlanByClient(
        List<PlanWindowProjection> candidates, DateOnly today)
    {
        var result = new Dictionary<Guid, PlanWindowProjection>();

        foreach (var group in candidates.GroupBy(p => p.ClientId))
        {
            var current = PlanWindowResolver.ResolveCurrentPlanStrict(
                group, p => p.StartDate, p => p.WeekCount, today);

            if (current is not null)
            {
                result[group.Key] = current;
            }
        }

        return result;
    }

    /// <summary>
    /// <paramref name="plan"/> is guaranteed to carry a non-null <c>StartDate</c> — only
    /// in-window plans resolved by <see cref="ResolveCurrentPlanByClient"/> ever reach here.
    /// </summary>
    private static bool IsEndingWithinWindow(PlanWindowProjection plan, DateOnly today)
    {
        var end = DateOnly.FromDateTime(plan.StartDate!.Value).AddDays(plan.WeekCount * 7);
        return end.DayNumber - today.DayNumber <= EndingSoonWindowDays;
    }

    /// <summary>One row of the caller's link roster, projected once from SQL.</summary>
    private sealed class LinkRow
    {
        public long LinkId { get; init; }
        public DateTime? ActivatedAtUtc { get; init; }
        public Guid ClientPublicId { get; init; }
        public Guid ClientUserId { get; init; }
        public string FirstName { get; init; } = string.Empty;
        public string LastName { get; init; } = string.Empty;
        public string? Email { get; init; }
        public string? AvatarBlobUrl { get; init; }
        public bool CanViewNutritionPlans { get; init; }
        public bool CanViewTrainingPlans { get; init; }
    }

    /// <summary>The caller's existing conversation with one roster client, if any, keyed by that client.</summary>
    private sealed class ConversationRow
    {
        public Guid ClientUserId { get; init; }
        public Guid PublicId { get; init; }
        public string? LastMessageText { get; init; }
        public DateTime? LastMessageAt { get; init; }
        public Guid? LastMessageSenderId { get; init; }
        public bool LastMessageHasImage { get; init; }
        public ChatEventType? LastMessageEventType { get; init; }
        public DateTime? ArchivedByProfessionalAt { get; init; }
        public int MessageCount { get; init; }
        public int UnreadMessageCount { get; init; }
    }

    /// <summary>A projected Mongo plan candidate — just enough to resolve the current in-window plan.</summary>
    private sealed class PlanWindowProjection
    {
        public Guid ClientId { get; init; }
        public DateTime? StartDate { get; init; }
        public int WeekCount { get; init; }
    }
}
