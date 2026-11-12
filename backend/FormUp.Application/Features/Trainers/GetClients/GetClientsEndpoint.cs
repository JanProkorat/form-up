using System.Security.Claims;
using FastEndpoints;
using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Documents;
using FormUp.Application.Domain.Entities;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Domain.Services;
using FormUp.Application.Infrastructure.Data;
using FormUp.Application.Infrastructure.Data.MongoDb;
using Microsoft.EntityFrameworkCore;
using MongoDB.Driver;

namespace FormUp.Application.Features.Trainers.GetClients;

/// <summary>
/// Endpoint for retrieving the list of clients managed by the authenticated trainer.
/// </summary>
/// <remarks>
/// <para>
/// Active/Paused status is derived per request from a Mongo plan lookup, so pagination cannot
/// happen in SQL: the endpoint reads the caller's WHOLE link roster (with <c>search</c>/
/// <c>tagIds</c> already applied at the SQL level), resolves each client's current plan per
/// visible domain from Mongo, classifies each row, then filters/sorts/paginates in memory. Cost
/// is O(roster size) per request, not O(page size) — the honest price of a status that is never
/// stored.
/// </para>
/// <para>
/// Round trips per request: 1 SQL profile lookup, 1 SQL roster+facts pass, 2 Mongo plan
/// projections (nutrition, training), 1 SQL tag lookup, and 2 lightweight SQL COUNTs for the
/// Pending tab count — 7 total, all independent of roster size and page size.
/// </para>
/// <para>
/// Ambiguities pinned for this endpoint (issue #1064): (1) <see cref="ClientTabCounts.Pending"/>
/// is not narrowed by <c>search</c>/<c>tagIds</c> — those describe existing linked clients, and
/// pending rows are not clients yet; (2) <see cref="ClientFilterCounts"/> apply <c>search</c> and
/// <c>tagIds</c> but never the requested filter chip itself, so a zero-count chip can grey out on
/// the client without losing the ability to select it; (3) the check-in chips
/// (<see cref="ClientListFilter.NewCheckIns"/> / <see cref="ClientListFilter.MissingCheckIns"/>)
/// are scoped per client to the professions the caller's link actually grants, consistent with
/// plan-domain gating — a dual-role coach can have two check-in rows per week for one client.
/// </para>
/// </remarks>
/// <param name="mongo">MongoDB context.</param>
/// <param name="db">Database context.</param>
/// <param name="timeProvider">Clock abstraction — lets tests pin "now" deterministically.</param>
/// <param name="entitlementService">Resolves which live clients are over the cap of the plan in force.</param>
public class GetClientsEndpoint(
    IMongoContext mongo, IApplicationDbContext db, TimeProvider timeProvider, EntitlementService entitlementService)
    : Endpoint<GetClientsRequest, GetClientsResponse>
{
    /// <summary>
    /// A plan's window ending within this many days of "now" counts toward the EndingSoon chip.
    /// </summary>
    private const int EndingSoonWindowDays = 14;

    /// <inheritdoc />
    public override void Configure()
    {
        Get("/trainer/clients");
        Roles(AppRoles.Trainer, AppRoles.Nutritionist);
        Summary(s =>
        {
            s.Summary = "Get trainer's clients";
            s.Description =
                "Returns a paginated list of clients managed by the authenticated trainer or " +
                "nutritionist, with derived status tabs, tag/chip filters, and per-tab/per-chip counts.";
        });
    }

    /// <inheritdoc />
    public override async Task HandleAsync(GetClientsRequest req, CancellationToken ct)
    {
        var userId = User.FindFirstValue(AppClaims.UserId);

        if (userId is null)
        {
            await Send.UnauthorizedAsync(ct);
            return;
        }

        var professionalProfile = await db.ProfessionalProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(tp => tp.UserId == Guid.Parse(userId), ct);

        if (professionalProfile is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(now);

        var roster = await LoadRosterAsync(professionalProfile.Id, professionalProfile.UserId, req, now, ct);

        var nutritionClientIds = roster.Where(r => r.IsActive && r.CanViewNutritionPlans).Select(r => r.ClientUserId).Distinct().ToList();
        var trainingClientIds = roster.Where(r => r.IsActive && r.CanViewTrainingPlans).Select(r => r.ClientUserId).Distinct().ToList();

        var nutritionByClient = await LoadCurrentNutritionPlansAsync(nutritionClientIds, professionalProfile.UserId, today, ct);
        var trainingByClient = await LoadCurrentTrainingPlansAsync(trainingClientIds, professionalProfile.UserId, today, ct);

        var tagsByLinkId = await LoadTagsByLinkIdAsync(roster, professionalProfile.Id, ct);

        // Same source as the CLIENT_OVER_PLAN_LIMIT gate, so the flag and the gate never disagree.
        var overCapClientIds = (await entitlementService.GetKeepClientsStateAsync(professionalProfile.Id, ct))
            .OverCapClientProfileIds
            .ToHashSet();

        var classified = roster
            .Select(row => Classify(row, nutritionByClient, trainingByClient, today, overCapClientIds))
            .ToList();

        var pendingCount = await CountPendingAsync(professionalProfile.Id, now, ct);

        var tabCounts = new ClientTabCounts
        {
            Active = classified.Count(r => r.Status == ClientListStatus.Active),
            Paused = classified.Count(r => r.Status == ClientListStatus.Paused),
            Archived = classified.Count(r => r.Status == ClientListStatus.Archived),
            OutsidePlan = classified.Count(r => r.IsOutsidePlan),
            Pending = pendingCount
        };

        // Omitted status means every live link — Active and Paused combined, Archived excluded —
        // matching this endpoint's pre-existing behaviour before status tabs existed.
        var tabFiltered = req.Status is null
            ? classified.Where(r => r.Status != ClientListStatus.Archived).ToList()
            : classified.Where(r => r.Status == req.Status.Value).ToList();

        if (req.OutsidePlan == true)
        {
            tabFiltered = tabFiltered.Where(r => r.IsOutsidePlan).ToList();
        }

        var counts = ClientRosterFilterClassifier.ComputeCounts(
            tabFiltered,
            r => r.Row.UnreadMessageCount > 0,
            r => r.Row.ConversationMessageCount == 0,
            r => r.Row.HasNewCheckIn,
            r => r.Row.HasMissingCheckIn,
            r => r.IsEndingSoon);

        var filterCounts = new ClientFilterCounts
        {
            All = counts.All,
            UnreadMessages = counts.UnreadMessages,
            NoMessages = counts.NoMessages,
            NewCheckIns = counts.NewCheckIns,
            MissingCheckIns = counts.MissingCheckIns,
            EndingSoon = counts.EndingSoon
        };

        var chipFiltered = ApplyChipFilter(tabFiltered, req.Filter);

        var page = chipFiltered
            .OrderByDescending(r => r.Row.LinkedAt)
            .Skip((req.Page - 1) * req.PageSize)
            .Take(req.PageSize)
            .Select(r => BuildSummary(r, tagsByLinkId))
            .ToList();

        await Send.OkAsync(new GetClientsResponse
        {
            Clients = page,
            TotalCount = chipFiltered.Count,
            Page = req.Page,
            PageSize = req.PageSize,
            TabCounts = tabCounts,
            FilterCounts = filterCounts
        }, ct);
    }

    private static List<ClassifiedRow> ApplyChipFilter(List<ClassifiedRow> rows, ClientListFilter? filter) =>
        rows.Where(r => ClientRosterFilterClassifier.Matches(
            filter,
            r.Row.UnreadMessageCount > 0,
            r.Row.ConversationMessageCount == 0,
            r.Row.HasNewCheckIn,
            r.Row.HasMissingCheckIn,
            r.IsEndingSoon)).ToList();

    /// <summary>
    /// Single SQL pass over the caller's whole link roster (no <c>Include</c> of full plan
    /// graphs — those live in Mongo). Search and tag filters are applied here, at the SQL level;
    /// the per-client facts (unread messages, conversation size, check-in flags) are correlated
    /// subqueries folded into the same query rather than a per-client round trip.
    /// </summary>
    private async Task<List<RosterRow>> LoadRosterAsync(
        long professionalProfileId, Guid callerUserId, GetClientsRequest req, DateTime now, CancellationToken ct)
    {
        var query = db.ClientProfessionalLinks
            .AsNoTracking()
            .Where(l => l.ProfessionalProfileId == professionalProfileId);

        if (!string.IsNullOrWhiteSpace(req.Search))
        {
            var search = req.Search.ToLower();
            query = query.Where(l =>
                l.ClientProfile.User.FirstName.ToLower().Contains(search) ||
                l.ClientProfile.User.LastName.ToLower().Contains(search) ||
                l.ClientProfile.User.Email!.ToLower().Contains(search));
        }

        if (req.TagIds.Count > 0)
        {
            query = query.Where(l => db.ClientTagAssignments.Any(a =>
                a.ClientProfessionalLinkId == l.Id &&
                req.TagIds.Contains(a.ClientTag.PublicId) &&
                a.ClientTag.OwnerProfessionalProfileId == professionalProfileId));
        }

        return await query
            .Select(l => new RosterRow
            {
                LinkId = l.Id,
                ClientProfileId = l.ClientProfileId,
                ClientPublicId = l.ClientProfile.PublicId,
                ClientUserId = l.ClientProfile.User.Id,
                Email = l.ClientProfile.User.Email!,
                FirstName = l.ClientProfile.User.FirstName,
                LastName = l.ClientProfile.User.LastName,
                AvatarBlobUrl = l.ClientProfile.User.AvatarBlobUrl,
                IsActive = l.ConnectionState != ConnectionState.Ended,
                LinkedAt = l.DateCreated,
                CanViewNutritionPlans = l.CanViewNutritionPlans,
                CanViewTrainingPlans = l.CanViewTrainingPlans,
                ConversationMessageCount = db.Conversations
                    .Where(c => c.ProfessionalUserId == callerUserId && c.ClientUserId == l.ClientProfile.User.Id)
                    .Select(c => c.Messages.Count)
                    .FirstOrDefault(),
                UnreadMessageCount = db.ChatMessages.Count(m =>
                    m.Conversation.ProfessionalUserId == callerUserId &&
                    m.Conversation.ClientUserId == l.ClientProfile.User.Id &&
                    !m.IsRead &&
                    m.Kind != ChatMessageKind.Event &&
                    m.SenderUserId == l.ClientProfile.User.Id),
                // Check-ins are form responses of the link's current collaboration; the open ones are
                // unsubmitted, so they count by creation time, the submitted ones by submission time.
                HasNewCheckIn = db.QuestionnaireResponses.Any(r =>
                    r.LinkId == l.Id &&
                    r.ProfessionalId == callerUserId &&
                    r.Kind == QuestionnaireResponseKind.CheckIn &&
                    r.Status == QuestionnaireResponseStatus.Submitted &&
                    r.ReviewedByCoachAtUtc == null &&
                    (l.ActivatedAtUtc == null || r.SubmittedAt >= l.ActivatedAtUtc)),
                HasMissingCheckIn = db.QuestionnaireResponses.Any(r =>
                    r.LinkId == l.Id &&
                    r.ProfessionalId == callerUserId &&
                    r.Kind == QuestionnaireResponseKind.CheckIn &&
                    (r.Status == QuestionnaireResponseStatus.Pending || r.Status == QuestionnaireResponseStatus.InProgress) &&
                    r.CheckInDueAtUtc != null && r.CheckInDueAtUtc < now &&
                    (l.ActivatedAtUtc == null || r.DateCreated >= l.ActivatedAtUtc))
            })
            .ToListAsync(ct);
    }

    private async Task<Dictionary<Guid, PlanWindowProjection>> LoadCurrentNutritionPlansAsync(
        List<Guid> clientUserIds, Guid professionalUserId, DateOnly today, CancellationToken ct)
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
            .Project(p => new PlanWindowProjection
            {
                ClientId = p.ClientId,
                Name = p.Name,
                StartDate = p.StartDate,
                WeekCount = p.Weeks.Count
            })
            .ToListAsync(ct);

        return ResolveCurrentPlanByClient(candidates, today);
    }

    private async Task<Dictionary<Guid, PlanWindowProjection>> LoadCurrentTrainingPlansAsync(
        List<Guid> clientUserIds, Guid professionalUserId, DateOnly today, CancellationToken ct)
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
            .Project(p => new PlanWindowProjection
            {
                ClientId = p.ClientId,
                Name = p.Name,
                StartDate = p.StartDate,
                WeekCount = p.Weeks.Count
            })
            .ToListAsync(ct);

        return ResolveCurrentPlanByClient(candidates, today);
    }

    /// <summary>
    /// Groups Active-status plan candidates by client and keeps only the one (if any) whose
    /// window contains <paramref name="today"/>, via
    /// <see cref="PlanWindowResolver.ResolveCurrentPlanStrict{T}"/>. Deliberately NOT
    /// <see cref="PlanWindowResolver.ResolveCurrentPlan{T}"/> — that method's single-candidate
    /// legacy fallback would classify an unranged (no <c>StartDate</c>) plan as current. Here a
    /// plan with no <c>StartDate</c> never counts as Active, regardless of how many candidates a
    /// client has. <c>GetClientDashboardEndpoint</c> shares this same strict resolution for its
    /// own status inputs so the two endpoints can never disagree (#1094).
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

    private async Task<Dictionary<long, List<ClientTagSummaryDto>>> LoadTagsByLinkIdAsync(
        List<RosterRow> roster, long professionalProfileId, CancellationToken ct)
    {
        if (roster.Count == 0)
        {
            return [];
        }

        var linkIds = roster.Select(r => r.LinkId).ToList();

        var assignments = await db.ClientTagAssignments
            .AsNoTracking()
            .Where(a => linkIds.Contains(a.ClientProfessionalLinkId) &&
                        a.ClientTag.OwnerProfessionalProfileId == professionalProfileId)
            .Select(a => new
            {
                a.ClientProfessionalLinkId,
                TagId = a.ClientTag.PublicId,
                a.ClientTag.Name,
                a.ClientTag.ColorHex
            })
            .ToListAsync(ct);

        return assignments
            .GroupBy(a => a.ClientProfessionalLinkId)
            .ToDictionary(
                g => g.Key,
                g => g
                    .Select(a => new ClientTagSummaryDto { TagId = a.TagId, Name = a.Name, ColorHex = a.ColorHex })
                    .OrderBy(t => t.Name)
                    .ToList());
    }

    private async Task<int> CountPendingAsync(long professionalProfileId, DateTime now, CancellationToken ct)
    {
        // Same one-row-per-email rule as the Pending tab, so the badge matches the rows (#1541).
        var invites = await db.PendingInvites
            .AsNoTracking()
            .Where(pi => pi.ProfessionalProfileId == professionalProfileId && pi.Status == PendingInviteStatus.Pending)
            .Select(pi => new { pi.Email, pi.SentAt })
            .ToListAsync(ct);
        var inviteCount = PendingInviteStatusResolver.OnePerEmail(invites, i => i.Email, i => i.SentAt, now).Count;

        var requestCount = await db.ClientRequests
            .AsNoTracking()
            .CountAsync(r => r.ProfessionalProfileId == professionalProfileId
                && (r.Status == ClientRequestStatus.Pending || r.Status == ClientRequestStatus.PackageProposed), ct);

        return inviteCount + requestCount;
    }

    private static ClassifiedRow Classify(
        RosterRow row,
        Dictionary<Guid, PlanWindowProjection> nutritionByClient,
        Dictionary<Guid, PlanWindowProjection> trainingByClient,
        DateOnly today,
        HashSet<long> overCapClientIds)
    {
        var activePlans = new List<ClientActivePlanDto>();
        var isEndingSoon = false;

        PlanWindowProjection? nutritionPlan = null;
        var hasActiveNutritionPlan = row.IsActive && row.CanViewNutritionPlans &&
            nutritionByClient.TryGetValue(row.ClientUserId, out nutritionPlan);

        if (hasActiveNutritionPlan)
        {
            activePlans.Add(new ClientActivePlanDto
            {
                Name = nutritionPlan!.Name,
                Type = Profession.Nutrition,
                StartDate = nutritionPlan.StartDate
            });

            if (IsEndingWithinWindow(nutritionPlan, today))
            {
                isEndingSoon = true;
            }
        }

        PlanWindowProjection? trainingPlan = null;
        var hasActiveTrainingPlan = row.IsActive && row.CanViewTrainingPlans &&
            trainingByClient.TryGetValue(row.ClientUserId, out trainingPlan);

        if (hasActiveTrainingPlan)
        {
            activePlans.Add(new ClientActivePlanDto
            {
                Name = trainingPlan!.Name,
                Type = Profession.Training,
                StartDate = trainingPlan.StartDate
            });

            if (IsEndingWithinWindow(trainingPlan, today))
            {
                isEndingSoon = true;
            }
        }

        var status = ClientStatusClassifier.Classify(
            row.IsActive,
            new LinkCapabilities(row.CanViewNutritionPlans, row.CanViewTrainingPlans),
            hasActiveNutritionPlan,
            hasActiveTrainingPlan);

        var isOutsidePlan = row.IsActive && overCapClientIds.Contains(row.ClientProfileId);

        return new ClassifiedRow(
            row, status, hasActiveNutritionPlan, hasActiveTrainingPlan, isEndingSoon, activePlans, isOutsidePlan);
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

    private static ClientSummary BuildSummary(ClassifiedRow r, Dictionary<long, List<ClientTagSummaryDto>> tagsByLinkId) =>
        new()
        {
            PublicId = r.Row.ClientPublicId,
            UserId = r.Row.ClientUserId,
            Email = r.Row.Email,
            FirstName = r.Row.FirstName,
            LastName = r.Row.LastName,
            IsActive = r.Row.IsActive,
            LinkedAt = r.Row.LinkedAt,
            Status = r.Status,
            AvatarBlobUrl = r.Row.AvatarBlobUrl,
            Tags = tagsByLinkId.TryGetValue(r.Row.LinkId, out var tags) ? tags : [],
            UnreadMessageCount = r.Row.UnreadMessageCount,
            HasActiveNutritionPlan = r.HasActiveNutritionPlan,
            HasActiveTrainingPlan = r.HasActiveTrainingPlan,
            ActivePlans = r.ActivePlans,
            IsOutsidePlan = r.IsOutsidePlan
        };

    /// <summary>
    /// One row of the caller's link roster, projected once from SQL. Everything here is either a
    /// static column or a correlated-subquery fact — no plan data, which lives in Mongo.
    /// </summary>
    private sealed class RosterRow
    {
        public long LinkId { get; init; }
        public long ClientProfileId { get; init; }
        public Guid ClientPublicId { get; init; }
        public Guid ClientUserId { get; init; }
        public string Email { get; init; } = string.Empty;
        public string FirstName { get; init; } = string.Empty;
        public string LastName { get; init; } = string.Empty;
        public string? AvatarBlobUrl { get; init; }
        public bool IsActive { get; init; }
        public DateTime LinkedAt { get; init; }
        public bool CanViewNutritionPlans { get; init; }
        public bool CanViewTrainingPlans { get; init; }
        public int ConversationMessageCount { get; init; }
        public int UnreadMessageCount { get; init; }
        public bool HasNewCheckIn { get; init; }
        public bool HasMissingCheckIn { get; init; }
    }

    /// <summary>
    /// A projected Mongo plan candidate — just enough to resolve the current in-window plan and
    /// surface it on the hover popover.
    /// </summary>
    private sealed class PlanWindowProjection
    {
        public Guid ClientId { get; init; }
        public string Name { get; init; } = string.Empty;
        public DateTime? StartDate { get; init; }
        public int WeekCount { get; init; }
    }

    private sealed record ClassifiedRow(
        RosterRow Row,
        ClientListStatus Status,
        bool HasActiveNutritionPlan,
        bool HasActiveTrainingPlan,
        bool IsEndingSoon,
        List<ClientActivePlanDto> ActivePlans,
        bool IsOutsidePlan);
}
