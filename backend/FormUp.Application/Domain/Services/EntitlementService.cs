using FormUp.Application.Domain.Constants;
using FormUp.Application.Domain.Enums;
using FormUp.Application.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FormUp.Application.Domain.Services;

/// <summary>
/// Resolves a coach's effective feature entitlements, client-count limit and second-role state from
/// their <see cref="Entities.CoachSubscription"/> and its <see cref="Entities.SubscriptionPlan"/>.
/// </summary>
/// <remarks>
/// A professional with no subscription row gets the Free plan's entitlements. While the clock is before
/// <see cref="Entities.CoachSubscription.FreeUntil"/>, or while the status is Trialing, Active or PastDue,
/// the assigned plan's own flags apply; Canceled and Incomplete fall back to the Free plan. A plan given for
/// free (<see cref="Entities.CoachSubscription.GivenFreeAt"/> set) applies only until its end, whatever the
/// status. After its end the coach must choose a plan; until they do, reads keep resolving to the ended plan's
/// own flags and nobody is over the cap. When the
/// Free plan row itself is missing the service logs an error and resolves to
/// <see cref="CoachEntitlements.Locked"/> (fail closed).
/// </remarks>
public class EntitlementService
{
    private readonly IApplicationDbContext db;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<EntitlementService> logger;

    /// <summary>
    /// Creates the service with the clock and logger it needs.
    /// </summary>
    /// <param name="db">Application database context.</param>
    /// <param name="timeProvider">Clock used to compare against <c>FreeUntil</c>.</param>
    /// <param name="logger">Logger for the missing-Free-plan error.</param>
    public EntitlementService(IApplicationDbContext db, TimeProvider timeProvider, ILogger<EntitlementService> logger)
    {
        this.db = db;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <summary>
    /// Resolves the effective entitlements for a professional.
    /// </summary>
    /// <param name="professionalProfileId">The professional's <c>ProfessionalProfile.Id</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<CoachEntitlements> GetEntitlementsAsync(long professionalProfileId, CancellationToken ct) =>
        (await ResolveAsync(professionalProfileId, ct)).Entitlements;

    /// <summary>
    /// Resolves the effective entitlements for the professional profile of a user. A user with no
    /// professional profile resolves like a coach with no subscription row.
    /// </summary>
    /// <param name="userId">The caller's <c>ApplicationUser.Id</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<CoachEntitlements> GetEntitlementsForUserAsync(Guid userId, CancellationToken ct) =>
        (await ResolveAsync(await FindProfileIdAsync(userId, ct), ct)).Entitlements;

    /// <summary>
    /// Resolves whether a second professional role is available to the professional.
    /// </summary>
    /// <param name="professionalProfileId">The professional's <c>ProfessionalProfile.Id</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<SecondRoleState> GetSecondRoleStateAsync(long professionalProfileId, CancellationToken ct) =>
        ToSecondRoleState(await ResolveAsync(professionalProfileId, ct));

    /// <summary>
    /// Resolves whether a second professional role is available to the user.
    /// </summary>
    /// <param name="userId">The caller's <c>ApplicationUser.Id</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<SecondRoleState> GetSecondRoleStateForUserAsync(Guid userId, CancellationToken ct) =>
        ToSecondRoleState(await ResolveAsync(await FindProfileIdAsync(userId, ct), ct));

    /// <summary>
    /// Resolves, with the same rules as <see cref="GetEntitlementsAsync"/>, which of the given professionals
    /// may use the payments feature.
    /// </summary>
    /// <param name="professionalProfileIds">The professionals' <c>ProfessionalProfile.Id</c> values.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<HashSet<long>> GetPaymentsEntitledProfileIdsAsync(
        IReadOnlyCollection<long> professionalProfileIds, CancellationToken ct)
    {
        var rows = await db.CoachSubscriptions
            .AsNoTracking()
            .Where(cs => professionalProfileIds.Contains(cs.ProfessionalProfileId))
            .Select(ProjectRow())
            .ToListAsync(ct);

        var rowsByProfile = rows.ToDictionary(row => row.ProfessionalProfileId);
        var needsFree = professionalProfileIds.Any(id => !rowsByProfile.TryGetValue(id, out var row) || !UsesAssignedPlan(row));
        var free = needsFree ? await LoadFreePlanAsync(ct) : null;

        return professionalProfileIds
            .Where(id => Resolve(rowsByProfile.GetValueOrDefault(id), free).Entitlements.CanUsePayments)
            .ToHashSet();
    }

    /// <summary>
    /// Counts the professional's active clients: distinct client profiles across all their active links,
    /// so a client linked in both roles counts once. Independent of subscription state.
    /// </summary>
    /// <param name="professionalProfileId">The professional's <c>ProfessionalProfile.Id</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task<int> GetActiveClientCountAsync(long professionalProfileId, CancellationToken ct) =>
        db.ClientProfessionalLinks
            .AsNoTracking()
            .Where(cpl => cpl.ProfessionalProfileId == professionalProfileId && cpl.ConnectionState != ConnectionState.Ended)
            .Select(cpl => cpl.ClientProfileId)
            .Distinct()
            .CountAsync(ct);

    /// <summary>
    /// Whether the professional may take on another active client, given their current entitlements
    /// and distinct active client count.
    /// </summary>
    /// <param name="professionalProfileId">The professional's <c>ProfessionalProfile.Id</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<bool> CanAddClientAsync(long professionalProfileId, CancellationToken ct)
    {
        var entitlements = await GetEntitlementsAsync(professionalProfileId, ct);

        if (entitlements.MaxActiveClients is null)
        {
            return true;
        }

        return await GetActiveClientCountAsync(professionalProfileId, ct) < entitlements.MaxActiveClients;
    }

    /// <summary>
    /// Whether the professional has room for the given client. A client who already has an active link to
    /// the professional takes no new slot, so adding a second role for them is never refused. Pass null
    /// when the client is not known yet (a new invite).
    /// </summary>
    /// <param name="professionalProfileId">The professional's <c>ProfessionalProfile.Id</c>.</param>
    /// <param name="clientProfileId">The client's profile id, or null when there is none yet.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<bool> HasCapacityForClientAsync(
        long professionalProfileId, long? clientProfileId, CancellationToken ct)
    {
        if (clientProfileId is not null
            && await db.ClientProfessionalLinks
                .AsNoTracking()
                .AnyAsync(cpl => cpl.ProfessionalProfileId == professionalProfileId
                                 && cpl.ClientProfileId == clientProfileId
                                 && cpl.ConnectionState != ConnectionState.Ended, ct))
        {
            return true;
        }

        return await CanAddClientAsync(professionalProfileId, ct);
    }

    /// <summary>
    /// Whether the user is a coach whose admin-given plan has ended and who has not chosen a plan yet.
    /// A single query; false for a user with no professional profile or subscription.
    /// </summary>
    /// <param name="userId">The caller's <c>ApplicationUser.Id</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<bool> IsPlanChoiceRequiredForUserAsync(Guid userId, CancellationToken ct)
    {
        var row = await db.CoachSubscriptions
            .AsNoTracking()
            .Where(cs => cs.ProfessionalProfile.UserId == userId)
            .Select(cs => new { cs.GivenFreeAt, cs.FreeUntil })
            .FirstOrDefaultAsync(ct);

        return row is not null
               && CoachSubscriptionStatusClassifier.IsPlanChoiceRequired(row.GivenFreeAt, row.FreeUntil, timeProvider.GetUtcNow());
    }

    /// <summary>
    /// Resolves whether the professional's paid plan has lapsed, whether an ended gift waits for a plan choice,
    /// and which live clients the plan in force keeps and which are over its cap. A lapse uses the Free plan's own
    /// limit; otherwise the cap is the client limit of the plan in force (Free or a smaller paid plan), read from the
    /// data. A plan with more live clients than its cap behaves like a lapse for the over-cap clients but is not
    /// <see cref="KeepClientsState.IsLapsed"/>. While a plan choice is required nobody is over the cap.
    /// </summary>
    /// <param name="professionalProfileId">The professional's <c>ProfessionalProfile.Id</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<KeepClientsState> GetKeepClientsStateAsync(long professionalProfileId, CancellationToken ct)
    {
        var row = await db.CoachSubscriptions
            .AsNoTracking()
            .Where(cs => cs.ProfessionalProfileId == professionalProfileId)
            .Select(cs => new { cs.Status, cs.FreeUntil, cs.GivenFreeAt, PlanMaxActiveClients = cs.SubscriptionPlan != null ? cs.SubscriptionPlan.MaxActiveClients : null })
            .FirstOrDefaultAsync(ct);

        if (row is null)
        {
            return KeepClientsState.NotLapsed;
        }

        if (CoachSubscriptionStatusClassifier.IsPlanChoiceRequired(row.GivenFreeAt, row.FreeUntil, timeProvider.GetUtcNow()))
        {
            var freePlan = await LoadFreePlanAsync(ct);

            return KeepClientsState.NotLapsed with
            {
                IsPlanChoiceRequired = true,
                Cap = freePlan is null ? 0 : freePlan.MaxActiveClients,
            };
        }

        if (IsLapsed(row.Status, row.FreeUntil, row.GivenFreeAt))
        {
            return await GetFreePlanKeepSplitAsync(professionalProfileId, ct);
        }

        // The plan in force: an unlimited plan never has anyone over the cap.
        if (row.PlanMaxActiveClients is not { } planCap)
        {
            return KeepClientsState.NotLapsed;
        }

        var split = await SplitLiveClientsAsync(professionalProfileId, planCap, ct);

        return split.OverCapClientProfileIds.Count == 0 ? KeepClientsState.NotLapsed : split with { IsLapsed = false };
    }

    /// <summary>
    /// Splits the professional's live clients into those the Free plan keeps and those over its cap, whatever the
    /// professional's own plan is. The cap is the Free plan's own limit, read from the data.
    /// </summary>
    /// <param name="professionalProfileId">The professional's <c>ProfessionalProfile.Id</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<KeepClientsState> GetFreePlanKeepSplitAsync(long professionalProfileId, CancellationToken ct)
    {
        var free = await LoadFreePlanAsync(ct);

        // A missing Free plan fails closed: nobody fits under a cap of zero.
        return await SplitLiveClientsAsync(professionalProfileId, free is null ? 0 : free.MaxActiveClients, ct);
    }

    private async Task<KeepClientsState> SplitLiveClientsAsync(long professionalProfileId, int? cap, CancellationToken ct)
    {
        var links = await db.ClientProfessionalLinks
            .AsNoTracking()
            .Where(cpl => cpl.ProfessionalProfileId == professionalProfileId && cpl.ConnectionState != ConnectionState.Ended)
            .Select(cpl => new KeepLinkRow(cpl.Id, cpl.ClientProfileId, cpl.ConnectionState, cpl.ActivatedAtUtc, cpl.DateCreated))
            .ToListAsync(ct);

        return CoachKeepClientsResolver.Resolve(links, cap);
    }

    /// <summary>
    /// Whether the user's professional plan has lapsed and the given client is over the Free plan's cap.
    /// Always false for a user with no professional profile or a client with no profile.
    /// </summary>
    /// <param name="coachUserId">The coach's <c>ApplicationUser.Id</c>.</param>
    /// <param name="clientUserId">The client's <c>ApplicationUser.Id</c> (what Mongo plans store as ClientId).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<bool> IsClientOverCapAsync(Guid coachUserId, Guid clientUserId, CancellationToken ct)
    {
        var overCap = await GetOverCapClientProfileIdsAsync(coachUserId, ct);

        if (overCap.Count == 0)
        {
            return false;
        }

        var clientProfileId = await db.ClientProfiles
            .AsNoTracking()
            .Where(cp => cp.UserId == clientUserId)
            .Select(cp => (long?)cp.Id)
            .FirstOrDefaultAsync(ct);

        return clientProfileId is { } id && overCap.Contains(id);
    }

    /// <summary>
    /// Same as the overload taking the client's user id, for a caller that holds the client's profile id.
    /// </summary>
    /// <param name="coachUserId">The coach's <c>ApplicationUser.Id</c>.</param>
    /// <param name="clientProfileId">The client's <c>ClientProfile.Id</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<bool> IsClientOverCapAsync(Guid coachUserId, long clientProfileId, CancellationToken ct) =>
        (await GetOverCapClientProfileIdsAsync(coachUserId, ct)).Contains(clientProfileId);

    private async Task<IReadOnlyList<long>> GetOverCapClientProfileIdsAsync(Guid coachUserId, CancellationToken ct)
    {
        var professionalProfileId = await FindProfileIdAsync(coachUserId, ct);

        if (professionalProfileId is null)
        {
            return [];
        }

        return (await GetKeepClientsStateAsync(professionalProfileId.Value, ct)).OverCapClientProfileIds;
    }

    private bool IsLapsed(SubscriptionStatus status, DateTimeOffset? freeUntil, DateTimeOffset? givenFreeAt) =>
        !CoachSubscriptionStatusClassifier.PlanApplies(status, freeUntil, givenFreeAt, timeProvider.GetUtcNow());

    private async Task<long?> FindProfileIdAsync(Guid userId, CancellationToken ct) =>
        await db.ProfessionalProfiles
            .AsNoTracking()
            .Where(p => p.UserId == userId)
            .Select(p => (long?)p.Id)
            .FirstOrDefaultAsync(ct);

    private async Task<Resolution> ResolveAsync(long? professionalProfileId, CancellationToken ct)
    {
        SubscriptionRow? row = null;

        if (professionalProfileId is not null)
        {
            row = await db.CoachSubscriptions
                .AsNoTracking()
                .Where(cs => cs.ProfessionalProfileId == professionalProfileId)
                .Select(ProjectRow())
                .FirstOrDefaultAsync(ct);
        }

        var free = row is not null && UsesAssignedPlan(row) ? null : await LoadFreePlanAsync(ct);

        return Resolve(row, free);
    }

    private Resolution Resolve(SubscriptionRow? row, PlanFlags? free)
    {
        if (row is not null && UsesAssignedPlan(row))
        {
            return new Resolution(row.Plan.ToEntitlements(), row.Plan.BothRolesIncluded, row.PaysSecondRoleAddOn);
        }

        return free is null
            ? new Resolution(CoachEntitlements.Locked, false, false)
            : new Resolution(free.ToEntitlements(), free.BothRolesIncluded, false);
    }

    private bool PlanApplies(SubscriptionRow row) =>
        CoachSubscriptionStatusClassifier.PlanApplies(row.Status, row.FreeUntil, row.GivenFreeAt, timeProvider.GetUtcNow());

    private bool UsesAssignedPlan(SubscriptionRow row) =>
        PlanApplies(row)
        || CoachSubscriptionStatusClassifier.IsPlanChoiceRequired(row.GivenFreeAt, row.FreeUntil, timeProvider.GetUtcNow());

    private async Task<PlanFlags?> LoadFreePlanAsync(CancellationToken ct)
    {
        var free = await db.SubscriptionPlans
            .AsNoTracking()
            .Where(p => p.Code == SubscriptionPlanCodes.Free)
            .Select(p => new PlanFlags(
                p.CanCreatePlans,
                p.CanMessage,
                p.CanSendQuestionnaires,
                p.CanUseWeeklyCheckIns,
                p.CanUsePerClientCheckInConfig,
                p.CanUsePayments,
                p.MaxActiveClients,
                p.BothRolesIncluded))
            .FirstOrDefaultAsync(ct);

        if (free is null)
        {
            logger.LogError(
                "The {PlanCode} subscription plan row is missing; coach entitlements fail closed",
                SubscriptionPlanCodes.Free);
        }

        return free;
    }

    private static SecondRoleState ToSecondRoleState(Resolution resolution)
    {
        if (resolution.BothRolesIncluded)
        {
            return SecondRoleState.Included;
        }

        return resolution.PaysSecondRoleAddOn ? SecondRoleState.PaidAddOn : SecondRoleState.NotAvailable;
    }

    private static System.Linq.Expressions.Expression<Func<Entities.CoachSubscription, SubscriptionRow>> ProjectRow() =>
        cs => new SubscriptionRow(
            cs.ProfessionalProfileId,
            cs.Status,
            cs.FreeUntil,
            cs.GivenFreeAt,
            cs.PaysSecondRoleAddOn,
            new PlanFlags(
                cs.SubscriptionPlan.CanCreatePlans,
                cs.SubscriptionPlan.CanMessage,
                cs.SubscriptionPlan.CanSendQuestionnaires,
                cs.SubscriptionPlan.CanUseWeeklyCheckIns,
                cs.SubscriptionPlan.CanUsePerClientCheckInConfig,
                cs.SubscriptionPlan.CanUsePayments,
                cs.SubscriptionPlan.MaxActiveClients,
                cs.SubscriptionPlan.BothRolesIncluded));

    private sealed record PlanFlags(
        bool CanCreatePlans,
        bool CanMessage,
        bool CanSendQuestionnaires,
        bool CanUseWeeklyCheckIns,
        bool CanUsePerClientCheckInConfig,
        bool CanUsePayments,
        int? MaxActiveClients,
        bool BothRolesIncluded)
    {
        public CoachEntitlements ToEntitlements() => new(
            CanCreatePlans,
            CanMessage,
            CanSendQuestionnaires,
            CanUseWeeklyCheckIns,
            CanUsePerClientCheckInConfig,
            CanUsePayments,
            MaxActiveClients);
    }

    private sealed record SubscriptionRow(
        long ProfessionalProfileId,
        SubscriptionStatus Status,
        DateTimeOffset? FreeUntil,
        DateTimeOffset? GivenFreeAt,
        bool PaysSecondRoleAddOn,
        PlanFlags Plan);

    private readonly record struct Resolution(
        CoachEntitlements Entitlements,
        bool BothRolesIncluded,
        bool PaysSecondRoleAddOn);
}
